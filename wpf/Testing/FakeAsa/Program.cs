using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

// Harmless parent/child fixture; never starts ASA or accesses Store.
if(args.Length==3&&args[0]=="--rcon-admin-fixture")
{
    RconAdminFixture.Run(args[1],args[2]);return 0;
}
if(args.Contains("--loader-fixture"))
{
    using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--child-fixture")
        {UseShellExecute=false,CreateNoWindow=true});
}
if(args.Length>=2 && args[0]=="--lifecycle-fixture")
{
    // Only the integration test's own TEMP fixture is permitted.
    string root=Path.GetFullPath(args[1]);
    if(!Path.IsPathFullyQualified(args[1]) ||args[1].StartsWith(@"\\") ||
       !root.StartsWith(Path.GetFullPath(Path.GetTempPath()),
           StringComparison.OrdinalIgnoreCase) ||
       !root.Contains("Untamed-Integration-",StringComparison.OrdinalIgnoreCase) ||
       !Directory.Exists(root))
        return 19;
    int pid=Environment.ProcessId;
    File.WriteAllText(Path.Combine(root,"ready-"+pid+".json"),
        JsonSerializer.Serialize(new{Pid=pid,StartedUtc=DateTimeOffset.UtcNow}));
    bool denySave=args.Contains("--no-save-proof");
    while(true)
    {
        foreach(var name in Directory.GetFiles(root,"cmd-*.json").OrderBy(x=>x))
        {
            string processing=name+".processing";
            try{File.Move(name,processing);}
            catch(IOException){continue;}
            try
            {
                using var doc=JsonDocument.Parse(File.ReadAllText(processing));
                var body=doc.RootElement;
                string token=body.GetProperty("Token").GetString()??"";
                string command=body.GetProperty("Command").GetString()??"";
                if(!Guid.TryParseExact(token,"N",out _))continue;
                bool proof=command=="SaveWorld" && !denySave;
                string ack=Path.Combine(root,"ack-"+token+".json");
                string temp=ack+".tmp";
                File.WriteAllText(temp,JsonSerializer.Serialize(new{
                    Token=token,Command=command,Pid=pid,SaveProof=proof,
                    CompletedUtc=DateTimeOffset.UtcNow}));
                File.Move(temp,ack,true);
                if(command=="DoExit")return 0;
            }
            finally{try{File.Delete(processing);}catch{}}
        }
        Thread.Sleep(30);
    }
}
Console.WriteLine("Fake ASA process ready: "+string.Join(" ",args));
while(true) Thread.Sleep(500);

static class RconAdminFixture
{
    public static void Run(string root,string portText)
    {
        string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        root=Path.GetFullPath(root);
        if(!string.Equals(Path.GetDirectoryName(root),temp,StringComparison.OrdinalIgnoreCase)||
           !Path.GetFileName(root).StartsWith("Untamed-RconLab-",StringComparison.Ordinal)||
           !Directory.Exists(root)||!int.TryParse(portText,out int port)||port is <1024 or >65535)
            throw new InvalidOperationException("Isolated RCON test fixture path/port required");
        string password=File.ReadAllText(Path.Combine(root,"fixture-password"));
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,port);listener.Start();
        File.WriteAllText(Path.Combine(root,"listener-ready"),Environment.ProcessId.ToString());
        int received=0;
        while(true)
        {
            using var client=listener.AcceptTcpClient();using var stream=client.GetStream();
            try
            {
                var auth=Read(stream);
                if(auth.Type!=3||auth.Body!=password){Write(stream,-1,2,"");continue;}
                Write(stream,auth.Id,2,"");
                var command=Read(stream);
                if(command.Type!=2)continue;
                File.WriteAllText(Path.Combine(root,"command-"+(++received)+".json"),
                    JsonSerializer.Serialize(new{command.Body,command.Id}));
                if(File.Exists(Path.Combine(root,"withhold-response"))){Thread.Sleep(15000);continue;}
                Write(stream,command.Id,0,"");
                var barrier=Read(stream);
                if(barrier.Type==2&&barrier.Body=="ListPlayers")Write(stream,barrier.Id,0,"No Players Connected");
            }
            catch(IOException){}
        }
    }
    static (int Id,int Type,string Body) Read(Stream stream)
    {
        byte[] size=new byte[4];stream.ReadExactly(size);
        int length=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(size);
        if(length is <10 or >10000)throw new IOException("Invalid fixture frame");
        byte[] payload=new byte[length];stream.ReadExactly(payload);
        if(payload[^1]!=0||payload[^2]!=0)throw new IOException("Invalid fixture terminator");
        return(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)),
            System.Text.Encoding.UTF8.GetString(payload,8,length-10));
    }
    static void Write(Stream stream,int id,int type,string body)
    {
        byte[] payload=System.Text.Encoding.UTF8.GetBytes(body),frame=new byte[payload.Length+14];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame,frame.Length-4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4),id);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8),type);
        payload.CopyTo(frame.AsSpan(12));stream.Write(frame);stream.Flush();
    }
}
