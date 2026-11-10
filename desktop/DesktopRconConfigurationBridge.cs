using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit stopped-map RCON setup. Existing ASA INI is verified byte-for-byte
/// as configuration evidence, NEVER overwritten. A brand-new installation
/// can create the first GUS.ini exactly once via AsaInitialRconSettings.
/// Historical Store-v1 and active maps remain immutable. Plaintext passwords
/// are never returned to React, logged or persisted in the Manager profile.
/// </summary>
public sealed class DesktopRconConfigurationBridge : IDisposable
{
    private sealed record Pending(Guid Token,Guid MapId,string BaselineArguments,
        string Installation,string Executable,int BaselineRcon,
        string PasswordProtected,int Port,bool CreateIni,string IniDigest,
        DateTimeOffset Created);
    private readonly ClusterService cluster;
    private readonly SemaphoreSlim serial=new(1,1);
    private Pending? pending;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopRconConfigurationBridge(ClusterService service) =>
        cluster=service??throw new ArgumentNullException(nameof(service));

    private LocalMap? Local(Guid id)=>
        cluster.Config.LocalMaps.SingleOrDefault(m=>m.Id==id);

    private static bool SafeMap(LocalMap m)=>
        !m.HistoricalStoreProtected && !m.ObservedReadOnlyImported && !m.Maintenance &&
        m.GamePort>0 && m.QueryPort>0 && !string.IsNullOrWhiteSpace(m.Arguments) &&
        !m.Arguments.Contains("-usestore",StringComparison.OrdinalIgnoreCase) &&
        !m.Arguments.Contains("converttostore",StringComparison.OrdinalIgnoreCase) &&
        !m.Arguments.Contains("RCONPort",StringComparison.OrdinalIgnoreCase) &&
        !m.Arguments.Contains("ServerAdminPassword",StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(m.StoreTestRoot) &&
        string.IsNullOrWhiteSpace(m.RconPasswordProtected) && m.RconPort==0;

    private bool Stopped(LocalMap map)
    {
        var observed=cluster.Check(map);
        return observed.ConfirmedStopped && !observed.Running &&
               observed.Identity==null && observed.LoaderIdentity==null;
    }

    private static string IniPath(LocalMap map)
    {
        string path=AsaInitialRconSettings.NewIniPath(map.InstallationPath);
        if(!string.Equals(Path.GetFullPath(map.ExecutablePath),
            Path.GetFullPath(Path.Combine(map.InstallationPath,"ShooterGame",
                "Binaries","Win64","ArkAscendedServer.exe")),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-RCON-EXE-IDENTITY");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-RCON-INI-REPARSE");
        return path;
    }

    private static string Digest(string path)
    {
        if(!File.Exists(path))return "";
        var file=new FileInfo(path);
        if(file.Length is <1 or >131072)
            throw new InvalidDataException("UM-RCON-INI-SIZE");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(path)));
    }

    private static void VerifyExisting(string path,int port,string password)
    {
        if(!File.Exists(path))throw new InvalidDataException("UM-RCON-INI-ABSENT");
        var lines=File.ReadAllLines(path);
        bool section=false;
        int enabled=0,ports=0,passwords=0;
        foreach(string item in lines)
        {
            string line=item.Trim();
            if(line.StartsWith("[",StringComparison.Ordinal))
            {
                section=line.Equals("[ServerSettings]",StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if(!section || line.StartsWith(";",StringComparison.Ordinal) ||
               line.StartsWith("#",StringComparison.Ordinal))continue;
            int equals=line.IndexOf('=');
            if(equals<1)continue;
            string key=line[..equals].Trim(),value=line[(equals+1)..].Trim();
            if(key.Equals("RCONEnabled",StringComparison.OrdinalIgnoreCase))
            {
                enabled++;
                if(!value.Equals("True",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("UM-RCON-INI-DISABLED");
            }
            else if(key.Equals("RCONPort",StringComparison.OrdinalIgnoreCase))
            {
                ports++;
                if(value!=port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new InvalidDataException("UM-RCON-INI-PORT-MISMATCH");
            }
            else if(key.Equals("ServerAdminPassword",StringComparison.OrdinalIgnoreCase))
            {
                passwords++;
                if(!string.Equals(value,password,StringComparison.Ordinal))
                    throw new InvalidDataException("UM-RCON-INI-PASSWORD-MISMATCH");
            }
        }
        if(enabled!=1||ports!=1||passwords!=1)
            throw new InvalidDataException("UM-RCON-INI-AMBIGUOUS");
    }

    private static bool ExistingWorldOrStore(LocalMap map)
    {
        if(KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map))return true;
        string folder=Path.Combine(map.InstallationPath,"ShooterGame","Saved","SavedArks");
        return Directory.Exists(folder)&&Directory.EnumerateFileSystemEntries(folder).Any();
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >4096)return Error(null,"UM-RCON-CONFIG-REQUEST");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("id",out var requestId) ||
               requestId.ValueKind!=JsonValueKind.String ||
               !Guid.TryParse(requestId.GetString(),out var rid) ||
               !root.TryGetProperty("method",out var methodElement) ||
               methodElement.ValueKind!=JsonValueKind.String)
                return Error(null,"UM-RCON-CONFIG-REQUEST");
            id=rid.ToString("D");
            var method=methodElement.GetString();
            if(method is not ("rcon.configStatus" or "rcon.configPreview" or "rcon.configApply"))
                return Error(id,"UM-METHOD-DENIED");
            await serial.WaitAsync();
            try
            {
                if(method=="rcon.configStatus")
                {
                    if(!TryGuid(root,"mapId",out var mapId))return Error(id,"UM-RCON-MAP");
                    var map=Local(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    return Ok(id,new{mapId,mapName=map.Name,rconPort=map.RconPort,
                        configured=map.RconPort>0&&!string.IsNullOrWhiteSpace(map.RconPasswordProtected),
                        permitted=SafeMap(map)&&Stopped(map)&&
                            FreshAsaInstallationSafety.IsProvenFresh(cluster,map),
                        evidence="LOCAL_RCON_CONFIGURATION_STATUS_NO_WRITE"});
                }
                if(method=="rcon.configPreview")
                {
                    if(!TryGuid(root,"mapId",out var mapId) ||
                       !root.TryGetProperty("rconPort",out var portField) ||
                       portField.ValueKind!=JsonValueKind.Number ||
                       !portField.TryGetInt32(out var port) ||
                       port is <1024 or >65535 ||
                       !root.TryGetProperty("password",out var secretField) ||
                       secretField.ValueKind!=JsonValueKind.String ||
                       !root.TryGetProperty("createIni",out var createField) ||
                       createField.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Error(id,"UM-RCON-CONFIG-FIELDS");
                    var map=Local(mapId);
                    if(map==null)return Error(id,"UM-MAP-NOT-OWNED");
                    if(!SafeMap(map)||!Stopped(map)||
                       !FreshAsaInstallationSafety.IsProvenFresh(cluster,map))
                        return Error(id,"UM-RCON-CONFIG-STOPPED-NONSTORE-ONLY");
                    string password=secretField.GetString()??"";
                    AsaInitialRconSettings.ValidateAdminPassword(password);
                    LocalMapPortPolicy.RequireNoConflicts(new LocalMap{
                        Id=map.Id,Name=map.Name,GamePort=map.GamePort,
                        QueryPort=map.QueryPort,RconPort=port
                    },cluster.Config.LocalMaps);
                    string path=IniPath(map);
                    bool create=createField.GetBoolean();
                    if(create)
                    {
                        if(File.Exists(path)||ExistingWorldOrStore(map))
                            return Error(id,"UM-RCON-BOOTSTRAP-NOT-FRESH");
                    }
                    else VerifyExisting(path,port,password);
                    string digest=Digest(path);
                    pending=new(Guid.NewGuid(),map.Id,map.Arguments,map.InstallationPath,
                        map.ExecutablePath,map.RconPort,SecureStore.Protect(password),port,
                        create,digest,DateTimeOffset.UtcNow);
                    return Ok(id,new{token=pending.Token,mapId=map.Id,mapName=map.Name,
                        rconPort=port,createsNewIni=create,existingIniVerified=!create,
                        evidence="STOPPED_LOCAL_RCON_PREVIEW_NO_WRITES"});
                }
                if(!TryGuid(root,"token",out var token)||pending==null ||
                   pending.Token!=token ||
                   DateTimeOffset.UtcNow-pending.Created>TimeSpan.FromMinutes(5))
                    return Error(id,"UM-RCON-CONFIG-TOKEN-EXPIRED");
                if(!root.TryGetProperty("confirm",out var approve) ||
                   approve.ValueKind!=JsonValueKind.True)
                    return Error(id,"UM-RCON-CONFIG-CONFIRMATION");
                var item=pending;
                pending=null; // one shot, including failed writes
                var current=Local(item.MapId);
                if(current==null||!SafeMap(current) || !Stopped(current) ||
                   !FreshAsaInstallationSafety.IsProvenFresh(cluster,current) ||
                   current.Arguments!=item.BaselineArguments ||
                   current.InstallationPath!=item.Installation ||
                   current.ExecutablePath!=item.Executable ||
                   current.RconPort!=item.BaselineRcon)
                    return Error(id,"UM-RCON-CONFIG-CHANGED");
                using var lease=cluster.Operations.Acquire(current.InstallationPath,
                    UntamedOperationKind.Configuration);
                if(!Stopped(current)||!SafeMap(current))
                    return Error(id,"UM-RCON-CONFIG-CHANGED");
                string filename=IniPath(current);
                if(Digest(filename)!=item.IniDigest)
                    return Error(id,"UM-RCON-INI-CHANGED");
                var next=JsonSerializer.Deserialize<LocalMap>(JsonSerializer.Serialize(current))
                    ??throw new InvalidDataException("UM-RCON-CLONE");
                next.RconPort=item.Port;
                next.RconHost="127.0.0.1";
                next.RconPasswordProtected=item.PasswordProtected;
                if(!item.CreateIni)
                    VerifyExisting(filename,item.Port,SecureStore.Unprotect(item.PasswordProtected));
                else if(ExistingWorldOrStore(current))
                    return Error(id,"UM-RCON-BOOTSTRAP-NOT-FRESH");
                lease.MarkMutationStarted();
                bool created=false;
                try
                {
                    if(item.CreateIni)
                    {
                        AsaInitialRconSettings.CreateForNewServer(current.InstallationPath,
                            item.Port,SecureStore.Unprotect(item.PasswordProtected));
                        created=true;
                    }
                    if(!Stopped(current))throw new InvalidOperationException("UM-RCON-PROCESS-CHANGED");
                    cluster.ReplaceMap(current,next);
                    lease.Complete();
                }
                catch
                {
                    // The new INI is removed only when it still has our exact bytes;
                    // a modified file is never silently deleted. Operation journal
                    // remains NEEDS_OPERATOR after an interrupted mutation.
                    if(created && File.Exists(filename))
                    {
                        string expected="[ServerSettings]\r\nRCONEnabled=True\r\nRCONPort="+
                            item.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)+
                            "\r\nServerAdminPassword="+
                            SecureStore.Unprotect(item.PasswordProtected)+"\r\n";
                        if(File.ReadAllText(filename)==expected)File.Delete(filename);
                    }
                    throw;
                }
                return Ok(id,new{mapId=next.Id,mapName=next.Name,
                    configured=true,rconPort=next.RconPort,createsNewIni=item.CreateIni,
                    serverStarted=false,evidence="STOPPED_LOCAL_RCON_CONFIG_COMMITTED"});
            }
            finally{serial.Release();}
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or
            System.Security.Cryptography.CryptographicException or JsonException)
        {
            return Error(id,"UM-RCON-CONFIG-FAILED");
        }
    }
    private static bool TryGuid(JsonElement root,string field,out Guid id)
    {
        id=Guid.Empty;
        return root.TryGetProperty(field,out var value)&&value.ValueKind==JsonValueKind.String&&
               Guid.TryParse(value.GetString(),out id)&&id!=Guid.Empty;
    }
    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>serial.Dispose();
}
