using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

// A deliberately explicit feature matrix, visible both in the WPF application
// and in safe support reports. "TEST" and "BLOCKED" must never be "READY".
public sealed record FeatureAvailability(string Id,string Title,string Status,
    bool ProductionValidated,string Remaining);
public static class ManagerFeatureMatrix
{
    public static readonly FeatureAvailability[] Current =
    {
        new("desktop","Windows WPF, tray, installer","TEST",false,
            "Windows test-PC acceptance still required"),
        new("fleet","Two local-host Managers and encrypted authenticated LAN observation","TEST",false,
            "No live production-host acceptance; peer is status-only"),
        new("vault","Untamed Cluster Vault ZIP and offline Store-v1 observation","TEST",false,
            "No live Store writes, character/tribe transfer tests or 1:1 acceptance"),
        new("mods","Active/Passive/Disabled local mod configuration","LOCAL",false,
            "No verified live ASA boot acknowledgement or installed file ID"),
        new("plugins","Safe plugin ZIP, rollback and configuration editor","LOCAL",false,
            "Not certified for live plugins or hot reload"),
        new("ini","INI/JSON editor with SHA-based conflict checks","LOCAL",false,
            "No live host editing acceptance"),
        new("steam","SteamCMD staging and test runner","SIMULATED",false,
            "Only FakeSteamCMD executes; no live server update"),
        new("api","ASA API inventory and official release metadata","READ_ONLY",false,
            "No live loader install/update and runtime verification"),
        new("backup","Verified local/offline 10-map and SQL archive","OFFLINE",false,
            "No coordinated live 2-host + Store + MariaDB snapshot"),
        new("restore","Data restore","BLOCKED",false,
            "Live Kova, ASA and SQL restore disabled until isolated acceptance"),
        new("lifecycle","ASA Start/Save/Stop/Restart and Watchdog recovery","SIMULATED",false,
            "Only real Windows FakeAsa process; no validated live ASA SaveWorld proof"),
        new("scheduler","Supervisor read-only recurring inspections","READ_ONLY",false,
            "Automatic live Restart/Wipe/Backup disabled"),
        new("rcon","RCON ListPlayers and mutating command previews","READ_ONLY",false,
            "No production mutating command authorization"),
        new("monitor","Local Supervisor CPU/RAM, event history","READ_ONLY",false,
            "Player count, WAN/jitter and genuine ASA readiness incomplete"),
        new("updater","Windows updater and local rollback","TEST",false,
            "Published signed Preview/Stable releases and test-PC acceptance missing"),
        new("discord","Discord server status","BLOCKED",false,
            "No opt-in authenticated Discord delivery connector yet"),
        new("mobile","Mobile management","BLOCKED",false,
            "No authenticated VPN/mobile UI or operator roles"),
        new("license","Cloudflare license activation","BLOCKED",false,
            "No verified activation/Ed25519 offline ticket integration"),
        new("ups","UPS shutdown monitoring","BLOCKED",false,
            "No supported UPS provider integration or tested save/stop safety"),
        new("wan","WAN/DDNS observation","READ_ONLY",false,
            "Opt-in observation only; no automatic IP-triggered restart")
    };
    public static void AssertHonest()
    {
        if(Current.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=Current.Length ||
           Current.Any(x=>x.ProductionValidated))
            throw new InvalidOperationException("Unverified live capability must never be advertised as production-ready");
    }
}

public sealed record SafeSupportBundleResult(string File,string Sha256,
    int EventCount,int FeatureCount,int MapCount);

// Never export raw crash.log, server logs, config JSON, full paths, process
// command lines, IPs, device IDs, usernames, Discord webhooks or player data.
public static class SafeSupportBundle
{
    private sealed record PublicSummary(int Schema,string Product,string Version,
        DateTimeOffset CreatedUtc,string Mode,int ConfiguredLocalMaps,
        int ConfiguredScheduledJobs,int ConfiguredRecurringJobs,
        bool AutoStartMayExecute,bool RealServerWritesAuthorized,
        bool LiveStoreTransferProven,bool LiveMariaDbRestoreAuthorized);
    private sealed record PublicEvent(DateTimeOffset Utc,string Severity,string Code);

    public static SafeSupportBundleResult Create(string destination,
        ManagerConfig config,IEnumerable<MonitorEvent>? events,
        DateTimeOffset createdUtc)
    {
        if(config==null)throw new ArgumentNullException(nameof(config));
        ManagerFeatureMatrix.AssertHonest();
        if(string.IsNullOrWhiteSpace(destination)||
           !Path.IsPathFullyQualified(destination) ||
           destination.StartsWith(@"\\")||
           !Path.GetExtension(destination).Equals(".zip",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An explicit local ZIP destination is required");
        string full=Path.GetFullPath(destination);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Support ZIP requires a fixed local disk");
        for(var p=new DirectoryInfo(Path.GetDirectoryName(full)!);p!=null;p=p.Parent)
            if(p.Exists && (p.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked support directory rejected");
        if(File.Exists(full)||Directory.Exists(full))
            throw new IOException("Support ZIP destination already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        var valid=events?.Where(e=>e!=null &&
            e.Code.Length is > 0 and <= 100 &&
            e.Code.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_') &&
            e.Severity is "Information" or "Warning" or "Error")
            .OrderByDescending(e=>e.Utc).Take(100)
            .Select(e=>new PublicEvent(e.Utc,e.Severity,e.Code)).ToArray()
            ??Array.Empty<PublicEvent>();
        var summary=new PublicSummary(1,"Untamed Kingdom ASA Manager",
            typeof(SafeSupportBundle).Assembly.GetName().Version?.ToString(3)??"unknown",
            createdUtc,"ISOLATED_TEST",config.LocalMaps.Count,
            config.ScheduledTasks.Count,config.RecurringSchedules.Count,
            false,false,false,false);
        try
        {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var archive=new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:false,Encoding.UTF8))
            {
                Add(archive,"summary.json",JsonSerializer.Serialize(summary));
                Add(archive,"features.json",JsonSerializer.Serialize(ManagerFeatureMatrix.Current));
                Add(archive,"events-redacted.json",JsonSerializer.Serialize(valid));
                Add(archive,"README.txt",
                    "Untamed Kingdom ASA Manager - explicit redacted support report\n"+
                    "No raw logs, credentials, IP addresses, host names, player data, "+
                    "full file paths, process arguments or Store records are exported.\n"+
                    "No production lifecycle or character-transfer compatibility is proven.\n");
            }
            File.Move(temp,full);
            using var input=File.OpenRead(full);
            return new(full,Convert.ToHexString(SHA256.HashData(input)),
                valid.Length,ManagerFeatureMatrix.Current.Length,
                config.LocalMaps.Count);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }

    private static void Add(ZipArchive zip,string name,string text)
    {
        var entry=zip.CreateEntry(name,CompressionLevel.Optimal);
        using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));
        writer.Write(text);
    }
}
