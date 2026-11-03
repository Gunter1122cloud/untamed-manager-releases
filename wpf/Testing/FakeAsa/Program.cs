using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

// Harmless parent/child fixture; never starts ASA or accesses Store.
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
