using System;
using System.IO;
using UntamedManager.Core;

public static class ModUpdatePlannerTests
{
    private static void Check(bool condition,string label)
    {
        if(!condition)throw new InvalidOperationException("MOD VERSION SAFETY: "+label);
        Console.WriteLine("PASS mod version safety: "+label);
    }
    public static void Run()
    {
        const int project=928548;
        DateTimeOffset oldDate=new(2026,10,1,0,0,0,TimeSpan.Zero);
        DateTimeOffset recent=new(2026,11,5,0,0,0,TimeSpan.Zero);
        var oldFile=new CurseForgeFile(14501,project,"Installed",
            oldDate,true,Array.Empty<int>());
        var newFile=new CurseForgeFile(14502,project,"New",
            recent,true,Array.Empty<int>());
        var full=new CurseForgeFilesResult(project,new[]{oldFile,newFile},
            Index:0,Total:2,HasMore:false);
        var installed=new LocalModEvidence(project.ToString(),true,14501,
            "CI verified file-ID fixture","Exact ID independently verified");
        var current=ModUpdatePlanner.Assess(installed with {VerifiedFileId=14502},full);
        Check(current.State==ModUpdateState.Current &&
              current.LatestFileId==14502 && !current.CanAutoInstall,
            "complete metadata plus matching proven local file ID may be marked Current");
        var newer=ModUpdatePlanner.Assess(installed,full);
        Check(newer.State==ModUpdateState.NewerFileAvailable &&
              newer.LatestFileId==14502 && !newer.CanAutoInstall,
            "complete metadata with independently proven older file ID permits Newer state, not install");
        var partial=ModUpdatePlanner.Assess(installed with {VerifiedFileId=14502},
            full with {HasMore=true,Total=27});
        Check(partial.State==ModUpdateState.Unknown &&
              partial.LatestFileId==null,
            "pagination uncertainty cannot yield false Current status");
        var partialDate=ModUpdatePlanner.Assess(installed,
            full with {HasMore=true,Total=27});
        Check(partialDate.State==ModUpdateState.Unknown,
            "pagination uncertainty cannot yield false Newer status");
        var tie=new CurseForgeFile(14503,project,"Same date",
            recent,true,Array.Empty<int>());
        var tied=ModUpdatePlanner.Assess(
            installed with {VerifiedFileId=14502},
            new(project,new[]{newFile,tie},0,2,false));
        Check(tied.State==ModUpdateState.Unknown,
            "equal publication dates with different IDs cannot imply Current");
        var absent=ModUpdatePlanner.Assess(installed with {DirectoryPresent=false,
            VerifiedFileId=null},full);
        Check(absent.State==ModUpdateState.Unknown &&
              absent.Reason.Contains("alternative ASA",StringComparison.Ordinal),
            "absence from known candidate folders is not proof of missing ASA mod");
        var uncertain=ModUpdatePlanner.Assess(installed with {VerifiedFileId=null},full);
        Check(uncertain.State==ModUpdateState.Unknown,
            "folder name is not an exact CurseForge file-ID fingerprint");
        var duplicate=ModUpdatePlanner.Assess(installed,
            new(project,new[]{oldFile,oldFile},0,2,false));
        Check(duplicate.State==ModUpdateState.Unknown,
            "duplicate provider file IDs are rejected as ambiguous");
        var wrongProject=new CurseForgeFilesResult(project+1,new[]{oldFile},0,1,false);
        bool refused=false;
        try{ModUpdatePlanner.Assess(installed,wrongProject);}
        catch(InvalidDataException){refused=true;}
        Check(refused,"wrong CurseForge project ID is rejected, not silently compared");
    }
}
