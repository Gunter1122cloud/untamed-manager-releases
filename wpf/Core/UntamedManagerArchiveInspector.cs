using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace UntamedManager.Core;

// Untamed Manager Migration Vault. Reads a previously copied configuration
// archive, never original live files and never writes its contents to a host.
// Historic folder names/JSON schema literals are protocol compatibility ONLY.
public sealed record UntamedManagerArchiveReport(
    int VerifiedFiles,int LocalMaps,int SharedMaps,int StorePeers,
    int ScheduledTasks,int SkippedTaskResults,int ScheduleSyncErrors,
    int AutoStartProfiles,int WatchdogProfiles,int PendingLoaderStates,
    int MissingServerPidStates,int BackupPolicyFiles,bool DiscordDisabled,
    bool DiscordControlsConfigured,bool EmbeddedSensitiveState,
    bool SharedSessionConsistent,bool PeerConfigHashesVerified,
    string[] MapNames,string[] Findings,string SourceSha256,
    bool ProductionImportAllowed);

public static class UntamedManagerArchiveInspector
{
    private const string Legacy="Kova/";
    private const string Shared="SHARED/.kova-store-v1/";
    private static readonly JsonSerializerOptions Compact=new(){
        Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};

    public static UntamedManagerArchiveReport Inspect(string filename)
    {
        using var snapshot=new VaultReadSnapshot();
        if(string.IsNullOrWhiteSpace(filename)||!filename.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VAULT_INPUT: helyi ZIP-másolat szükséges");
        byte[] bytes=snapshot.Read(filename,50_000_000);
        string sourceSha=Sha(bytes);
        var entries=VaultReadSnapshot.Archive(bytes,"README.txt");
        int verifiedFiles=entries.Count(x=>x.Value.Length>0)-2;
        var vault=ClusterVaultArchiveInspector.InspectFiles(entries);

        using var config=Json(entries,Legacy+"cluster-v1.json");
        using var current=Json(entries,Legacy+"cluster-store-v1.json");
        using var launch=Json(entries,Legacy+"launch-profiles-v1.json");
        using var routes=Json(entries,Legacy+"local-servers.json");
        using var schedules=Json(entries,Legacy+"schedules-v1.json");
        using var supervisor=Json(entries,Legacy+"supervisor-v1.json");
        using var bot=Json(entries,Legacy+"discord-bot-v1.json");
        using var operation=Json(entries,Shared+"operation.json");
        using var localDevice=Json(entries,Legacy+"shared-store-device-v1.json");
        using var localLock=Json(entries,Legacy+"shared-store-lock-v1.json");
        var failures=new List<string>();

        var localMembers=Array(config.RootElement,"members").EnumerateArray().ToArray();
        if(Str(config.RootElement,"id")!="KovaCluster"||
           Str(config.RootElement,"mode")!="shared"||localMembers.Length!=5)
            throw new InvalidDataException("A mentett közös konfiguráció nem támogatott");
        var localIds=localMembers.Select(x=>Str(x,"id")).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if(localIds.Count!=5||localIds.Contains(""))
            throw new InvalidDataException("Nem egyedi helyi mapazonosítók");
        var localNames=localMembers.Select(x=>Str(x,"map")).ToArray();
        var store=Required(current.RootElement,"maps");
        if(Int32(current.RootElement,"schema")!=1||
           store.ValueKind!=JsonValueKind.Object||
           store.EnumerateObject().Count()!=5 ||
           !store.EnumerateObject().Select(x=>x.Name).ToHashSet(
               StringComparer.OrdinalIgnoreCase).SetEquals(localIds))
            failures.Add("Az öt helyi map aktív adattár-állapota nem teljes");
        var launchRows=Array(launch.RootElement,"rows").EnumerateArray().ToArray();
        if(Int32(launch.RootElement,"version")!=1 ||launchRows.Length!=5||
           !launchRows.Select(x=>Str(x,"id")).ToHashSet(
               StringComparer.OrdinalIgnoreCase).SetEquals(localIds))
            failures.Add("A helyi indítási profilok és a maplista eltérnek");
        var routeIds=routes.RootElement.ValueKind==JsonValueKind.Object?
            routes.RootElement.EnumerateObject().Select(x=>x.Name).ToHashSet(
                StringComparer.OrdinalIgnoreCase):new HashSet<string>();
        if(!routeIds.SetEquals(localIds))
            failures.Add("A helyi szerverregiszter és a maplista eltérnek");

        var op=operation.RootElement;
        if(Int32(op,"schema")!=1 || Str(op,"cluster")!="KovaCluster"||
           !Guid.TryParse(Str(op,"session"),out var session))
            throw new InvalidDataException("Nem támogatott közös munkamenet");
        var peerIds=Array(op,"peers").EnumerateArray()
            .Select(x=>x.ValueKind==JsonValueKind.String?x.GetString()??"":"").ToArray();
        if(peerIds.Length!=2||peerIds.Distinct(
            StringComparer.OrdinalIgnoreCase).Count()!=2 ||
           peerIds.Any(id=>!Guid.TryParse(id,out _)))
            throw new InvalidDataException("Hiányos kétgazdás kapcsolat");
        bool sessions=true,hashes=true,phases=true;
        var peerDocs=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        var names=new List<string>();
        foreach(var peerId in peerIds)
        {
            using var peer=Json(entries,Shared+peerId+".json");
            var p=peer.RootElement;
            if(!Guid.TryParse(Str(p,"session"),out var sid)||sid!=session ||
               !Guid.TryParse(Str(p,"id"),out var own)||own!=Guid.Parse(peerId))
                sessions=false;
            if(Str(p,"phase")!="done")phases=false;
            var pcfg=Required(p,"config");
            if(Str(pcfg,"id")!="KovaCluster"||Str(pcfg,"mode")!="shared" ||
               !Str(pcfg,"path").Equals(Str(op,"path"),
                   StringComparison.OrdinalIgnoreCase))
                sessions=false;
            if(!StoreV1Fingerprint.Matches(pcfg,Str(p,"configHash")))
                hashes=false;
            var members=Array(p,"members").EnumerateArray().ToArray();
            var selected=Array(p,"selected").EnumerateArray()
                .Select(x=>x.GetString()??"").ToArray();
            if(members.Length!=5||selected.Length!=5||
               !selected.ToHashSet(StringComparer.OrdinalIgnoreCase)
                  .SetEquals(members.Select(x=>Str(x,"id"))))
                sessions=false;
            names.AddRange(members.Select(x=>Str(x,"map")));
            peerDocs[peerId]=p.Clone();
        }
        if(names.Count!=10||names.Distinct(
            StringComparer.OrdinalIgnoreCase).Count()!=10)
            failures.Add("A két host mapkiosztása nem egyedi / hiányos");
        if(!phases)failures.Add("A két kapcsolat nincs lezárt állapotban");
        if(!hashes)failures.Add("A közös konfigurációlenyomat eltér");
        if(!sessions)failures.Add("A két peer munkamenete vagy konfigurációja eltér");
        string device=Str(localDevice.RootElement,"id");
        if(!peerDocs.TryGetValue(device,out var localPeer) ||
           !Guid.TryParse(Str(localLock.RootElement,"session"),out var lockedSession)||
           lockedSession!=session ||
           !Str(localLock.RootElement,"configHash").Equals(
               Str(localPeer,"configHash"),StringComparison.OrdinalIgnoreCase))
            failures.Add("A mentett helyi kapcsolat és a zárolási munkamenet eltér");
        else
        {
            var ownIds=Array(localPeer,"members").EnumerateArray()
                .Select(x=>Str(x,"id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!ownIds.SetEquals(localIds) ||
               !Array(localLock.RootElement,"selected").EnumerateArray()
                .Select(x=>x.GetString()??"").ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(localIds))
                failures.Add("A helyi mapok és a zárolás kiválasztása eltér");
            foreach(var member in store.ValueKind==JsonValueKind.Object?
                store.EnumerateObject():Enumerable.Empty<JsonProperty>())
            {
                if(Str(member.Value,"phase")!="active" ||
                   !Guid.TryParse(Str(member.Value,"sharedSession"),out var stored)||
                   stored!=session ||
                   Str(member.Value,"backupId")!=Str(Required(localPeer,"backup"),"id"))
                    failures.Add("Egy helyi map munkamenete nem aktív/egyező");
            }
        }

        var launchIds=launchRows.Select(x=>Str(x,"id")).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var ownedPaths=entries.Keys.Where(x=>x.StartsWith(Legacy+"tools/",
            StringComparison.OrdinalIgnoreCase) &&
            x.EndsWith("/owned-process-v1.json",StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var backups=entries.Keys.Where(x=>x.StartsWith(Legacy+"tools/",
            StringComparison.OrdinalIgnoreCase) &&
            x.EndsWith("/backup-settings.json",StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if(ownedPaths.Length!=5||ownedPaths.Any(path=>
           !launchIds.Contains(path.Split('/')[2])))
            failures.Add("A mentett folyamatregiszter nem fedi le az öt mapot");
        int pending=0,missingServer=0;
        foreach(var path in ownedPaths)
        {
            using var process=Json(entries,path);
            var api=Required(process.RootElement,"api");
            if(Str(api,"state") is "starting" or "unknown")pending++;
            if(!PositiveInt(api,"serverPid"))missingServer++;
        }
        if(pending>0||missingServer>0)
            failures.Add("Folyamat/API indulási állapot mentve; nem valós futáskészültségi bizonyíték");
        if(backups.Length<5)
            failures.Add("Nem minden maphoz szerepel külön korábbi mentésbeállítás");

        var scheduleProfiles=Required(schedules.RootElement,"profiles");
        if(Int32(schedules.RootElement,"version")!=1 ||
           scheduleProfiles.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Az ütemezőadat formátuma ismeretlen");
        if(!scheduleProfiles.EnumerateObject().Select(x=>x.Name)
           .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(localIds))
            failures.Add("Az ütemező nem ugyanazt az öt mapot tartalmazza");
        int taskCount=0,skipped=0,syncError=0;
        foreach(var row in scheduleProfiles.EnumerateObject())
        {
            if(Required(row.Value,"lastSyncError").ValueKind!=JsonValueKind.Null)
                syncError++;
            var tasks=Array(Required(row.Value,"config"),"tasks");
            taskCount+=tasks.GetArrayLength();
            var runtime=Required(row.Value,"runtime");
            if(runtime.ValueKind!=JsonValueKind.Object)
                throw new InvalidDataException("Az ütemező előzményformátuma hibás");
            skipped+=runtime.EnumerateObject().Count(x=>
                Str(Required(x.Value,"last"),"result")=="skipped");
        }
        if(syncError>0 || skipped>0)
            failures.Add("Kim aradt/hibás szinkronú ütemezési műveletek az archívumban"
                .Replace("Kim aradt","Kimaradt"));

        int auto=0,watchdog=0;
        var sup=Array(supervisor.RootElement,"config").EnumerateArray().ToArray();
        if(sup.Length!=5 ||
           !sup.Select(x=>Str(x,"id")).ToHashSet(
                StringComparer.OrdinalIgnoreCase).SetEquals(localIds))
            failures.Add("A Supervisor és a helyi maplista eltér");
        foreach(var row in sup)
        {
            if(Bool(row,"autoStart"))auto++;
            if(Bool(row,"watchdog"))watchdog++;
        }
        if(auto>0||watchdog>0)
            failures.Add("Régi automatikus indítás/watchdog beállítások: importkor TILTVA");

        bool botDisabled=!Bool(bot.RootElement,"enabled");
        bool controlsConfigured=Bool(Required(bot.RootElement,"controls"),"enabled");
        if(controlsConfigured)
            failures.Add("Korábbi távoli Discord-vezérlési szabályok: NEM importálhatók");
        bool sensitive=entries.Keys.Any(x=>x.Contains("leveldb/",
            StringComparison.OrdinalIgnoreCase)||
            x.Contains("Session Storage/",StringComparison.OrdinalIgnoreCase)||
            x.EndsWith("device-identity.bin",StringComparison.OrdinalIgnoreCase)||
            x.EndsWith("/Local State",StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(Str(bot.RootElement,"secret"));
        if(sensitive)
            failures.Add("A ZIP titkokat vagy érzékeny munkamenetfájlokat is tartalmaz; "+
                "nyilvános tárhelyre nem tölthető");
        if(entries.Keys.Any(x=>x.Contains("leveldb/",
            StringComparison.OrdinalIgnoreCase)))
            failures.Add("A futó alkalmazásból másolt LevelDB nem konzisztens restore-csomag");
        failures.Add("Nincsenek világ-, karakter-, tribe-, plugin- vagy SQL-mentések a csomagban");
        // Only map labels go into the WPF report; original roots, IDs, PIDs,
        // Discord secrets, schedules' messages and DB settings stay private.
        var safeMaps=names.Select(x=>x.Length>48||
             x.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c==' '||c=='_'||c=='-'||c=='.'))?
             "Ismeretlen map":x).ToArray();
        return new(verifiedFiles,localMembers.Length,names.Count,peerIds.Length,
            taskCount,skipped,syncError,auto,watchdog,pending,missingServer,
            backups.Length,botDisabled,controlsConfigured,sensitive,
            vault.SessionConsistent&&sessions&&phases,vault.ConfigHashesVerified&&hashes,safeMaps,
            failures.Concat(vault.Findings.Select(f=>f.Code+": "+f.Message)).Distinct().ToArray(),
            sourceSha,false);
    }

    private static string Sha(byte[] bytes)=>
        Convert.ToHexString(SHA256.HashData(bytes));
    private static JsonDocument Json(Dictionary<string,byte[]> files,string name)=>
        files.TryGetValue(name,out var bytes)?
            JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=48}):
            throw new InvalidDataException("Kötelező történeti konfiguráció hiányzik");
    private static JsonElement Required(JsonElement obj,string name)=>
        obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(name,out var value)?
            value:throw new InvalidDataException("Hiányzó konfigurációs szerkezeti mező");
    private static JsonElement Array(JsonElement obj,string name)
    {
        var result=Required(obj,name);
        return result.ValueKind==JsonValueKind.Array?result:
            throw new InvalidDataException("Hibás konfigurációs lista");
    }
    private static string Str(JsonElement obj,string name)=>
        obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(name,out var value)&&
        value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    private static int Int32(JsonElement obj,string name)=>
        obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(name,out var value)&&
        value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var n)?n:-1;
    private static long Int64(JsonElement obj,string name)=>
        obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(name,out var value)&&
        value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var n)?n:-1;
    private static bool Bool(JsonElement obj,string name)=>
        obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(name,out var value)&&
        value.ValueKind==JsonValueKind.True;
    private static bool PositiveInt(JsonElement obj,string name)=>
        obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(name,out var value)&&
        value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var n)&&n>0;
}

