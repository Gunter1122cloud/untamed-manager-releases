using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UntamedManager.Core;

public static class SignedManagerPanelTests
{
    static void Check(bool value,string reason)
    {if(!value)throw new Exception("Signed panel: "+reason);Console.WriteLine("PASS signed-panel "+reason);}
    static bool Refused(Action action)
    {try{action();return false;}catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException){return true;}}
    public static void Run(string root)
    {
        using var signer=RSA.Create(2048);using var foreign=RSA.Create(2048);
        string publicKey=signer.ExportSubjectPublicKeyInfoPem();
        byte[] Package(byte[] payload)=>JsonSerializer.SerializeToUtf8Bytes(new ManagerPanelPackage(
            Convert.ToBase64String(payload),Convert.ToBase64String(signer.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))));
        Guid id=Guid.NewGuid();
        var model=new ManagerPanelDefinition(1,id,"1.0.0","Saját állapotpanel",new[]{
            new ManagerPanelItem("metric","Mapok","local.maps"),
            new ManagerPanelItem("metric","Futó mapok","local.running"),
            new ManagerPanelItem("navigate","Mentések","backups"),
            new ManagerPanelItem("text","Címke","Árvíztűrő 🦖")});
        byte[] package=Package(JsonSerializer.SerializeToUtf8Bytes(model));
        var inspected=SignedManagerPanelRegistry.Verify(package,publicKey);
        Check(inspected.Id==id&&inspected.PublisherFingerprint.Length==64&&inspected.PayloadSha256.Length==64,
            "real RSA-PSS SHA-256 signature validates exact declarative payload and publisher key");
        Check(Refused(()=>SignedManagerPanelRegistry.Verify(package,foreign.ExportSubjectPublicKeyInfoPem())),
            "another publisher's key cannot authorize the signed payload");
        var envelope=JsonSerializer.Deserialize<ManagerPanelPackage>(package)!;
        byte[] changed=JsonSerializer.SerializeToUtf8Bytes(envelope with {PayloadBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))});
        Check(Refused(()=>SignedManagerPanelRegistry.Verify(changed,publicKey)),"altered payload fails real signature verification");
        foreach(var item in new[]{new ManagerPanelItem("metric","secret","RconPasswordProtected"),
            new ManagerPanelItem("navigate","Stop","DoExit"),new ManagerPanelItem("script","code","Process.Start"),
            new ManagerPanelItem("xaml","code","ObjectDataProvider")})
            Check(Refused(()=>SignedManagerPanelRegistry.Verify(Package(JsonSerializer.SerializeToUtf8Bytes(model with {Items=new[]{item}})),publicKey)),
                "even correctly signed unknown data fields, commands, scripts and XAML are rejected");
        string json=JsonSerializer.Serialize(model).Replace("\"Schema\":1","\"Schema\":1,\"\\u0053chema\":1");
        Check(Refused(()=>SignedManagerPanelRegistry.Verify(Package(Encoding.UTF8.GetBytes(json)),publicKey)),
            "escaped duplicate signed JSON properties cannot equivocate across readers");
        string source=Path.Combine(root,"signed-panel-package.json"),key=Path.Combine(root,"publisher-public.pem");
        File.WriteAllBytes(source,package);File.WriteAllText(key,publicKey);
        string profile=Path.Combine(root,"signed-panel-profile");Guid host=Guid.NewGuid();
        var registry=new SignedManagerPanelRegistry(profile,host);
        Check(registry.Load().Count==0,"unapproved external package is not auto-loaded");
        Check(Refused(()=>registry.Install(source,key,new string('0',64),inspected.PayloadSha256)),
            "operator approval binds exact publisher fingerprint, independently of a valid signature");
        registry.Install(source,key,inspected.PublisherFingerprint,inspected.PayloadSha256);
        var loaded=registry.Load().Single().Panel!;
        Check(loaded.Build(new(DateTimeOffset.UtcNow,"local",5,3,1048576))[1].Value=="3"&&
            loaded.Build(new(DateTimeOffset.UtcNow.AddMinutes(-1),"local",5,3,1048576))[1].Value=="ismeretlen",
            "approved panel renders only narrow local snapshot and preserves unknown stale runtime values");
        Check(Refused(()=>new SignedManagerPanelRegistry(profile,Guid.NewGuid()).Load()),
            "protected allowlist cannot be transplanted to another Manager host identity");
        byte[] goodPackage=Package(JsonSerializer.SerializeToUtf8Bytes(model with {Id=Guid.NewGuid()}));
        string other=Path.Combine(root,"second-panel.json");File.WriteAllBytes(other,goodPackage);
        var second=SignedManagerPanelRegistry.Inspect(other,key);
        registry.Install(other,key,second.PublisherFingerprint,second.PayloadSha256);
        File.WriteAllText(Path.Combine(profile,"ui-panels",inspected.PayloadSha256+".panel.json"),"corrupted package");
        var health=registry.Load();
        Check(health.Count==2&&health.Count(x=>x.Panel!=null)==1&&health.Single(x=>x.Id==id).Status=="UM-PANEL-REJECTED",
            "one faulty extension is rejected while another verified panel remains usable");
        using(var coreOperation=new UntamedOperationCoordinator().Acquire(profile,UntamedOperationKind.Configuration))
            coreOperation.Complete();
        Check(true,"extension failure does not leave a pending operation in the core Manager profile domain");
        registry.Revoke(id);
        Check(registry.Load().Count==1&&File.Exists(Path.Combine(profile,"ui-panels",inspected.PayloadSha256+".panel.json")),
            "revoking approval disables the panel while preserving its artifact for review");
    }
}
