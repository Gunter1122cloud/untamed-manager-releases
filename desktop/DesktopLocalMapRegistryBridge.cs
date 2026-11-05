using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Two-step local ASA installation registration; writes the Manager's own
/// profile only, never ASA saves, binaries, INI, RCON or Store. A fresh
/// SteamCMD download is deliberately a separate operational workflow.
/// </summary>
public sealed class DesktopLocalMapRegistryBridge : IDisposable
{
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<Guid,PreparedImport> prepared=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record PreparedImport(Guid Token,string Name,string Root,
        string ServerExe,DateTimeOffset CreatedUtc);
    public DesktopLocalMapRegistryBridge(ClusterService current)=>
        cluster=current??throw new ArgumentNullException(nameof(current));

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Failure(null,"UM-INVALID-MESSAGE");
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            if(data.ValueKind!=JsonValueKind.Object ||
               !GuidField(data,"id",out var requestId) ||
               !data.TryGetProperty("method",out var methodValue) ||
               methodValue.ValueKind!=JsonValueKind.String)
                return Failure(null,"UM-INVALID-MESSAGE");
            id=requestId.ToString("D");
            string? method=methodValue.GetString();
            if(method is not ("servers.inspectLocal" or "servers.importLocal"))
                return Failure(id,"UM-METHOD-DENIED");

            await gate.WaitAsync();
            try
            {
                foreach(var expired in prepared.Where(x=>
                    DateTimeOffset.UtcNow-x.Value.CreatedUtc>TimeSpan.FromMinutes(5))
                    .Select(x=>x.Key).ToArray())prepared.Remove(expired);
                if(method=="servers.inspectLocal")
                {
                    if(!StringField(data,"name",100,out var requestedName) ||
                       !StringField(data,"root",520,out var requestedRoot))
                        return Failure(id,"UM-INVALID-IMPORT");
                    var name=LocalMapNameRules.Normalize(requestedName);
                    var root=ValidateFixedLocalRoot(requestedRoot);
                    var found=LocalAsaInstallLocator.Probe(root);
                    if(!found.HasServerExecutable)
                        return Failure(id,"UM-ASA-EXE-MISSING");
                    RequireUnique(name,root);
                    if(prepared.Count>=5)prepared.Clear();
                    var token=Guid.NewGuid();
                    prepared[token]=new(token,name,root,found.ServerExecutable!,
                        DateTimeOffset.UtcNow);
                    return Success(id,new {
                        importToken=token,name,folderName=Path.GetFileName(root),
                        exeFound=true,apiLoaderDetected=found.HasApiLoader,
                        portsConfigured=false,evidence="LOCAL_BINARY_LAYOUT_PROBED_NO_MUTATION"
                    });
                }

                if(!GuidField(data,"importToken",out var tokenId) ||
                   !prepared.Remove(tokenId,out var preview))
                    return Failure(id,"UM-IMPORT-EXPIRED");
                var checkedRoot=ValidateFixedLocalRoot(preview.Root);
                var checkedExe=LocalAsaInstallLocator.Probe(checkedRoot).ServerExecutable;
                if(!string.Equals(checkedExe,preview.ServerExe,
                    StringComparison.OrdinalIgnoreCase))
                    return Failure(id,"UM-IMPORT-CHANGED");
                RequireUnique(preview.Name,checkedRoot);
                var map=new LocalMap{
                    Id=Guid.NewGuid(),Name=preview.Name,
                    InstallationPath=checkedRoot,ExecutablePath=checkedExe!,
                    // No ports, RCON secrets or fabricated startup command.
                    Arguments="",GamePort=0,QueryPort=0,RconPort=0,
                    StartWithAsaApi=false,AutoStart=false
                };
                cluster.AddMap(map); // Existing atomic Manager ConfigStore CAS write.
                return Success(id,new{
                    mapId=map.Id,mapName=map.Name,exeRegistered=true,
                    needsConfiguration=true,evidence="MANAGER_PROFILE_ONLY_NO_ASA_CHANGE"
                });
            }
            finally {gate.Release();}
        }
        catch(DirectoryNotFoundException){return Failure(id,"UM-IMPORT-DIRECTORY-MISSING");}
        catch(IOException){return Failure(id,"UM-IMPORT-IO-OR-CONFLICT");}
        catch(Exception){return Failure(id,"UM-IMPORT-VALIDATION-FAILED");}
    }

    private void RequireUnique(string name,string root)
    {
        if(cluster.Config.LocalMaps.Any(m=>
            string.Equals(m.Name,name,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Duplicate local map name");
        foreach(var existing in cluster.Config.LocalMaps)
        {
            if(string.IsNullOrWhiteSpace(existing.InstallationPath))continue;
            string owner=Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing.InstallationPath));
            if(root.Equals(owner,StringComparison.OrdinalIgnoreCase) ||
               root.StartsWith(owner+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
               owner.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing Manager installation overlaps new import");
        }
    }
    private static string ValidateFixedLocalRoot(string userPath)
    {
        if(string.IsNullOrWhiteSpace(userPath)||userPath.Length>520 ||
           !Path.IsPathFullyQualified(userPath) ||
           userPath.StartsWith(@"\\",StringComparison.Ordinal) ||
           userPath.StartsWith("//",StringComparison.Ordinal) ||
           userPath.Contains('~'))
            throw new InvalidDataException("Only explicit local Windows installation path is supported");
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(userPath));
        if(!Directory.Exists(root))throw new DirectoryNotFoundException();
        var drive=new DriveInfo(Path.GetPathRoot(root)!);
        if(drive.DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Fixed local drive is required");
        for(DirectoryInfo? node=new(root);node!=null;node=node.Parent)
            if(node.Exists && (node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked installation directory refused");
        return root;
    }
    private static bool GuidField(JsonElement root,string key,out Guid value)
    {
        value=Guid.Empty;
        return root.TryGetProperty(key,out var element) &&
            element.ValueKind==JsonValueKind.String &&
            Guid.TryParse(element.GetString(),out value) && value!=Guid.Empty;
    }
    private static bool StringField(JsonElement root,string key,int max,out string value)
    {
        value="";
        if(!root.TryGetProperty(key,out var element) ||
           element.ValueKind!=JsonValueKind.String)return false;
        value=element.GetString()?.Trim()??"";
        return value.Length is >0 and <=520 && value.Length<=max &&
            !value.Contains('\0') && !value.Any(char.IsControl);
    }
    private static string Success(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Failure(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>gate.Dispose();
}
