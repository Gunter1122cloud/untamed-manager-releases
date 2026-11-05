using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class ManagerSettingsAtomicityTests
{
    private static void Assert(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "SETTINGS ATOMICITY: "+reason);
        Console.WriteLine("PASS settings atomicity: "+reason);
    }

    public static async Task Run(string sandboxRoot)
    {
        string root=Path.Combine(sandboxRoot,"settings-CAS-fixture");
        Directory.CreateDirectory(root);
        await using var current=new ClusterService("settings-CAS",root);
        current.AddMap(new LocalMap {
            Name="The Island",GamePort=7777,QueryPort=27015,RconPort=37015
        });
        var configRef=current.Config;
        var mapsRef=configRef.LocalMaps;
        var mapRef=mapsRef.Single();
        var scheduledRef=configRef.ScheduledTasks;
        var recurringRef=configRef.RecurringSchedules;

        await using var stale=new ClusterService("settings-CAS",root);
        var staleConfig=stale.Config;
        var staleMap=staleConfig.LocalMaps.Single();
        string staleHost=staleConfig.LocalHostName;

        current.UpdateSettings(c=>{
            c.LocalHostName="UK-FICTIONAL-CI-HOST";
            c.PeerAddress="127.0.0.1";
            c.PeerPort=38100;
            c.CurseForgeApiKeyProtected="SYNTHETIC_PLACEHOLDER_KEY";
        });
        Assert(ReferenceEquals(current.Config,configRef) &&
            ReferenceEquals(current.Config.LocalMaps,mapsRef) &&
            ReferenceEquals(current.Config.LocalMaps[0],mapRef) &&
            ReferenceEquals(current.Config.ScheduledTasks,scheduledRef) &&
            ReferenceEquals(current.Config.RecurringSchedules,recurringRef),
            "successful settings save preserves config, live map and schedule objects");
        Assert(current.Config.LocalHostName=="UK-FICTIONAL-CI-HOST" &&
            current.Config.PeerPort==38100,
            "successful settings save updates only approved scalar settings");

        bool staleRejected=false;
        try{stale.UpdateSettings(c=>c.LocalHostName="GHOST-UNPERSISTED-HOST");}
        catch(IOException){staleRejected=true;}
        Assert(staleRejected &&
            ReferenceEquals(stale.Config,staleConfig) &&
            stale.Config.LocalHostName==staleHost &&
            ReferenceEquals(stale.Config.LocalMaps.Single(),staleMap),
            "concurrent settings CAS rejects write without leaving a phantom in-memory change");

        bool refusedMapMutation=false;
        try{current.UpdateSettings(c=>c.LocalMaps.Clear());}
        catch(InvalidOperationException){refusedMapMutation=true;}
        Assert(refusedMapMutation &&
            ReferenceEquals(current.Config.LocalMaps.Single(),mapRef),
            "settings callback cannot erase a registered map through staged collection");
        bool refusedHostIdentity=false;
        try{current.UpdateSettings(c=>c.LocalHostId=Guid.NewGuid().ToString("N"));}
        catch(InvalidOperationException){refusedHostIdentity=true;}
        Assert(refusedHostIdentity,
            "settings cannot replace Store/Manager host ownership identity");

        bool callbackExceptionPropagates=false;
        try{current.UpdateSettings(c=>{
            c.DiscordWebhookProtected="SYNTHETIC_NOT_PERSISTED";
            throw new InvalidOperationException("CI deliberate callback failure");
        });}
        catch(InvalidOperationException){callbackExceptionPropagates=true;}
        Assert(callbackExceptionPropagates &&
            current.Config.DiscordWebhookProtected!="SYNTHETIC_NOT_PERSISTED",
            "failed update callback leaves protected settings unchanged");

        await using var reloaded=new ClusterService("settings-CAS",root);
        Assert(reloaded.Config.LocalHostName=="UK-FICTIONAL-CI-HOST" &&
            reloaded.Config.PeerPort==38100 &&
            reloaded.Config.CurseForgeApiKeyProtected=="SYNTHETIC_PLACEHOLDER_KEY" &&
            reloaded.Config.DiscordWebhookProtected!="SYNTHETIC_NOT_PERSISTED" &&
            reloaded.Config.LocalMaps.Single().Name=="The Island",
            "independent Manager reload sees only committed settings and original map profile");
    }
}
