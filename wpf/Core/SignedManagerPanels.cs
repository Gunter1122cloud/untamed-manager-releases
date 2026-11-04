using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record ManagerPanelSnapshot(DateTimeOffset ObservedUtc,string HostName,
    int LocalMaps,int? RunningMaps,long? WorkingSetBytes);
public sealed record ManagerPanelLine(string Kind,string Label,string Value);
public interface IManagerPanelExtension
{
    Guid Id {get;}
    string Title {get;}
    IReadOnlyList<ManagerPanelLine> Build(ManagerPanelSnapshot snapshot);
}
public sealed record ManagerPanelPackage(string PayloadBase64,string SignatureBase64);
public sealed record ManagerPanelItem(string Kind,string Label,string Value);
public sealed record ManagerPanelDefinition(int Schema,Guid Id,string Version,string Title,ManagerPanelItem[] Items);
public sealed class VerifiedManagerPanel : IManagerPanelExtension
{
    readonly ManagerPanelDefinition model;
    public Guid Id=>model.Id;
    public string Title=>model.Title;
    public string Version=>model.Version;
    public string PublisherFingerprint {get;}
    public string PayloadSha256 {get;}
    internal VerifiedManagerPanel(ManagerPanelDefinition model,string publisher,string hash)
    {this.model=model with {Items=model.Items.ToArray()};PublisherFingerprint=publisher;PayloadSha256=hash;}
    public IReadOnlyList<ManagerPanelLine> Build(ManagerPanelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool fresh=snapshot.ObservedUtc<=DateTimeOffset.UtcNow.AddSeconds(5)&&
            DateTimeOffset.UtcNow-snapshot.ObservedUtc<=TimeSpan.FromSeconds(30);
        string Metric(string value)=>value switch{
            "local.host"=>snapshot.HostName==null?"ismeretlen":snapshot.HostName[..Math.Min(snapshot.HostName.Length,100)],
            "local.maps"=>snapshot.LocalMaps>=0?snapshot.LocalMaps.ToString():"ismeretlen",
            "local.running"=>fresh&&snapshot.RunningMaps>=0?snapshot.RunningMaps.Value.ToString():"ismeretlen",
            "local.memoryMiB"=>fresh&&snapshot.WorkingSetBytes>=0?(snapshot.WorkingSetBytes.Value/1048576.0).ToString("N1"):"ismeretlen",
            _=>"ismeretlen"
        };
        return model.Items.Select(x=>new ManagerPanelLine(x.Kind,x.Label,x.Kind=="metric"?Metric(x.Value):x.Value)).ToArray();
    }
}
public sealed record ManagerPanelLoad(Guid Id,VerifiedManagerPanel? Panel,string Status);

// External extensions are bounded SIGNED DECLARATIVE PANELS. No assembly,
// XAML, script, URL, reflection or arbitrary command is interpreted or loaded.
// A panel sees only the narrow snapshot and may request allowlisted navigation.
public sealed class SignedManagerPanelRegistry
{
    private sealed record Pin(Guid Id,string PayloadSha256,string PublisherFingerprint,string PublicKeyPem);
    private sealed record Trust(int Schema,Guid Host,string Profile,List<Pin> Panels);
    readonly string profile,library,state;readonly Guid host;
    public SignedManagerPanelRegistry(string profileRoot,Guid localHost)
    {
        if(localHost==Guid.Empty)throw new InvalidDataException("Local panel owner required");
        profile=Local(profileRoot);host=localHost;library=Local(Path.Combine(profile,"ui-panels"));state=Path.Combine(library,"trust.dpapi");
    }
    static string Local(string raw)
    {
        UntamedOperationCoordinator.ResourceKey(raw);string result=Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar);
        if(File.Exists(result)&&(File.GetAttributes(result)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked panel path rejected");
        return result;
    }
    static byte[] ReadBounded(string raw,int max)
    {
        string path=Local(raw);
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <1||file.Length>max)throw new InvalidDataException("Panel input size rejected");
        byte[] bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);return bytes;
    }
    static void Properties(JsonElement element,params string[] allowed)
    {
        if(element.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Panel object required");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in element.EnumerateObject())
            if(!allowed.Contains(property.Name,StringComparer.Ordinal)||!names.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate panel property rejected");
    }
    static bool Text(string? value,int max)=>value!=null&&value.Length<=max&&!value.Any(char.IsControl);
    public static VerifiedManagerPanel Verify(byte[] packageBytes,string publicKeyPem)
    {
        if(packageBytes==null||packageBytes.Length is <1 or >65536||publicKeyPem==null||
           publicKeyPem.Length>8192||publicKeyPem.Contains("PRIVATE KEY",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bounded public-key panel package required");
        using var envelopeDoc=JsonDocument.Parse(packageBytes,new JsonDocumentOptions{MaxDepth=8});
        Properties(envelopeDoc.RootElement,"PayloadBase64","SignatureBase64");
        var envelope=JsonSerializer.Deserialize<ManagerPanelPackage>(packageBytes)??throw new InvalidDataException("Missing panel envelope");
        byte[] payload,signature;
        try{payload=Convert.FromBase64String(envelope.PayloadBase64);signature=Convert.FromBase64String(envelope.SignatureBase64);}
        catch(Exception e) when(e is FormatException or ArgumentNullException){throw new InvalidDataException("Invalid panel signature frame");}
        if(payload.Length is <1 or >32768||signature.Length is <256 or >512)throw new InvalidDataException("Panel signature/payload bounds exceeded");
        using var rsa=RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
            if(rsa.KeySize is <2048 or >4096||!rsa.VerifyData(payload,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))
                throw new InvalidDataException("Panel publisher signature is invalid");
        }
        catch(CryptographicException){throw new InvalidDataException("Panel publisher key/signature cannot be verified");}
        using var document=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=8});
        Properties(document.RootElement,"Schema","Id","Version","Title","Items");
        var definition=JsonSerializer.Deserialize<ManagerPanelDefinition>(payload)??throw new InvalidDataException("Missing panel definition");
        if(definition.Schema!=1||definition.Id==Guid.Empty||!Text(definition.Title,100)||string.IsNullOrWhiteSpace(definition.Title)||
           !System.Text.RegularExpressions.Regex.IsMatch(definition.Version??"",@"\A[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\z")||
           definition.Items==null||definition.Items.Length is <1 or >24)
            throw new InvalidDataException("Panel schema, identity or bounds are invalid");
        foreach(var item in document.RootElement.GetProperty("Items").EnumerateArray())Properties(item,"Kind","Label","Value");
        foreach(var item in definition.Items)
        {
            if(item==null||!Text(item.Label,100)||!Text(item.Value,500))throw new InvalidDataException("Panel text exceeds bounds");
            bool allowed=item.Kind switch{
                "text"=>true,
                "metric"=>item.Value is "local.host" or "local.maps" or "local.running" or "local.memoryMiB",
                "navigate"=>item.Value is "dashboard" or "servers" or "monitoring" or "backups" or "readiness",
                _=>false
            };
            if(!allowed)throw new UnauthorizedAccessException("Panel requests an unavailable field or capability");
        }
        string fingerprint=Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        return new(definition,fingerprint,Convert.ToHexString(SHA256.HashData(payload)));
    }
    public static VerifiedManagerPanel Inspect(string package,string publicKey)=>
        Verify(ReadBounded(package,65536),new UTF8Encoding(false,true).GetString(ReadBounded(publicKey,8192)));
    Trust ReadTrust()
    {
        Local(library);if(!File.Exists(state))return new(1,host,profile,new());
        Trust? trust;
        try{trust=JsonSerializer.Deserialize<Trust>(ProtectedData.Unprotect(ReadBounded(state,131072),null,DataProtectionScope.CurrentUser));}
        catch(CryptographicException){throw new InvalidDataException("Panel owner approval cannot be verified");}
        if(trust==null||trust.Schema!=1||trust.Host!=host||!string.Equals(trust.Profile,profile,StringComparison.OrdinalIgnoreCase)||
           trust.Panels==null||trust.Panels.Count>16||trust.Panels.Any(p=>p==null||p.Id==Guid.Empty||
               p.PayloadSha256==null||p.PayloadSha256.Length!=64||!p.PayloadSha256.All(Uri.IsHexDigit)||
               p.PublisherFingerprint==null||p.PublisherFingerprint.Length!=64)||
           trust.Panels.GroupBy(x=>x.Id).Any(g=>g.Count()!=1))throw new InvalidDataException("Panel approval scope or identity differs");
        return trust;
    }
    void Save(Trust trust)
    {
        Local(library);Directory.CreateDirectory(library);Local(state);
        byte[] encrypted=ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(trust),null,DataProtectionScope.CurrentUser);
        string temp=state+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(encrypted);file.Flush(true);}
            File.Move(temp,state,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public VerifiedManagerPanel Install(string packagePath,string publicKeyPath,string expectedPublisher,string expectedPayload)
    {
        byte[] package=ReadBounded(packagePath,65536);string key=new UTF8Encoding(false,true).GetString(ReadBounded(publicKeyPath,8192));
        var panel=Verify(package,key);
        if(panel.PublisherFingerprint!=expectedPublisher||panel.PayloadSha256!=expectedPayload)
            throw new UnauthorizedAccessException("Panel or publisher changed after operator preview");
        // Panel failures use their own durable domain and can never leave a
        // pending lifecycle/configuration operation on the Manager profile.
        using var operation=new UntamedOperationCoordinator(Path.Combine(library,"operations"))
            .Acquire(library,UntamedOperationKind.Configuration);
        var trust=ReadTrust();if(trust.Panels.Count>=16||trust.Panels.Any(x=>x.Id==panel.Id))
            throw new InvalidOperationException("Panel already approved or extension limit reached");
        string destination=Local(Path.Combine(library,panel.PayloadSha256+".panel.json"));
        bool exists=File.Exists(destination);
        if(exists)
        {
            var previous=Verify(ReadBounded(destination,65536),key);
            if(previous.PayloadSha256!=panel.PayloadSha256)throw new InvalidDataException("Existing panel artifact differs");
        }
        operation.MarkMutationStarted();Directory.CreateDirectory(library);
        if(!exists)
        {using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(package);output.Flush(true);}
        trust.Panels.Add(new(panel.Id,panel.PayloadSha256,panel.PublisherFingerprint,key));Save(trust);operation.Complete();return panel;
    }
    public IReadOnlyList<ManagerPanelLoad> Load()
    {
        var trust=ReadTrust();var result=new List<ManagerPanelLoad>();
        foreach(var pin in trust.Panels)
        {
            try
            {
                var panel=Verify(ReadBounded(Path.Combine(library,pin.PayloadSha256+".panel.json"),65536),pin.PublicKeyPem);
                if(panel.Id!=pin.Id||panel.PayloadSha256!=pin.PayloadSha256||panel.PublisherFingerprint!=pin.PublisherFingerprint)
                    throw new InvalidDataException("Approved panel pin changed");
                result.Add(new(pin.Id,panel,"verified"));
            }
            catch(Exception e) when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            {result.Add(new(pin.Id,null,"UM-PANEL-REJECTED"));}
        }
        return result;
    }
    public void Revoke(Guid panel)
    {
        using var operation=new UntamedOperationCoordinator(Path.Combine(library,"operations"))
            .Acquire(library,UntamedOperationKind.Configuration);
        var trust=ReadTrust();if(trust.Panels.RemoveAll(x=>x.Id==panel)==0){operation.Complete();return;}
        operation.MarkMutationStarted();Save(trust);operation.Complete();
    }
}
