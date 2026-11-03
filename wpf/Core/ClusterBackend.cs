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
    public string LogFilePath { get; set; } = "";
    public int GamePort { get; set; }
    public int QueryPort { get; set; }
    public int RconPort { get; set; }
    public string RconHost { get; set; } = "127.0.0.1";
    public string RconPasswordProtected { get; set; } = "";
    public bool AutoStart { get; set; } = false;
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
    public int PeerPort { get; set; }
    public string PairingKeyProtected { get; set; } = "";
    public string GitHubRepository { get; set; } = "Gunter1122cloud/untamed-manager-releases";
    public string PublisherCertificateThumbprint { get; set; } = "";
    public List<LocalMap> LocalMaps { get; set; } = new();
}
public sealed class PublicMapStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    public string Status { get; set; } = "leállítva";
    public int GamePort { get; set; }
}
public sealed class HostStatus
{
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
    public string Root { get; }
    public string FilePath => Path.Combine(Root, "cluster-config.json");
    public ConfigStore(string profile = "default", string? rootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 40 ||
            profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Invalid local profile name", nameof(profile));
        Root = rootOverride ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "UntamedManager", "Profiles", profile);
        Directory.CreateDirectory(Root);
    }
    public ManagerConfig Read()
    {
        if (!File.Exists(FilePath)) return new ManagerConfig();
        return JsonSerializer.Deserialize<ManagerConfig>(File.ReadAllText(FilePath)) ??
               throw new InvalidDataException("Config JSON invalid");
    }
    public void Save(ManagerConfig cfg)
    {
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
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented=true }), Encoding.UTF8);
        if (File.Exists(FilePath))
        {
            string backup = Path.Combine(Root, "cluster-config.previous.json");
            File.Replace(temp, FilePath, backup, true);
        }
        else File.Move(temp, FilePath);
    }
}
public sealed record ProcessCheck(bool Running, string Status, ProcessIdentity? Identity);

public sealed class WindowsProcessInspector
{
    public ProcessCheck Inspect(LocalMap map)
    {
        if (string.IsNullOrWhiteSpace(map.ExecutablePath) || !File.Exists(map.ExecutablePath))
            return new ProcessCheck(false, "Nincs érvényes helyi végrehajtható fájl", null);
        var matches = new List<ProcessIdentity>();
        string exe = Path.GetFullPath(map.ExecutablePath).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process");
            using var processes = searcher.Get();
            foreach (ManagementObject entry in processes)
            {
                try
                {
                    string path = Convert.ToString(entry["ExecutablePath"]) ?? "";
                    if (string.IsNullOrWhiteSpace(path) ||
                        !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), exe)) continue;
                    string command = Convert.ToString(entry["CommandLine"]) ?? "";
                    if (!CommandMatches(command, exe, map.Arguments)) continue;
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
                        continue;
                    matches.Add(identity);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception or ArgumentException) {}
            }
        }
        catch (Exception ex) { return new ProcessCheck(false, "WMI folyamatellenőrzés hiba: " + ex.Message, null); }
        return matches.Count switch {
            0 => new ProcessCheck(false,"Leállítva vagy nem azonosítható",null),
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
            prefix = prefix.Substring(exe.Length).Trim();
        }
        return string.Equals(prefix, requiredArguments.Trim(), StringComparison.Ordinal);
    }
    public ProcessIdentity Launch(LocalMap map)
    {
        if (string.IsNullOrWhiteSpace(map.ExecutablePath)) throw new InvalidOperationException("Missing executable path");
        var exe = Path.GetFullPath(map.ExecutablePath);
        if (!File.Exists(exe)) throw new FileNotFoundException("ASA server executable not found",exe);
        // No Windows job object, no kill-on-dispose. Server survives Manager UI exit/update.
        var psi = new ProcessStartInfo(exe,map.Arguments) {
            WorkingDirectory=string.IsNullOrWhiteSpace(map.InstallationPath) ?
                Path.GetDirectoryName(exe)! : Path.GetFullPath(map.InstallationPath),
            UseShellExecute=false, CreateNoWindow=true
        };
        using var process=Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        return new ProcessIdentity {
            Pid=process.Id, StartedUtc=new DateTimeOffset(process.StartTime.ToUniversalTime()),
            ExecutablePath=exe, Arguments=map.Arguments
        };
    }
}
public sealed class PeerStatusListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<HostStatus> _status;
    private readonly byte[] _key;
    private readonly CancellationTokenSource _cts=new();
    private readonly Dictionary<string,long> _seen = new();
    private readonly object _lock=new();
    private Task? _loop;
    public PeerStatusListener(int port, string sharedSecret, Func<HostStatus> getStatus)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (sharedSecret.Length < 24) throw new ArgumentException("Pairing secret must be at least 24 characters");
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
                _=Task.Run(async()=> { using(socket) try {await Handle(socket);}catch{} },_cts.Token);
            }
            catch (OperationCanceledException) {break;}
            catch (SocketException) when (_cts.IsCancellationRequested) {break;}
        }
    }
    private async Task Handle(TcpClient socket)
    {
        socket.ReceiveTimeout=4000;socket.SendTimeout=4000;
        using var stream=socket.GetStream();
        var frame=JsonSerializer.Deserialize<WireEnvelope>(await Framing.Read(stream,_cts.Token))
            ?? throw new InvalidDataException("Missing frame");
        if (!Verify(frame)) return;
        byte[] request=Convert.FromBase64String(frame.PayloadBase64);
        if (Encoding.UTF8.GetString(request) != "GET_STATUS") return;
        var payload=JsonSerializer.SerializeToUtf8Bytes(_status());
        await Framing.Write(stream, JsonSerializer.SerializeToUtf8Bytes(Sign(payload)),_cts.Token);
    }
    private WireEnvelope Sign(byte[] payload)
    {
        var f=new WireEnvelope { UnixSeconds=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),PayloadBase64=Convert.ToBase64String(payload)};
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
            if (_seen.ContainsKey(f.Nonce))return false;
            _seen[f.Nonce]=now;
        }
        return true;
    }
    public async ValueTask DisposeAsync()
    {
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
            PayloadBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes("GET_STATUS"))};
        f.SignatureHex=Convert.ToHexString(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(
            $"{f.UnixSeconds}\n{f.Nonce}\n{f.PayloadBase64}")));
        using var socket=new TcpClient();
        await socket.ConnectAsync(host,port,ct);
        using var stream=socket.GetStream();
        await Framing.Write(stream,JsonSerializer.SerializeToUtf8Bytes(f),ct);
        var response=JsonSerializer.Deserialize<WireEnvelope>(await Framing.Read(stream,ct))
            ?? throw new InvalidDataException("Empty LAN response");
        if(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-response.UnixSeconds)>45)
            throw new InvalidDataException("LAN response clock skew");
        byte[] digest=HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(
            $"{response.UnixSeconds}\n{response.Nonce}\n{response.PayloadBase64}"));
        if (!CryptographicOperations.FixedTimeEquals(digest,Convert.FromHexString(response.SignatureHex)))
            throw new InvalidDataException("LAN response signature mismatch");
        return JsonSerializer.Deserialize<HostStatus>(Convert.FromBase64String(response.PayloadBase64))
            ?? throw new InvalidDataException("Invalid status response");
    }
}
public sealed class ClusterService : IAsyncDisposable
{
    public ConfigStore Store { get; }
    public ManagerConfig Config { get; private set; }
    private readonly WindowsProcessInspector _inspector=new();
    private PeerStatusListener? _listener;
    public HostStatus? RemoteSnapshot { get; private set; }
    public string RemoteMessage { get; private set; }="Nincs peer beállítva";
    public ClusterService(string profile = "default",string? rootOverride=null)
    {
        Store=new ConfigStore(profile,rootOverride);
        Config=Store.Read();
    }
    public void Save() => Store.Save(Config);
    public void UpdateSettings(Action<ManagerConfig> update) {update(Config);Save();}
    public void AddMap(LocalMap map)
    {
        if(Config.LocalMaps.Any(m=>m.Id==map.Id))throw new InvalidOperationException("Duplicate map");
        if(Config.LocalMaps.Any(m=>m.Name.Equals(map.Name,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Map is already registered locally");
        Config.LocalMaps.Add(map);Save();
    }
    public void RemoveMap(Guid id)
    {
        if(Config.LocalMaps.RemoveAll(m=>m.Id==id)!=1)throw new KeyNotFoundException("Map not found locally");
        Save();
    }
    public ProcessCheck Check(LocalMap map)
    {
        if(!Config.LocalMaps.Contains(map)) throw new UnauthorizedAccessException("Not a local map");
        var check=_inspector.Inspect(map);
        if(check.Running && check.Identity!=null)
        {
            if(map.LastProcess?.Pid!=check.Identity.Pid ||
               map.LastProcess.StartedUtc!=check.Identity.StartedUtc)
            {map.LastProcess=check.Identity;Save();}
        }
        return check;
    }
    public ProcessIdentity LaunchLocal(Guid id)
    {
        var map=Config.LocalMaps.SingleOrDefault(x=>x.Id==id) ??
            throw new UnauthorizedAccessException("Map is not owned by this Manager");
        if (Check(map).Running)throw new InvalidOperationException("Server already running");
        var identity=_inspector.Launch(map);map.LastProcess=identity;Save();return identity;
    }
    public HostStatus LocalStatus()
    {
        return new HostStatus{
            HostId=Config.LocalHostId,HostName=Config.LocalHostName,ObservedUtc=DateTimeOffset.UtcNow,
            Maps=Config.LocalMaps.Select(map=>{
                var c=Check(map);
                return new PublicMapStatus {Id=map.Id.ToString("N"), Name=map.Name,
                    Running=c.Running,Status=c.Status,GamePort=map.GamePort};
            }).ToList()
        };
    }
    public async Task BeginLanAsync()
    {
        if(_listener!=null)return;
        if(Config.ListenPort==0)return;
        string key=SecureStore.Unprotect(Config.PairingKeyProtected);
        if(key.Length<24)throw new InvalidOperationException("Configure shared pairing key first");
        _listener=new PeerStatusListener(Config.ListenPort,key,LocalStatus);
        _listener.Start();
        await Task.CompletedTask;
    }
    public async Task<HostStatus?> PollPeerAsync(CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(Config.PeerAddress)||Config.PeerPort==0){
            RemoteMessage="Peer nincs beállítva";RemoteSnapshot=null;return null;}
        try {
            var snapshot=await PeerStatusClient.Query(Config.PeerAddress,Config.PeerPort,
                SecureStore.Unprotect(Config.PairingKeyProtected),ct);
            if(snapshot.HostId==Config.LocalHostId)
                throw new InvalidDataException("Peer host ID equals local host ID");
            RemoteSnapshot=snapshot;RemoteMessage="LAN kapcsolat működik";return snapshot;
        }
        catch(Exception ex) {
            RemoteMessage="A másik gép offline vagy nem hitelesíthető: "+ex.Message;
            RemoteSnapshot=null;return null; // Local manager must keep functioning.
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(_listener!=null)await _listener.DisposeAsync();
        // Deliberately never kill or dispose an ASA process here.
    }
}
