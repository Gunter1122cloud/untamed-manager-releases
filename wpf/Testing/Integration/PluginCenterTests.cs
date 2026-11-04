using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class PluginCenterTests
{
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new InvalidOperationException("PLUGIN CENTER TEST: "+reason);
        Console.WriteLine("PASS premium-plugin "+reason);
    }

    public static async Task Run(string root)
    {
        var testRoot=Path.Combine(root,"premium-plugin-center");
        string install=Path.Combine(testRoot,"disposable-asa");
        string active=Path.Combine(install,"ShooterGame","Binaries","Win64","ArkApi","Plugins");
        string disabled=Path.Combine(install,"ShooterGame","Binaries","Win64","UntamedDisabledPlugins");
        string name="SamplePlugin";
        string dir=Path.Combine(active,name);
        Directory.CreateDirectory(dir);
        string dll=Path.Combine(dir,"SamplePlugin.dll");
        File.WriteAllBytes(dll,new byte[]{0x4d,0x5a,0x90,0x00,0x13,0x37});
        File.WriteAllText(Path.Combine(dir,"config.json"),"{\"mysetting\":123}");
        File.WriteAllText(Path.Combine(dir,"license.key"),"offline-only-license-fixture");
        var first=new LocalMap{Name="Disposable map A",InstallationPath=install};
        var second=new LocalMap{Name="Disposable map B",InstallationPath=install};
        bool otherRunning=true;
        await using var cluster=new ClusterService("plugin-center-test",
            Path.Combine(testRoot,"profile"),
            map=>map.Id==second.Id && otherRunning
                ?new ProcessCheck(true,"simulated running peer in shared install",null)
                :new ProcessCheck(false,"synthetic confirmed stopped",null,true));
        cluster.Config.LocalMaps.Add(first);
        cluster.Config.LocalMaps.Add(second);
        cluster.Save();

        var initial=PluginStateManager.Inventory(first);
        Check(initial.Count==1&&initial.Single().Enabled,
            "local disk inventory initially shows enabled plugin");
        var denied=PluginStateManager.InspectLocalToggle(cluster,first.Id,name,false);
        Check(!denied.Allowed&&denied.CheckedLocalMaps==2&&denied.Reason.Contains("Disposable map B"),
            "plugin toggle is denied when the other locally registered shared-install map runs");
        bool blocked=false;
        try{PluginStateManager.ToggleLocalChecked(cluster,first.Id,name,false);}
        catch(InvalidOperationException){blocked=true;}
        Check(blocked&&File.Exists(dll),
            "shared-map running guard cannot mutate original DLL or configuration");

        otherRunning=false;
        var readiness=PluginStateManager.InspectLocalToggle(cluster,first.Id,name,false);
        Check(readiness.Allowed&&readiness.CheckedLocalMaps==2,
            "both local shared-install maps must be confirmed stopped");
        var result=PluginStateManager.ToggleLocalChecked(cluster,first.Id,name,false);
        string destination=Path.Combine(disabled,name);
        Check(!Directory.Exists(dir)&&Directory.Exists(destination) &&
            result.BackupPackage.Length>0 && result.RequiresRestart &&
            File.ReadAllText(Path.Combine(destination,"config.json"))=="{\"mysetting\":123}" &&
            File.ReadAllText(Path.Combine(destination,"license.key"))=="offline-only-license-fixture",
            "verified toggle disables whole plugin without losing config or license data");
        var now=PluginStateManager.Inventory(first).Single();
        Check(!now.Enabled&&now.Name==name,
            "disabled local plugin remains visible and selectable");
        Check(!PluginStateManager.InspectLocalToggle(cluster,first.Id,name,false).Allowed,
            "already disabled plugin is nonactionable");
        var enabled=PluginStateManager.ToggleLocalChecked(cluster,first.Id,name,true);
        Check(File.Exists(dll)&&enabled.Enabled &&
            PluginStateManager.Inventory(first).Single().Enabled &&
            TestBackupPackage.Verify(enabled.BackupPackage,first.Id).Files.Count>=3,
            "enable restores directory and makes a complete verified pre-toggle backup");

        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination,"collision.txt"),"offline test");
        bool collisionBlocked=false;
        try{PluginStateManager.Inventory(first);}
        catch(InvalidDataException){collisionBlocked=true;}
        Check(collisionBlocked && File.Exists(dll),
            "same plugin folder in both roots is never silently reported actionable");
        Directory.Delete(destination,true);
        bool remoteDenied=false;
        try{PluginStateManager.InspectLocalToggle(cluster,Guid.NewGuid(),name,false);}
        catch(UnauthorizedAccessException){remoteDenied=true;}
        Check(remoteDenied&&File.Exists(dll),
            "foreign map cannot toggle or inspect local plugin modifications");
    }
}
