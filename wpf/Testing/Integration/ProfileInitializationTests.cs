using UntamedManager.Core;

public static class ProfileInitializationTests
{
    static void Check(bool value,string reason)
    {if(!value)throw new Exception("Profile initialization: "+reason);Console.WriteLine("PASS profile-init "+reason);}
    public static async Task Run(string root)
    {
        string profile=Path.Combine(root,"first-use-profile");
        var first=new ConfigStore("first",profile);var second=new ConfigStore("second",profile);
        var stale=new ConfigStore("stale",profile);
        var results=await Task.WhenAll(Task.Run(first.EnsureInitialized),Task.Run(second.EnsureInitialized));
        Check(results[0].LocalHostId==results[1].LocalHostId&&Guid.TryParse(results[0].LocalHostId,out var id)&&id!=Guid.Empty,
            "concurrent first-use initialization publishes exactly one durable local host identity");
        byte[] initialized=File.ReadAllBytes(first.FilePath);
        Check(first.Read().LocalHostId==results[0].LocalHostId&&initialized.SequenceEqual(File.ReadAllBytes(first.FilePath)),
            "subsequent profile reads preserve the initial identity without writing");
        bool blocked=false;try{stale.Save(new ManagerConfig());}catch(IOException){blocked=true;}
        Check(blocked,"pre-initialization stale writer cannot overwrite the newly published profile");
        await using(var cluster=new ClusterService("observed",profile,_=>new(false,"CI stopped",null,true)))
        {
            cluster.LocalStatus();cluster.LocalStatus();
            Check(cluster.Config.LocalHostId==results[0].LocalHostId&&initialized.SequenceEqual(File.ReadAllBytes(first.FilePath)),
                "constructing a Manager for an existing profile and observing status never rewrites identity metadata");
        }
        await using var restarted=new ClusterService("restarted",profile,_=>new(false,"CI stopped",null,true));
        Check(restarted.Config.LocalHostId==results[0].LocalHostId,
            "restart before any manual settings edit retains signed IPC/panel owner identity");
    }
}
