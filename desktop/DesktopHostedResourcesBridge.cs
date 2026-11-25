using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>Explicitly published text resources, loopback only, optionally encrypted at rest.
/// No arbitrary filesystem reads, proxying, shared Store access or ASA changes.</summary>
public sealed class DesktopHostedResourcesBridge : IDisposable
{
    private static readonly HashSet<string> Kinds=new(StringComparer.Ordinal){
        "AdminListURL","BanListURL","BadWordListURL","BadWordWhiteListURL",
        "CustomDynamicConfigUrl","CustomNotificationURL","CustomLiveTuningUrl"
    };
    private readonly object gate=new();
    private readonly bool allowEphemeral;
    private readonly ConcurrentDictionary<string,byte[]> resources=new(StringComparer.Ordinal);
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;
    private int port;
    private bool disposed;
    private readonly string? stateFile;
    private bool persistenceError;
    private sealed record SavedResources(int Port,Dictionary<string,string> Contents);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public DesktopHostedResourcesBridge(bool allowEphemeral=false,string? profileRoot=null)
    {
        this.allowEphemeral=allowEphemeral;
        if(profileRoot==null)return;
        stateFile=Path.Combine(Path.GetFullPath(profileRoot),"hosted-resources.dpapi");
        try
        {
            DemandSafePath();
            if(!File.Exists(stateFile))return;
            using var input=new FileStream(stateFile,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(input.Length is <1 or >4_000_000)throw new InvalidDataException();
            using var reader=new StreamReader(input,Encoding.UTF8);
            var saved=JsonSerializer.Deserialize<SavedResources>(SecureStore.Unprotect(reader.ReadToEnd()),Json);
            if(saved==null||saved.Port is <1024 or >65535||saved.Contents==null||
               saved.Contents.Count is <1 or >7||saved.Contents.Any(x=>!ValidContent(x.Key,x.Value)))
                throw new InvalidDataException();
            foreach(var x in saved.Contents)resources[x.Key]=Encoding.UTF8.GetBytes(x.Value);
            StartLocked(saved.Port);
        }
        catch(Exception){StopLocked();persistenceError=true;}
    }
    private void DemandSafePath()
    {
        if(stateFile==null)return;
        for(var d=new DirectoryInfo(Path.GetDirectoryName(stateFile)!);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
        if(File.Exists(stateFile)&&(File.GetAttributes(stateFile)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException();
    }
    private static bool ValidContent(string kind,string? content)=>
        Kinds.Contains(kind)&&content!=null&&content.Length<=65536&&Encoding.UTF8.GetByteCount(content)<=131072&&
        !content.Any(c=>char.IsControl(c)&&c!='\r'&&c!='\n'&&c!='\t')&&UntamedPresentation.Redact(content)==content;
    private void SaveLocked(string kind,string content)
    {
        if(stateFile==null)return;
        DemandSafePath();
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        DemandSafePath();
        var next=resources.ToDictionary(x=>x.Key,x=>Encoding.UTF8.GetString(x.Value),StringComparer.Ordinal);
        next[kind]=content;
        byte[] bytes=Encoding.UTF8.GetBytes(SecureStore.Protect(JsonSerializer.Serialize(new SavedResources(port,next),Json)));
        if(bytes.Length>4_000_000)throw new InvalidDataException();
        string temp=stateFile+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(bytes);output.Flush(true);}
            DemandSafePath();
            File.Move(temp,stateFile,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private void StartLocked(int requested)
    {
        var next=new TcpListener(IPAddress.Loopback,requested);
        next.Server.ExclusiveAddressUse=true;
        try{next.Start(8);}catch{next.Stop();throw;}
        port=((IPEndPoint)next.LocalEndpoint).Port;
        listener=next;cancellation=new CancellationTokenSource();
        _=ServeAsync(next,cancellation.Token,port);
    }
    private object Status()=>new{
        running=listener!=null,port,
        resources=resources.OrderBy(x=>x.Key).Select(x=>new{kind=x.Key,url="http://127.0.0.1:"+port+"/"+x.Key,bytes=x.Value.Length}).ToArray(),
        loopbackOnly=true,persistent=stateFile!=null,persistenceError
    };
    public Task<string> HandleAsync(string raw)
    {
        string? id=null;
        string Fail(string code)=>JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
        try
        {
            if(raw.Length is <2 or >400_000)return Task.FromResult(Fail("UM-RESOURCE-REQUEST"));
            using var doc=JsonDocument.Parse(raw);
            var data=doc.RootElement;
            id=data.GetProperty("id").GetGuid().ToString("D");
            string? method=data.GetProperty("method").GetString();
            lock(gate)
            {
                if(disposed)return Task.FromResult(Fail("UM-RESOURCE-CLOSED"));
                if(method=="resources.status")return Task.FromResult(JsonSerializer.Serialize(new{id,ok=true,data=Status()},Json));
                if(method is not ("resources.publish" or "resources.stop")||
                   !data.TryGetProperty("confirm",out var confirm)||confirm.ValueKind!=JsonValueKind.True)
                    return Task.FromResult(Fail("UM-RESOURCE-APPROVAL"));
                if(method=="resources.stop")
                {
                    DemandSafePath();
                    if(stateFile!=null&&File.Exists(stateFile))File.Delete(stateFile);
                    StopLocked();persistenceError=false;
                }
                else
                {
                    string kind=data.GetProperty("kind").GetString()??"";
                    string content=data.GetProperty("content").GetString()??"";
                    int requested=data.GetProperty("port").GetInt32();
                    if(persistenceError)return Task.FromResult(Fail("UM-RESOURCE-PERSISTENCE-UNAVAILABLE"));
                    if(!ValidContent(kind,content)||(requested<1024||requested>65535)&&!(allowEphemeral&&requested==0))
                        return Task.FromResult(Fail("UM-RESOURCE-INVALID-CONTENT-OR-PORT"));
                    if(listener!=null&&requested!=port&&!(allowEphemeral&&requested==0))
                        return Task.FromResult(Fail("UM-RESOURCE-STOP-BEFORE-PORT-CHANGE"));
                    bool started=listener==null;
                    if(started)StartLocked(requested);
                    try{SaveLocked(kind,content);}
                    catch{if(started)StopLocked();throw;}
                    resources[kind]=Encoding.UTF8.GetBytes(content);
                }
                return Task.FromResult(JsonSerializer.Serialize(new{id,ok=true,data=Status()},Json));
            }
        }
        catch(Exception ex) when(ex is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or SocketException or IOException or FormatException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {return Task.FromResult(Fail("UM-RESOURCE-UNAVAILABLE"));}
    }
    private async Task ServeAsync(TcpListener current,CancellationToken token,int boundPort)
    {
        using var lanes=new SemaphoreSlim(4,4);
        var tasks=new List<Task>();
        try
        {
            while(!token.IsCancellationRequested)
            {
                await lanes.WaitAsync(token);
                TcpClient client;
                try{client=await current.AcceptTcpClientAsync(token);}
                catch{lanes.Release();throw;}
                tasks.RemoveAll(t=>t.IsCompleted);
                tasks.Add(ServeClient(client,token,boundPort,lanes));
            }
        }
        catch(Exception ex) when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
        finally
        {
            lock(gate)
            {
                if(ReferenceEquals(listener,current)){StopLocked();persistenceError=stateFile!=null;}
            }
            await Task.WhenAll(tasks);
        }
    }
    private async Task ServeClient(TcpClient client,CancellationToken token,int boundPort,SemaphoreSlim lanes)
    {
        using(client)
        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                if(client.Client.RemoteEndPoint is not IPEndPoint remote||!IPAddress.IsLoopback(remote.Address))return;
                using var stream=client.GetStream();
                byte[] header=new byte[8192];int used=0;
                while(used<header.Length)
                {
                    int n=await stream.ReadAsync(header.AsMemory(used,1),deadline.Token);
                    if(n==0)return;
                    used+=n;
                    if(used>=4&&header[used-4]==13&&header[used-3]==10&&header[used-2]==13&&header[used-1]==10)break;
                }
                if(used<4||header[used-4]!=13||header[used-3]!=10||header[used-2]!=13||header[used-1]!=10)return;
                string text=Encoding.ASCII.GetString(header,0,used);
                string[] lines=text.Split("\r\n",StringSplitOptions.None);
                string[] first=lines[0].Split(' ',StringSplitOptions.None);
                var hosts=lines.Skip(1).Where(x=>x.StartsWith("Host:",StringComparison.OrdinalIgnoreCase)).ToArray();
                bool hostOk=hosts.Length==1&&hosts[0][5..].Trim().Equals("127.0.0.1:"+boundPort,StringComparison.OrdinalIgnoreCase);
                string path=first.Length==3?first[1]:"";
                bool valid=first.Length==3&&first[0]=="GET"&&(first[2] is "HTTP/1.0" or "HTTP/1.1")&&hostOk&&
                    !lines.Skip(1).Any(x=>x.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)||x.StartsWith("Transfer-Encoding:",StringComparison.OrdinalIgnoreCase));
                byte[]? body=null;
                if(valid&&path.StartsWith('/')&&Kinds.Contains(path[1..]))resources.TryGetValue(path[1..],out body);
                string status=!valid?"400 Bad Request":body==null?"404 Not Found":"200 OK";
                body??=Array.Empty<byte>();
                byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: "+body.Length+
                    "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response,deadline.Token);
                if(body.Length>0)await stream.WriteAsync(body,deadline.Token);
            }
            catch(Exception ex) when(ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException){}
            finally{lanes.Release();}
        }
    }
    private void StopLocked()
    {
        var old=cancellation;cancellation=null;
        var socket=listener;listener=null;port=0;
        old?.Cancel();socket?.Stop();old?.Dispose();
        resources.Clear();
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;StopLocked();}}
}
