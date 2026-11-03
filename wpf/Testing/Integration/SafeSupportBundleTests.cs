using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UntamedManager.Core;

public static class SafeSupportBundleTests
{
    private static void Check(bool ok,string name)
    {
        if(!ok)throw new Exception("INTEGRATION TEST FAILED: "+name);
        Console.WriteLine("PASS support "+name);
    }
    public static void Run(string root)
    {
        var cfg=new ManagerConfig{
            LocalHostId="UK-TEST-SECRET-HOST-ID",
            LocalHostName="PRIVATE-HOST-UK",
            PeerAddress="SECRET-WAN-PEER",
            PairingKeyProtected="SECRET-DPAPI-PAIRING",
            CurseForgeApiKeyProtected="SECRET-CURSEFORGE-DPAPI",
            PublisherCertificateThumbprint="SECRET-PUBLISHER-ID",
            LocalMaps=new(){
                new LocalMap{
                    Name="PLAYER-SECRET-NAME",ExecutablePath=@"C:\Users\SECRET-USERNAME\ArkAscendedServer.exe",
                    Arguments="--password=SECRET-RCON-PASSWORD",
                    RconPasswordProtected="SECRET-RCON-DPAPI",
                    RconHost="SECRET-NETWORK-IP"
                }
            }
        };
        var now=DateTimeOffset.UtcNow;
        var events=new[]{
            new MonitorEvent(Guid.NewGuid(),now,"PRIVATE-HOST-UK",
                Guid.NewGuid(),"Warning","watchdog-check"),
            new MonitorEvent(Guid.NewGuid(),now,"PRIVATE-HOST-UK",
                Guid.NewGuid(),"Information","manager-start"),
            new MonitorEvent(Guid.NewGuid(),now,"PRIVATE-HOST-UK",
                null,"Warning","ATTACK_SECRET-RCON-DPAPI / unsafe text")
        };
        string file=Path.Combine(root,"support-test.zip");
        var report=SafeSupportBundle.Create(file,cfg,events,now);
        Check(report.EventCount==2&&report.FeatureCount>=20 &&
            report.MapCount==1&&report.Sha256.Length==64,
            "safe support ZIP exports only whitelisted capability metadata and event codes");
        using(var zip=ZipFile.OpenRead(file))
        {
            var expected=new[]{"summary.json","features.json","events-redacted.json","README.txt"};
            Check(zip.Entries.Select(x=>x.Name).OrderBy(x=>x).SequenceEqual(
                expected.OrderBy(x=>x)),
                "support bundle contains four explicit sanitized entries; never raw logs");
            string joined=string.Join("\n",zip.Entries.Select(x=>{
                using var reader=new StreamReader(x.Open(),Encoding.UTF8);
                return reader.ReadToEnd();
            }));
            foreach(var leak in new[]{"PRIVATE-HOST-UK","SECRET-USERNAME",
                "SECRET-RCON-PASSWORD","SECRET-DPAPI-PAIRING",
                "SECRET-CURSEFORGE-DPAPI","SECRET-RCON-DPAPI",
                "SECRET-NETWORK-IP","SECRET-WAN-PEER","PLAYER-SECRET-NAME",
                "UK-TEST-SECRET-HOST-ID","SECRET-PUBLISHER-ID"})
                Check(!joined.Contains(leak,StringComparison.Ordinal),
                    "sensitive config source data excluded: "+leak.Split('-')[0]);
            Check(joined.Contains("watchdog-check")&&
                joined.Contains("\"ProductionValidated\":false") &&
                joined.Contains("\"RealServerWritesAuthorized\":false")&&
                joined.Contains("\"LiveStoreTransferProven\":false"),
                "support bundle reports diagnostics honestly without fabricated live-ASA validation");
        }
        bool duplicateBlocked=false;
        try{SafeSupportBundle.Create(file,cfg,events,now);}
        catch(IOException){duplicateBlocked=true;}
        Check(duplicateBlocked,"support export never silently replaces an existing ZIP");
        bool remoteBlocked=false;
        try{SafeSupportBundle.Create(@"\\server\share\report.zip",cfg,events,now);}
        catch(InvalidDataException){remoteBlocked=true;}
        Check(remoteBlocked,"support export refuses implicit remote SMB targets");
        Check(ManagerFeatureMatrix.Current.All(x=>!x.ProductionValidated),
            "feature matrix never marks unvalidated production operations as ready");
    }
}
