using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public enum ModUpdateState { Unknown, NotInstalled, Current, NewerFileAvailable, RemoteUnavailable }
public sealed record LocalModEvidence(string ModId,bool DirectoryPresent,int? VerifiedFileId,
    string Source,string Note);
public sealed record ModUpdateAssessment(string ModId,ModUpdateState State,
    int? LocalFileId,int? LatestFileId,string Reason,bool CanAutoInstall=false);

public static class ModUpdatePlanner
{
    // Directory existence is weak evidence: the installed CurseForge FILE ID
    // is NOT derivable from its mod/project ID or folder timestamp.
    public static LocalModEvidence InspectCandidate(string installRoot,string projectId)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(projectId??"",
            @"\A[0-9]{5,12}\z"))
            throw new InvalidDataException("Invalid CurseForge project ID");
        if(string.IsNullOrWhiteSpace(installRoot)||!Path.IsPathFullyQualified(installRoot) ||
           installRoot.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local install path required");
        string root=Path.GetFullPath(installRoot);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Only local fixed disks supported");
        // ASA layouts vary by server version. Finding a candidate does not
        // prove that ASA actually loaded it, or identify the installed file.
        var candidates=new[]{
            Path.Combine(root,"ShooterGame","Mods",projectId),
            Path.Combine(root,"ShooterGame","Binaries","Win64","ShooterGame",
                "Mods",projectId)
        };
        foreach(var folder in candidates)
        {
            for(var d=new DirectoryInfo(folder);d!=null;d=d.Parent)
                if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Linked mod path rejected");
            if(Directory.Exists(folder))
                return new(projectId,true,null,folder,
                    "Candidate folder exists; actual runtime load and file ID unverified");
        }
        return new(projectId,false,null,root,
            "No known candidate directory found; other ASA layouts not excluded");
    }

    public static ModUpdateAssessment Assess(LocalModEvidence local,
        CurseForgeFilesResult remote)
    {
        if(local==null||remote==null||int.Parse(local.ModId)!=remote.ModId)
            throw new InvalidDataException("Mod project identity mismatch");
        var valid=remote.Files.Where(x=>x.ModId==remote.ModId&&x.Available)
            .OrderByDescending(x=>x.Published??DateTimeOffset.MinValue)
            .ThenByDescending(x=>x.FileId).ToArray();
        if(valid.Length==0)
            return new(local.ModId,ModUpdateState.RemoteUnavailable,
                local.VerifiedFileId,null,"No available verified CurseForge file metadata");
        var latest=valid[0];
        if(!local.DirectoryPresent)
            return new(local.ModId,ModUpdateState.NotInstalled,
                null,latest.FileId,"No known installed directory; not proof of absence");
        if(local.VerifiedFileId==null)
            return new(local.ModId,ModUpdateState.Unknown,
                null,latest.FileId,"Installed CurseForge file ID unknown; update comparison is unsafe");
        var installed=valid.FirstOrDefault(x=>x.FileId==local.VerifiedFileId);
        if(installed==null)
            return new(local.ModId,ModUpdateState.Unknown,
                local.VerifiedFileId,latest.FileId,"Installed file ID not in checked remote page");
        if(installed.FileId==latest.FileId)
            return new(local.ModId,ModUpdateState.Current,
                installed.FileId,latest.FileId,"Verified same file ID on checked metadata page");
        if(!installed.Published.HasValue||!latest.Published.HasValue)
            return new(local.ModId,ModUpdateState.Unknown,
                installed.FileId,latest.FileId,"Publication order is unverified");
        return new(local.ModId,latest.Published>installed.Published?
            ModUpdateState.NewerFileAvailable:ModUpdateState.Current,
            installed.FileId,latest.FileId,
            "File IDs and publication dates compared; no authorized download/installation");
    }
}
