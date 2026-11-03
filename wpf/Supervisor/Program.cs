using System.ServiceProcess;
if(args.Contains("--service"))ServiceBase.Run(new SupervisorWindowsService(args));
else
{
    using var cancellation=new CancellationTokenSource();
    Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
    await SupervisorWorker.Run(args,cancellation.Token);
}
sealed class SupervisorWindowsService:ServiceBase
{
    private readonly string[] arguments;
    public SupervisorWindowsService(string[] args)
    {
        arguments=args;int i=Array.IndexOf(args,"--service-name");
        ServiceName=i>=0&&i+1<args.Length?args[i+1]:"UntamedSupervisor";
    }
    private readonly CancellationTokenSource cancellation=new();
    private Task? worker;
    protected override void OnStart(string[] args)
    {
        worker=Task.Run(()=>SupervisorWorker.Run(arguments,cancellation.Token));
        _=worker.ContinueWith(_=>Stop(),TaskContinuationOptions.OnlyOnFaulted);
    }
    protected override void OnStop(){cancellation.Cancel();worker?.Wait(TimeSpan.FromSeconds(15));}
}
