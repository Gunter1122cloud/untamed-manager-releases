using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UntamedManager.Core;

string Value(string key,string fallback)
{
    int index=Array.IndexOf(args,key);
    return index<0?fallback:index+1<args.Length?args[index+1]:throw new ArgumentException("Missing "+key);
}
var store=new ConfigStore(Value("--profile","default"),Value("--root","") is {Length:>0} root?root:null);
using var gate=new Mutex(true,"Local\\UntamedSupervisor-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(store.Root).ToUpperInvariant()))),out bool first);
if(!first)throw new IOException("Supervisor already running for this profile");
var inspector=new WindowsProcessInspector();var policy=new SupervisorPolicy();
using var cancellation=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
do
{
    try
    {
        var config=store.Read();
        var states=config.LocalMaps.Select(map=>
        {
            ProcessCheck state;
            // Store-backed maps stay blocked; the worker never probes a shared Store.
            if(map.Arguments.Contains("store",StringComparison.OrdinalIgnoreCase))state=new(false,"Store validation required",null);
            else state=string.IsNullOrWhiteSpace(map.ApiLoaderPath)?inspector.Inspect(map):inspector.InspectFamily(map);
            return policy.Observe(map.Id,state,map.Maintenance,DateTimeOffset.UtcNow);
        }).ToArray();
        string path=Path.Combine(store.Root,"supervisor-heartbeat.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new {hostId=config.LocalHostId,utc=DateTimeOffset.UtcNow,readOnly=true,states}));
        File.Move(path+".tmp",path,true);
    }
    catch(Exception ex) {Console.Error.WriteLine("Supervisor observation failed: "+ex.GetType().Name);}
    if(args.Contains("--once"))break;
    try {await Task.Delay(TimeSpan.FromSeconds(10),cancellation.Token);}catch(OperationCanceledException){break;}
}while(!cancellation.IsCancellationRequested);
// No process kill, RCON command, install, restore or restart exists in this worker.
