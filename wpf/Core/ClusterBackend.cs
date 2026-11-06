using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// This model is intentionally separate from the historic Kova audit. Nothing is imported implicitly.
public sealed class LocalMap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string InstallationPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public List<string> DisabledModIds { get; set; } = new();
    public bool ModChangesPending { get; set; } = false;
    public string LogFilePath { get; set; } = "";
    public string ApiLoaderPath { get; set; } = "";
    public string ApiLoaderArguments { get; set; } = "";
    // Explicit per-map opt-in. Existing profiles default to direct server mode.
    // Loader path alone is historical observation metadata, not permission to launch it.
    public bool StartWithAsaApi { get; set; } = false;
    public ProcessIdentity? LastLoader { get; set; }
    public string StoreTestRoot { get; set; } = "";
    // Existing Kova Store-v1 copies are imported as quarantined metadata,
    // not as an executable or writable character profile.
    public bool HistoricalStoreProtected { get; set; } = false;
    public string HistoricalArchiveSha256 { get; set; } = "";
    public string HistoricalMapKey { get; set; } = "";
    public List<string> HistoricalEnabledMods { get; set; } = new();
    public List<string> HistoricalPassiveMods { get; set; } = new();
    public int GamePort { get; set; }
    public int QueryPort { get; set; }
    public int RconPort { get; set; }
    public string RconHost { get; set; } = "127.0.0.1";
    public string RconPasswordProtected { get; set; } = "";
    public bool AutoStart { get; set; } = false;
    public bool Maintenance { get; set; } = false;
    public bool MonitorRcon { get; set; } = false;
    public int WatchdogIntervalSeconds { get; set; } = 30;
    public ProcessIdentity? LastProcess { get; set; }
}
public sealed class ProcessIdentity
{
    public int Pid { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string Arguments { get; set; } = "";
}
public sealed class ManagerConfig
{
    public string LocalHostId { get; set; } = Guid.NewGuid().ToString("N");
    public string LocalHostName { get; set; } = Environment.MachineName;
    public int ListenPort { get; set; } = 0; // Opt-in; 0 disables LAN listener.
    public string PeerAddress { get; set; } = ""; // IPv4/host, configured explicitly by user.
    // Mandatory durable identity pin; an IP address or shared secret alone is not a peer identity.
    // Existing profiles fail closed until the operator explicitly pairs the second host.
    public string ExpectedPeerHostId { get; set; } = "";
    public int PeerPort { get; set; }
    public string PairingKeyProtected { get; set; } = "";
    // Approved CurseForge key: stored only as Windows-user DPAPI ciphertext.
    public string CurseForgeApiKeyProtected { get; set; } = "";
    // Optional, explicitly configured outbound status webhook. Stored only with user-bound DPAPI.
    public string DiscordWebhookProtected { get; set; } = "";
    public string GitHubRepository { get; set; } = "Gunter1122cloud/untamed-manager-releases";
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;
    public string PublisherCertificateThumbprint { get; set; } = "";
    public bool TelemetryHistoryEnabled { get; set; } = false;
    // SteamCMD preferences are metadata only; auto-update remains opt-in
    // and the independent production SaveComplete/Stop capability gate applies.
    public string SteamCmdExecutable { get; set; } = "";
    public bool AutoSteamUpdatesEnabled { get; set; } = false;
    public int SteamCheckIntervalMinutes { get; set; } = 180;
    public string SteamMaintenanceStart { get; set; } = "03:00";
    public string SteamMaintenanceEnd { get; set; } = "05:00";
    public bool SteamWarnPlayers { get; set; } = true;
    public int SteamWaitPlayersMinutes { get; set; } = 30;
    public bool SteamRestartAfterUpdate { get; set; } = true;
    public int SteamPostUpdateProbeMinutes { get; set; } = 10;
    public List<ScheduledTask> ScheduledTasks { get; set; } = new();
    public List<RecurringSchedule> RecurringSchedules { get; set; } = new();
    public List<LocalMap> LocalMaps { get; set; } = new();
}
public sealed class PublicMapStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    public string Status { get; set; } = "leállítva";
    public int GamePort { get; set; }
    // Authenticated LAN telemetry only: no install paths, credentials or
    // remote commands; older Managers safely ignore these optional fields.
    public string? SteamBuildId { get; set; }
    public string SteamUpdateState { get; set; } = "Nem ellenőrizhető";
}
public sealed class HostStatus
{
    public LegacyStoreV1PeerProof? OriginalStoreV1 {get;set;}
    public string HostId { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTimeOffset ObservedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<PublicMapStatus> Maps { get; set; } = new();
}
public sealed class WireEnvelope
{
    public long UnixSeconds { get; set; }
    public string Nonce { get; set; } = "";
    public string PayloadBase64 { get; set; } = "";
    public string SignatureHex { get; set; } = "";
}
public static class SecureStore
{
    // DPAPI binds secrets to the Windows user account. No cleartext credentials in JSON.
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }
    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        var data = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}
public sealed class ConfigStore
{
    private string? expectedHash;
    private static string? Digest(string path)=>File.Exists(path)?
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):null;
    public string Root { get; }
    public string FilePath => Path.Combine(Root, "cluster-config.json");
    public ConfigStore(string profile = "default", string? rootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 40 ||
            profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Invalid local profile name", nameof(profile));
        Root = rootOverride ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "UntamedManager", "Profiles", profile);
        UntamedOperationCoordinator.ResourceKey(Root);
        Directory.CreateDirectory(Root);
        expectedHash=Digest(FilePath);
    }
    public ManagerConfig Read()
    {
        if (!File.Exists(FilePath)){expectedHash=null;return new ManagerConfig();}
        byte[] bytes=File.ReadAllBytes(FilePath);
        expectedHash=Convert.ToHexString(SHA256.HashData(bytes));
        return JsonSerializer.Deserialize<ManagerConfig>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) ??
               throw new InvalidDataException("Config JSON invalid");
    }
    // Explicit first-use creation precedes IPC/ownership initialization.
    // Existing profiles and subsequent status reads are never rewritten here.
    public ManagerConfig EnsureInitialized()
    {
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(FilePath).ToUpperInvariant())));
        using var gate=new Mutex(false,"Global\\Untamed-Config-"+key);bool entered=false;
        try
        {
            try{entered=gate.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("Profile initialization busy");
            UntamedOperationCoordinator.ResourceKey(Root);
            if(!File.Exists(FilePath))
            {
                var initial=new ManagerConfig();
                string temporary=FilePath+"."+Guid.NewGuid().ToString("N")+".initial";
                try
                {
                    using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {JsonSerializer.Serialize(file,initial);file.Flush(true);}
                    File.Move(temporary,FilePath,false);
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
            return Read();
        }
        finally{if(entered)gate.ReleaseMutex();}
    }

    public void Save(ManagerConfig cfg)
    {
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(FilePath).ToUpperInvariant())));
        using var gate=new Mutex(false,"Global\\Untamed-Config-"+key);
        bool entered=false;
        string? temp=null;
        try
        {
        try{entered=gate.WaitOne(0);}catch(AbandonedMutexException){entered=true;}
        if(!entered)throw new IOException("UNTAMED_CONFIG_BUSY");
        if(Digest(FilePath)!=expectedHash)throw new IOException("UNTAMED_CONFIG_CHANGED_RELOAD_REQUIRED");
        Directory.CreateDirectory(Root);
        // Prevent accidental replacement with another machine's configuration.
        if (string.IsNullOrWhiteSpace(cfg.LocalHostId)) throw new InvalidDataException("Missing host ID");
        if (cfg.LocalMaps.Select(x=>x.Id).Distinct().Count() != cfg.LocalMaps.Count)
            throw new InvalidDataException("Duplicate map IDs");
        foreach (var map in cfg.LocalMaps)
        {
            if (string.IsNullOrWhiteSpace(map.Name)) throw new InvalidDataException("Map name empty");
            if (map.GamePort is < 0 or > 65535 || map.QueryPort is < 0 or > 65535 ||
                map.RconPort is < 0 or > 65535) throw new InvalidDataException("Invalid map port");
        }
        temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented=true }), Encoding.UTF8);
        if (File.Exists(FilePath))
        {
            string backup = Path.Combine(Root, "cluster-config.previous.json");
            File.Replace(temp, FilePath, backup, true);
        }
        else File.Move(temp, FilePath);
        expectedHash=Digest(FilePath);
        }
        finally
        {
            try{if(temp!=null&&File.Exists(temp))File.Delete(temp);}
            finally{if(entered)gate.ReleaseMutex();}
        }
    }
}
public sealed record ProcessCheck(bool Running, string Status, ProcessIdentity? Identity,
    bool ConfirmedStopped = false, ProcessIdentity? LoaderIdentity = null);

public sealed partial class WindowsProcessInspector
{
    public ProcessCheck Inspect(LocalMap map)
    {
        if (string.IsNullOrWhiteSpace(map.ExecutablePath) || !File.Exists(map.ExecutablePath))
            return new ProcessCheck(false, "Nincs érvényes helyi végrehajtható fájl", null);
        var matches = new List<ProcessIdentity>();
        bool uncertain = false;
        string exe = Path.GetFullPath(map.ExecutablePath).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            using var processes = searcher.Get();
            foreach (ManagementObject entry in processes)
            {
                try
                {
                    string path = Convert.ToString(entry["ExecutablePath"]) ?? "";
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(Convert.ToString(entry["Name"]), Path.GetFileName(exe)))
                            uncertain = true;
                        continue;
                    }
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), exe)) continue;
                    string command = Convert.ToString(entry["CommandLine"]) ?? "";
                    // An installation with changed or unreadable arguments is not proven stopped.
                    if (!CommandMatches(command, exe, map.Arguments)) { uncertain = true; continue; }
                    int id = Convert.ToInt32(entry["ProcessId"]);
                    using var p = Process.GetProcessById(id);
                    if (p.HasExited) continue;
                    var identity = new ProcessIdentity {
                        Pid=id,
                        StartedUtc = new DateTimeOffset(p.StartTime.ToUniversalTime()),
                        ExecutablePath=exe, Arguments=map.Arguments
                    };
                    // If a tracked process still exists, require matching creation time: never trust PID alone.
                    if (map.LastProcess != null && map.LastProcess.Pid == id &&
                        Math.Abs((identity.StartedUtc - map.LastProcess.StartedUtc).TotalSeconds) > 2)
                    { uncertain = true; continue; }
                    matches.Add(identity);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception or ArgumentException)
                { uncertain = true; }
            }
        }
        catch (Exception ex) { return new ProcessCheck(false, "WMI folyamatellenőrzés hiba: " + ex.Message, null); }
        if (uncertain) return new ProcessCheck(false,"Folyamatállapot ismeretlen – műveletek tiltva",null);
        return matches.Count switch {
            0 => new ProcessCheck(false,"Leállítva · folyamatlista ellenőrizve",null,true),
            1 => new ProcessCheck(true,"Fut · folyamat ellenőrizve",matches[0]),
            _ => new ProcessCheck(false,"Több azonos folyamat – manuális azonosítás szükséges",null)
        };
    }
    // Comparison uses complete command-line suffix, never a loose substring map name.
    public static bool CommandMatches(string commandLine, string exe, string requiredArguments)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        string prefix = commandLine.TrimStart();
        if (prefix.StartsWith('"'))
        {
            int i = prefix.IndexOf('"',1);
            if (i < 0) return false;
            string actualExe = prefix.Substring(1,i-1);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(actualExe), exe)) return false;
            prefix=prefix.Substring(i+1).Trim();
        }
        else
        {
            if (!prefix.StartsWith(exe, StringComparison.OrdinalIgnoreCase)) return false;
            if (prefix.Length > exe.Length && !char.IsWhiteSpace(prefix[exe.Length])) return false;
            prefix = prefix.Substring(exe.Length).Trim();
        }
        return string.Equals(prefix, requiredArguments.Trim(), StringComparison.Ordinal);
    }
    public ProcessIdentity Launch(LocalMap map)
    {
        if (string.IsNullOrWhiteSpace(map.ExecutablePath)) throw new InvalidOperationException("Missing executable path");
        var launch=AsaApiLaunchPolicy.Resolve(map);
        string exe=launch.ExecutablePath,arguments=launch.Arguments;
        if (!File.Exists(exe)) throw new FileNotFoundException("ASA server executable not found",exe);
        // No Windows job object, no kill-on-dispose. Server survives Manager UI exit/update.
        var psi = new ProcessStartInfo(exe,arguments) {
            WorkingDirectory=launch.UseApi ? Path.GetDirectoryName(exe)! :
                string.IsNullOrWhiteSpace(map.InstallationPath) ?
                Path.GetDirectoryName(exe)! : Path.GetFullPath(map.InstallationPath),
            UseShellExecute=false, CreateNoWindow=true
        };
        using var process=Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        return new ProcessIdentity {
            Pid=process.Id, StartedUtc=new DateTimeOffset(process.StartTime.ToUniversalTime()),
            ExecutablePath=exe, Arguments=arguments
        };
    }
}
public sealed class PeerStatusListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<HostStatus> _status;
    private readonly byte[] _key;
    private readonly CancellationTokenSource _cts=new();
    private readonly IPAddress? _expectedRemoteIp;
    private int _activeRequests;
    private const int MaximumConcurrentRequests=4;
    private readonly Dictionary<string,long> _seen = new();
    private readonly object _lock=new();
    private Task? _loop;
    private int _disposed;
    public PeerStatusListener(int port, string sharedSecret, Func<HostStatus> getStatus,
        string? expectedRemoteAddress=null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (sharedSecret.Length < 24) throw new ArgumentException("Pairing secret must be at least 24 characters");
        // Reject other LAN hosts before parsing even an authenticated request.
        // Listening on an explicit local interface / Windows firewall scope is
        // additionally recommended in production.
        if(!string.IsNullOrWhiteSpace(expectedRemoteAddress))
        {
            if(!IPAddress.TryParse(expectedRemoteAddress,out var remote) ||
               remote.AddressFamily!=AddressFamily.InterNetwork)
                throw new InvalidDataException("Expected LAN peer must have a literal IPv4 address");
            _expectedRemoteIp=remote;
        }
        _listener=new TcpListener(IPAddress.Any,port);
        _key=SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));
        _status=getStatus;
    }
    public void Start() { _listener.Start(); _loop=Task.Run(AcceptAsync); }
    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try {
                var socket=await _listener.AcceptTcpClientAsync(_cts.Token);
                // No arbitrary host can reserve the scarce request slots.
                if(_expectedRemoteIp!=null &&
                   (socket.Client.RemoteEndPoint is not IPEndPoint source ||
                    !source.Address.Equals(_expectedRemoteIp)))
                {
                    socket.Dispose();
                    continue;
                }
                if(Interlocked.Increment(ref _activeRequests)>MaximumConcurrentRequests)
                {
                    Interlocked.Decrement(ref _activeRequests);
                    socket.Dispose();
                    continue;
                }
                _=Task.Run(async()=>
                {
                    try { using(socket) await Handle(socket); }
                    catch(Exception) { /* Untrusted LAN inputs never escape the listener. */ }
                    finally { Interlocked.Decrement(ref _activeRequests); }
                });
            }
            catch (OperationCanceledException) {break;}
            catch (SocketException) when (_cts.IsCancellationRequested) {break;}
        }
    }
    private async Task Handle(TcpClient socket)
    {
        socket.ReceiveTimeout=4000;socket.SendTimeout=4000;
        // TCP ReceiveTimeout does not bound asynchronous ReadExactlyAsync:
        // a malicious slow sender must not pin a request indefinitely.
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var stream=socket.GetStream();
        var frame=JsonSerializer.Deserialize<WireEnvelope>(await Framing.Read(stream,deadline.Token))
            ?? throw new InvalidDataException("Missing frame");
        if (!Verify(frame)) return;
        byte[] request=Convert.FromBase64String(frame.PayloadBase64);
        // LAN requests are strictly read-only again. Remote update commands
        // cannot affect this Manager's independent local maintenance queue.
        if(Encoding.UTF8.GetString(StatusCipher.Decrypt(_key,request))!="GET_STATUS")return;
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(_status());
        await Framing.Write(stream,
            JsonSerializer.SerializeToUtf8Bytes(Sign(StatusCipher.Encrypt(_key,payload),frame.Nonce)),
            deadline.Token);
    }
    private WireEnvelope Sign(byte[] payload, string requestNonce)
    {
        var f=new WireEnvelope { UnixSeconds=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Nonce=requestNonce,PayloadBase64=Convert.ToBase64String(payload)};
        f.SignatureHex=Convert.ToHexString(HMACSHA256.HashData(_key,Encoding.UTF8.GetBytes(
            $"{f.UnixSeconds}\n{f.Nonce}\n{f.PayloadBase64}")));
        return f;
    }
    private bool Verify(WireEnvelope f)
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now-f.UnixSeconds)>45 || f.Nonce.Length!=32 || f.PayloadBase64.Length>240000) return false;
        byte[] expected=HMACSHA256.HashData(_key,Encoding.UTF8.GetBytes(
            $"{f.UnixSeconds}\n{f.Nonce}\n{f.PayloadBase64}"));
        byte[] claimed;
        try {claimed=Convert.FromHexString(f.SignatureHex);}catch{return false;}
        if(!CryptographicOperations.FixedTimeEquals(expected,claimed))return false;
        lock(_lock) {
            foreach(var expired in _seen.Where(x=>x.Value<now-60).Select(x=>x.Key).ToArray())_seen.Remove(expired);
            if (_seen.ContainsKey(f.Nonce) || _seen.Count>=4096)return false;
            _seen[f.Nonce]=now;
        }
        return true;
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _cts.Cancel();_listener.Stop();
        if(_loop!=null)try{await _loop;}catch(OperationCanceledException){}
        _cts.Dispose();
    }
}
public static class Framing
{
    public static async Task<byte[]> Read(Stream stream,CancellationToken ct)
    {
        var header=new byte[4];
        await stream.ReadExactlyAsync(header,ct);
        int length=IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header));
        if(length<=0 || length>256000)throw new InvalidDataException("Invalid LAN message length");
        byte[] buffer=new byte[length];await stream.ReadExactlyAsync(buffer,ct);return buffer;
    }
    public static async Task Write(Stream stream,byte[] data,CancellationToken ct)
    {
        if(data.Length>256000)throw new InvalidDataException("Frame too large");
        var header=BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
        await stream.WriteAsync(header,ct);await stream.WriteAsync(data,ct);await stream.FlushAsync(ct);
    }
}
public static class PeerStatusClient
{
    public static async Task<HostStatus> Query(string host,int port,string sharedSecret,
        CancellationToken cancellationToken=default)
    {
        if (sharedSecret.Length<24) throw new ArgumentException("Pairing key too short");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var ct=timeout.Token;
        var key=SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));
        var f=new WireEnvelope{UnixSeconds=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            PayloadBase64=Convert.ToBase64String(StatusCipher.Encrypt(key, Encoding.UTF8.GetBytes("GET_STATUS")))};
        f.SignatureHex=Convert.ToHexString(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(
            $"{f.UnixSeconds}\n{f.Nonce}\n{f.PayloadBase64}")));
        using var socket=new TcpClient();
        await socket.ConnectAsync(host,port,ct);
        using var stream=socket.GetStream();
        await Framing.Write(stream,JsonSerializer.SerializeToUtf8Bytes(f),ct);
        var response=JsonSerializer.Deserialize<WireEnvelope>(await Framing.Read(stream,ct))
            ?? throw new InvalidDataException("Empty LAN response");
        if(response.Nonce != f.Nonce || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-response.UnixSeconds)>45)
            throw new InvalidDataException("LAN response clock skew");
        byte[] digest=HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(
            $"{response.UnixSeconds}\n{response.Nonce}\n{response.PayloadBase64}"));
        if (!CryptographicOperations.FixedTimeEquals(digest,Convert.FromHexString(response.SignatureHex)))
            throw new InvalidDataException("LAN response signature mismatch");
        var status=JsonSerializer.Deserialize<HostStatus>(
            StatusCipher.Decrypt(key,Convert.FromBase64String(response.PayloadBase64)))
            ?? throw new InvalidDataException("Invalid status response");
        // Frame size alone does not validate an authenticated peer's data schema.
        if(!Guid.TryParse(status.HostId,out var remoteId) || remoteId==Guid.Empty ||
           string.IsNullOrWhiteSpace(status.HostName) || status.HostName.Length>128 ||
           status.Maps==null || status.Maps.Count>25 ||
           status.Maps.Any(m=>m==null || !Guid.TryParse(m.Id,out var id) ||
               id==Guid.Empty || string.IsNullOrWhiteSpace(m.Name) ||
               m.Name.Length>160 || m.Status==null || m.Status.Length>512 ||
               m.GamePort is <0 or >65535) ||
           status.Maps.Select(m=>m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
               !=status.Maps.Count)
            throw new InvalidDataException("LAN status schema invalid");
        return status;
    }
}
public sealed class ClusterService : IAsyncDisposable
{
    public UntamedOperationCoordinator Operations {get;}
    public ConfigStore Store { get; }
    public ManagerConfig Config { get; private set; }
    private readonly WindowsProcessInspector _inspector=new();
    private readonly Func<LocalMap, ProcessCheck> _inspect;
    private PeerStatusListener? _listener;
    private bool _remoteReachable;
    public bool RemoteIsFresh=>_remoteReachable && RemoteSnapshot!=null && StatusFreshness.IsFresh(RemoteSnapshot.ObservedUtc,DateTimeOffset.UtcNow);
    public HostStatus? RemoteSnapshot { get; private set; }
    public string RemoteMessage { get; private set; }="Nincs peer beállítva";
    public ClusterService(string profile = "default",string? rootOverride=null,
        Func<LocalMap, ProcessCheck>? inspect = null)
    {
        Store=new ConfigStore(profile,rootOverride);
        Operations=new UntamedOperationCoordinator();
        Config=Store.EnsureInitialized();
        _inspect=inspect ?? _inspector.Inspect;
    }
    public void Save() => Store.Save(Config);

    /// <summary>
    /// Commit only settings through the existing optimistic ConfigStore
    /// transaction. The callback sees an isolated staged copy: an exception,
    /// competing Manager update or filesystem failure cannot mutate the live
    /// config, map instances, Store ownership or scheduler state in memory.
    /// </summary>
    public void UpdateSettings(Action<ManagerConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ManagerConfig staged=JsonSerializer.Deserialize<ManagerConfig>(
            JsonSerializer.Serialize(Config)) ??
            throw new InvalidDataException("Unable to stage Manager settings");
        update(staged);

        // Keep schedule and map mutation on their dedicated, guarded paths.
        // These collections contain live object identities; replacing them
        // would silently invalidate the selected map and pending UI drafts.
        if(staged.LocalHostId!=Config.LocalHostId ||
           JsonSerializer.Serialize(staged.LocalMaps)!=
               JsonSerializer.Serialize(Config.LocalMaps) ||
           JsonSerializer.Serialize(staged.ScheduledTasks)!=
               JsonSerializer.Serialize(Config.ScheduledTasks) ||
           JsonSerializer.Serialize(staged.RecurringSchedules)!=
               JsonSerializer.Serialize(Config.RecurringSchedules))
            throw new InvalidOperationException(
                "UpdateSettings cannot mutate host identity, maps or schedules");

        // Save checks the original digest and writes atomically. On failure
        // the original Config object has never been altered.
        Store.Save(staged);

        // Preserve Config, LocalMaps and task-list object identity for
        // selection, reattach, LAN polling and in-memory draft sessions.
        Config.LocalHostName=staged.LocalHostName;
        Config.ListenPort=staged.ListenPort;
        Config.PeerAddress=staged.PeerAddress;
        Config.PeerPort=staged.PeerPort;
        Config.ExpectedPeerHostId=staged.ExpectedPeerHostId;
        Config.PairingKeyProtected=staged.PairingKeyProtected;
        Config.CurseForgeApiKeyProtected=staged.CurseForgeApiKeyProtected;
        Config.DiscordWebhookProtected=staged.DiscordWebhookProtected;
        Config.GitHubRepository=staged.GitHubRepository;
        Config.UpdateChannel=staged.UpdateChannel;
        Config.PublisherCertificateThumbprint=staged.PublisherCertificateThumbprint;
        Config.TelemetryHistoryEnabled=staged.TelemetryHistoryEnabled;
        Config.SteamCmdExecutable=staged.SteamCmdExecutable;
        Config.AutoSteamUpdatesEnabled=staged.AutoSteamUpdatesEnabled;
        Config.SteamCheckIntervalMinutes=staged.SteamCheckIntervalMinutes;
        Config.SteamMaintenanceStart=staged.SteamMaintenanceStart;
        Config.SteamMaintenanceEnd=staged.SteamMaintenanceEnd;
        Config.SteamWarnPlayers=staged.SteamWarnPlayers;
        Config.SteamWaitPlayersMinutes=staged.SteamWaitPlayersMinutes;
        Config.SteamRestartAfterUpdate=staged.SteamRestartAfterUpdate;
        Config.SteamPostUpdateProbeMinutes=staged.SteamPostUpdateProbeMinutes;
    }
    public void AddMap(LocalMap map)
    {
        // Names are user-supplied; the installed game's current map list is not a cap.
        map.Name=LocalMapNameRules.Normalize(map.Name);
        if(Config.LocalMaps.Any(m=>m.Id==map.Id))throw new InvalidOperationException("Duplicate map");
        if(Config.LocalMaps.Any(m=>m.Name.Equals(map.Name,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Map is already registered locally");
        Config.LocalMaps.Add(map);
        try{Save();}
        catch
        {
            // A failed config CAS/write must not leave an unpersisted map visible
            // to UI, process observation, scheduler, or later config saves.
            Config.LocalMaps.Remove(map);
            throw;
        }
    }
    public void ReplaceMap(LocalMap existing,LocalMap replacement)
    {
        // An entirely staged candidate is persisted atomically. The original
        // object and any active observers remain unchanged if the save fails.
        int index=Config.LocalMaps.IndexOf(existing);
        if(index<0 || replacement.Id!=existing.Id)
            throw new UnauthorizedAccessException("Map identity changed or is not registered locally");
        replacement.Name=LocalMapNameRules.Normalize(replacement.Name);
        if(Config.LocalMaps.Any(m=>m.Id!=existing.Id &&
            m.Name.Equals(replacement.Name,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Map name is already registered locally");
        Config.LocalMaps[index]=replacement;
        try{Save();}
        catch
        {
            Config.LocalMaps[index]=existing;
            throw;
        }
    }
    public void RemoveMap(Guid id)
    {
        int index=Config.LocalMaps.FindIndex(m=>m.Id==id);
        if(index<0)throw new KeyNotFoundException("Map not found locally");
        var map=Config.LocalMaps[index];
        Config.LocalMaps.RemoveAt(index);
        try{Save();}
        catch
        {
            // Keep original order and object identity after optimistic-CAS failure.
            Config.LocalMaps.Insert(index,map);
            throw;
        }
    }
    /// <summary>
    /// Strictly read-only physical process observation for evidence collection.
    /// Unlike Check(), this must not conflate a blocked Store-v1 launch policy
    /// with a running/unknown Windows process. It grants no launch, Stop,
    /// RCON, ownership, or Store-write capability. A missing executable,
    /// unreadable process family, unknown PID or unmatched command line
    /// must remain unconfirmed (never synthetic "stopped").
    /// </summary>
    public ProcessCheck ObserveLocalProcessesReadOnly(LocalMap map)
    {
        if(map==null || !Config.LocalMaps.Contains(map))
            throw new UnauthorizedAccessException("Only locally registered maps can be observed");
        if(map.StartWithAsaApi || !string.IsNullOrWhiteSpace(map.ApiLoaderPath))
            return _inspector.InspectFamily(map);
        return _inspect(map);
    }

    public ProcessCheck Check(LocalMap map)
    {
        if(!Config.LocalMaps.Contains(map)) throw new UnauthorizedAccessException("Not a local map");
        if(map.HistoricalStoreProtected)
            return new(false,"Történeti Store-v1 map: indítás tiltva a teljes eredeti launch/szinkron validációjáig",null);
        if((map.Arguments+" "+map.ApiLoaderArguments).Contains("converttostore",StringComparison.OrdinalIgnoreCase))
            return new(false,"Store conversion is forbidden",null);
        if(map.Arguments.Contains("-usestore",StringComparison.OrdinalIgnoreCase))
        {
            var store=LocalStoreProbe.Inspect(map.StoreTestRoot,VaultProtocol.ClusterId,map.Arguments);
            if(!store.Safe)return new(false,store.Reason,null);
        }
        if(string.IsNullOrWhiteSpace(map.ApiLoaderPath)&&Path.GetFileName(map.ExecutablePath).Contains("loader",StringComparison.OrdinalIgnoreCase))
            return new(false,"Configure loader and child executable separately",null);
        // Observation is strictly read-only: a dashboard refresh, LAN status query
        // or watchdog poll must never modify cluster-config.json.
        if(map.StartWithAsaApi)
        {
            // A saved preference is not proof that loader binaries are usable.
            try { AsaApiLaunchPolicy.Resolve(map); }
            catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { return new(false,"ASA API indítási előfeltétel hiányzik: "+ex.Message,null); }
        }
        if(!string.IsNullOrWhiteSpace(map.ApiLoaderPath))
        {
            // Even after opting out, a previous loader child may still be alive.
            // A direct start must not bypass the old family observation.
            var family=_inspector.InspectFamily(map);
            if(map.StartWithAsaApi || family.Running || !family.ConfirmedStopped)
                return family;
        }
        return _inspect(map);
    }
    private static bool SameIdentity(ProcessIdentity? left,ProcessIdentity? right)
    {
        if(left==null||right==null)return left==null&&right==null;
        return left.Pid==right.Pid && left.StartedUtc==right.StartedUtc &&
            string.Equals(left.ExecutablePath,right.ExecutablePath,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Arguments,right.Arguments,StringComparison.Ordinal);
    }

    // Explicit operator-requested persistence, not a side effect of Check().
    // Re-observe under the installation-wide operation lease to prevent accepting
    // stale PIDs, a replaced loader or another Manager's changing state.
    public bool RecordObservedIdentity(Guid mapId,ProcessCheck expected)
    {
        var map=Config.LocalMaps.SingleOrDefault(x=>x.Id==mapId) ??
            throw new UnauthorizedAccessException("Map is not owned by this Manager");
        if(expected==null||!expected.Running||expected.Identity==null||expected.Identity.Pid<=0)
            throw new InvalidOperationException("A running, identified local process is required");
        using var operation=Operations.Acquire(map.InstallationPath,UntamedOperationKind.Configuration);
        var current=Check(map);
        if(!current.Running||current.Identity==null||
           !SameIdentity(expected.Identity,current.Identity)||
           !SameIdentity(expected.LoaderIdentity,current.LoaderIdentity))
            throw new InvalidOperationException("Process identity changed; repeat the observation");
        if(SameIdentity(map.LastProcess,current.Identity)&&SameIdentity(map.LastLoader,current.LoaderIdentity))
        {
            operation.Complete();
            return false;
        }
        var previousProcess=map.LastProcess;
        var previousLoader=map.LastLoader;
        operation.MarkMutationStarted();
        map.LastProcess=current.Identity;
        map.LastLoader=current.LoaderIdentity;
        try { Save(); }
        catch
        {
            map.LastProcess=previousProcess;
            map.LastLoader=previousLoader;
            throw;
        }
        operation.Complete();
        return true;
    }
    // Genuine local ASA start for a NEW SteamCMD installation only. This is
    // deliberately separate from historical Store-v1 imports and FakeAsa.
    // No automatic server restart or recovery is authorized here. A successful
    // Process.Start is not evidence of RCON/ASA client readiness.
    public ProcessIdentity LaunchFreshAsa(Guid id,bool operatorApproved,Guid? approvedRestartId=null)
    {
        var map=Config.LocalMaps.SingleOrDefault(x=>x.Id==id) ??
            throw new UnauthorizedAccessException("Only local registered maps may start");
        if(KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(map) || map.Maintenance ||
           !string.IsNullOrWhiteSpace(map.StoreTestRoot) ||
           (map.Arguments+" "+map.ApiLoaderArguments).Contains("store",
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Store-v1/historical launch remains locked");
        if(map.Id==Guid.Empty || map.GamePort is <1024 or >65535 ||
           map.QueryPort is <1024 or >65535 ||
           string.IsNullOrWhiteSpace(map.Arguments) ||
           !Path.IsPathFullyQualified(map.InstallationPath) ||
           map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal) ||
           !Path.IsPathFullyQualified(map.ExecutablePath))
            throw new InvalidDataException("New ASA installation has incomplete local setup");
        string root=Path.GetFullPath(map.InstallationPath);
        var restartJournal=new AsaManualRestartJournal(root);
        restartJournal.DemandAuthorizedStart(approvedRestartId);
        string server=Path.Combine(root,"ShooterGame","Binaries","Win64",
            "ArkAscendedServer.exe");
        if(!string.Equals(Path.GetFullPath(map.ExecutablePath),server,
            StringComparison.OrdinalIgnoreCase) || !File.Exists(server))
            throw new InvalidDataException("Expected native ASA executable not found");
        if(!AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidDataException("ASA binary path must be a local fixed drive without symlinks");
        var build=AsaSteamBuildCatalog.InstalledBuild(root);
        if(string.IsNullOrWhiteSpace(build))
            throw new InvalidDataException("Official SteamCMD ASA build manifest is missing");
        FreshAsaInstallationSafety.Demand(this,map);
        if(!AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidDataException("UM-FRESH-ASA-RCON-CONFIG-DIVERGED");
        LocalMapPortPolicy.RequireNoConflicts(map,Config.LocalMaps);
        if(!AsaLocalPortPreflight.CanStart(map))
            throw new InvalidOperationException("UM-ASA-START-LOCAL-PORT-IN-USE");
        var launch=AsaApiLaunchPolicy.Resolve(map);
        if(!string.Equals(launch.Arguments,map.Arguments,StringComparison.Ordinal))
            throw new InvalidDataException("Unverified server launch argument set");
        var before=Check(map);
        if(!ReleaseCapabilityGate.Check(ManagerOperation.RealAsaStart,true,
            map.Maintenance,before.ConfirmedStopped,before.Running,
            verifiedFreshInstall:true,explicitOperatorApproval:operatorApproved).Allowed ||
           before.Identity!=null || before.LoaderIdentity!=null)
            throw new InvalidOperationException("ASA start requires a confirmed stopped local map");
        if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidOperationException("Unresolved restore prevents ASA start");
        using var lease=Operations.Acquire(root,UntamedOperationKind.Launch);
        restartJournal.DemandAuthorizedStart(approvedRestartId);
        // Reject changed disk/host conditions after the installation-wide
        // operation lock, immediately before spawning any process.
        FreshAsaInstallationSafety.Demand(this,map);
        if(!AsaRconConfigurationEvidence.Matches(map))
            throw new InvalidDataException("UM-FRESH-ASA-RCON-CONFIG-DIVERGED");
        LocalMapPortPolicy.RequireNoConflicts(map,Config.LocalMaps);
        if(!AsaLocalPortPreflight.CanStart(map) ||
           AsaSteamBuildCatalog.InstalledBuild(root)!=build ||
           !AsaReadyProbe.IsRegisteredServerBinary(map))
            throw new InvalidOperationException("UM-ASA-START-PREFLIGHT-CHANGED");
        var current=Check(map);
        if(!ReleaseCapabilityGate.Check(ManagerOperation.RealAsaStart,true,
            map.Maintenance,current.ConfirmedStopped,current.Running,
            verifiedFreshInstall:true,explicitOperatorApproval:operatorApproved).Allowed ||
           current.Identity!=null||current.LoaderIdentity!=null)
            throw new InvalidOperationException("ASA process state changed before launch");
        // Mark BEFORE spawn. An interrupted start must never be retried
        // automatically; the operator can reattach to its exact process.
        lease.MarkMutationStarted();
        var identity=_inspector.Launch(map);
        // The server may outlive the Manager UI. The durable identity is used
        // to recognize the original process and reject PID reuse.
        if(map.StartWithAsaApi)map.LastLoader=identity;else map.LastProcess=identity;
        Save();
        lease.Complete();
        return identity;
    }

    public ProcessIdentity LaunchLocal(Guid id)
    {
        var map=Config.LocalMaps.SingleOrDefault(x=>x.Id==id) ??
            throw new UnauthorizedAccessException("Map is not owned by this Manager");
        // RC safety enforcement belongs to the service, not merely the WPF button:
        // prevent timers, other UI components or direct method callers from
        // launching actual ArkAscendedServer.exe before production validation.
        if(map.HistoricalStoreProtected)
            throw new InvalidOperationException("Historical Store-v1 profile is locked until full launch parity validation");
        ReleaseCapabilityGate.DemandFakeAsaStart(this,map);
        if(KovaCompatibleWorldBackup.InstallationRecoveryRequired(map))
            throw new InvalidOperationException("UM-ASA-START-BLOCKED-BY-RESTORE-RECOVERY");
        using var operation=Operations.Acquire(map.InstallationPath,UntamedOperationKind.Launch);
            if(map.Maintenance)throw new InvalidOperationException("Maintenance mode blocks launch");
            if (!Check(map).ConfirmedStopped)
                throw new InvalidOperationException("Server is running or its stopped state cannot be verified");
            operation.MarkMutationStarted();
            var identity=_inspector.Launch(map);
            if(map.StartWithAsaApi)map.LastLoader=identity;else map.LastProcess=identity;
            Save();operation.Complete();return identity;
    }
    public HostStatus LocalStatus()
    {
        return new HostStatus{
            OriginalStoreV1=LegacyStoreV1PeerProof.FromCurrentUserLegacyProfile(),
            HostId=Config.LocalHostId,HostName=Config.LocalHostName,ObservedUtc=DateTimeOffset.UtcNow,
            Maps=Config.LocalMaps.Select(map=>{
                var c=Check(map);
                string? build=null;
                string steamState="Nem ellenőrizhető";
                try
                {
                    build=AsaSteamBuildCatalog.InstalledBuild(map.InstallationPath);
                    var recorded=new SteamBuildStatusStore(Store.Root).Read(map);
                    steamState=recorded.State switch {
                        AsaSteamBuildState.Current=>"Naprakész",
                        AsaSteamBuildState.UpdateAvailable=>"Frissítés elérhető",
                        AsaSteamBuildState.Checking=>"Ellenőrzés folyamatban",
                        AsaSteamBuildState.Installing=>"Frissítés folyamatban",
                        AsaSteamBuildState.Restarting=>"Újraindítás folyamatban",
                        AsaSteamBuildState.Failed=>"Hiba történt",
                        _=>"Nem ellenőrizhető"
                    };
                }
                catch(Exception){/* peer observation cannot invent Steam evidence */}
                return new PublicMapStatus {Id=map.Id.ToString("N"), Name=map.Name,
                    Running=c.Running,Status=c.Status,GamePort=map.GamePort,
                    SteamBuildId=build,SteamUpdateState=steamState};
            }).ToList()
        };
    }
    public async Task BeginLanAsync()
    {
        if(_listener!=null)return;
        if(Config.ListenPort==0)return;
        string key=SecureStore.Unprotect(Config.PairingKeyProtected);
        if(key.Length<24)throw new InvalidOperationException("Configure shared pairing key first");
        if(string.IsNullOrWhiteSpace(Config.PeerAddress) ||
           !Guid.TryParse(Config.ExpectedPeerHostId,out var expectedId) ||
           expectedId==Guid.Empty || Config.ExpectedPeerHostId==Config.LocalHostId)
            throw new InvalidOperationException("LAN requires an explicitly pinned second host");
        _listener=new PeerStatusListener(Config.ListenPort,key,LocalStatus,Config.PeerAddress);
        _listener.Start();
        await Task.CompletedTask;
    }
    public async Task<HostStatus?> PollPeerAsync(CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(Config.PeerAddress)||Config.PeerPort==0){
            RemoteMessage="Peer nincs beállítva";RemoteSnapshot=null;return null;}
        try {
            if(!Guid.TryParse(Config.ExpectedPeerHostId,out var expectedId) ||
               expectedId==Guid.Empty ||
               !Guid.TryParse(Config.LocalHostId,out var localId) ||
               expectedId==localId)
                throw new InvalidDataException("LAN peer identity is not pinned");
            var snapshot=await PeerStatusClient.Query(Config.PeerAddress,Config.PeerPort,
                SecureStore.Unprotect(Config.PairingKeyProtected),ct);
            if(!Guid.TryParse(snapshot.HostId,out var returnedId) ||
               returnedId!=expectedId)
                throw new InvalidDataException("LAN peer HostId does not match the pinned device");
            if(!StatusFreshness.IsFresh(snapshot.ObservedUtc,DateTimeOffset.UtcNow))throw new InvalidDataException("Peer measurement is stale");
            RemoteSnapshot=snapshot;_remoteReachable=true;RemoteMessage="LAN kapcsolat működik";return snapshot;
        }
        catch(Exception ex) {
            RemoteMessage="A másik gép offline vagy nem hitelesíthető: "+ex.Message;
            _remoteReachable=false;return null; // Preserve last observation, explicitly stale.
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(_listener!=null)await _listener.DisposeAsync();
        // Deliberately never kill or dispose an ASA process here.
    }
}

