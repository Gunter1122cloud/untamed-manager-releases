using System.Diagnostics;
// Harmless parent/child fixture; never starts ASA or accesses Store.
if(args.Contains("--loader-fixture"))
{
    using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--child-fixture")
        {UseShellExecute=false,CreateNoWindow=true});
}
Console.WriteLine("Fake ASA process ready: "+string.Join(" ",args));
while(true) Thread.Sleep(500);
