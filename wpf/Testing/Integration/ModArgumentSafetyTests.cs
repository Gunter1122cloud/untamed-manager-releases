using UntamedManager.Core;

public static class ModArgumentSafetyTests
{
    static void Check(bool value,string reason)
    {if(!value)throw new Exception("Mod arguments: "+reason);Console.WriteLine("PASS mod-arguments "+reason);}
    public static async Task Run(string root)
    {
        const string unknown="TheIsland_WP?SessionName=\"UK  -mods=111111  server\" -Custom=\"keep  two   spaces\"";
        var map=new LocalMap{Name="mod quoting",Arguments=unknown+" -mods=930170,928548 -passivemods=930170"};
        var state=ModStateController.Read(map);
        Check(state.Passive.SequenceEqual(new[]{"930170"})&&state.Active.SequenceEqual(new[]{"928548"}),
            "quoted unknown argument cannot inject a second mod switch");
        var preview=ModStateController.Preview(map,new(map.Id,"912815",ModMode.Active));
        Check(preview.NewArguments.StartsWith(unknown,StringComparison.Ordinal)&&
            preview.NewArguments.EndsWith("-mods=930170,928548,912815 -passivemods=930170"),
            "exact unknown text and internal whitespace survive mod changes; passive remains first once");
        var wholeQuoted=new LocalMap{Arguments="\"-mods=930170,928548\" \"-passivemods=930170\""};
        Check(ModStateController.Read(wholeQuoted).Active.Single()=="928548",
            "whole quoted Windows switch is interpreted as the actual mod argument");
        foreach(string invalid in new[]{"-mods=928548 -mods=930170","\"-mods=928548\" -mods=930170",
            "-mods=\"928548 930170\"","-mods=928548 -Custom=\"unterminated","-mods"})
        {
            bool blocked=false;try{ModStateController.Read(new LocalMap{Arguments=invalid});}
            catch(InvalidDataException){blocked=true;}
            Check(blocked,"ambiguous, duplicate or unclosed argument frame rejected");
        }
        string install=Path.Combine(root,"mod-argument-install");Directory.CreateDirectory(install);
        map.InstallationPath=install;
        await using var cluster=new ClusterService("mod-arguments",Path.Combine(root,"mod-argument-profile"),
            _=>new(false,"CI stopped",null,true));
        cluster.Config.LocalMaps.Add(map);cluster.Save();string previous=map.Arguments;
        byte[] persisted=File.ReadAllBytes(cluster.Store.FilePath);
        bool rejected=false;
        try{ModStateController.ApplyLocalBatch(cluster,new[]{new ModEdit(map.Id,"912815",ModMode.Active),
            new ModEdit(Guid.NewGuid(),"935528",ModMode.Passive)});}
        catch(UnauthorizedAccessException){rejected=true;}
        Check(rejected&&map.Arguments==previous&&persisted.SequenceEqual(File.ReadAllBytes(cluster.Store.FilePath)),
            "multi-map batch failure rolls back memory and leaves persisted arguments byte-identical");
        ModStateController.ApplyLocalBatch(cluster,new[]{new ModEdit(map.Id,"912815",ModMode.Active)});
        Check(map.ModChangesPending,"configuration change remains pending without authenticated runtime evidence");
        bool ackRefused=false;try{ModStateController.ConfirmAppliedAfterVerifiedRestart(cluster,map);}
        catch(InvalidOperationException){ackRefused=true;}
        Check(ackRefused&&map.ModChangesPending,"restart claim without real loaded-mod proof never clears pending");
    }
}
