using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

/// <summary>
/// Local read-only plan for the most common future case: a completely new
/// ASA map whose world/profile/tribe files do not exist yet. Such a map
/// must first start with -usestore; -converttostore is ONLY for an existing
/// non-Store world. This does not enroll or start a Store writer.
/// </summary>
public sealed record FutureStoreFreshFirstUsePlan(
    Guid MapId,string MapKey,string ClusterId,string ClusterPath,
    string Args,string OriginalSourceSha256,string Fingerprint)
{
    public bool NeedsLegacyWorldConversion=>false;
    public bool ProcessStartAuthorized=>false;
    public bool OriginalStoreTouched=>false;
    public bool RemotePeerWriterVerified=>false;
    public bool CharacterTravelCertified=>false;
}

public static class FutureStoreFreshFirstUsePlanner
{
    private static void Deny(string code)=>throw new InvalidDataException(
        "UM-FUTURE-FIRST-USE-"+code);

    private static void Stopped(ProcessCheck? value)
    {
        if(value==null||!value.ConfirmedStopped||value.Running||
           value.Identity!=null||value.LoaderIdentity!=null)
            throw new InvalidOperationException(
                "UM-FUTURE-FIRST-USE-CONFIRMED-STOPPED-REQUIRED");
    }

    private static void NoSavedData(string root)
    {
        string saved=Path.Combine(root,"ShooterGame","Saved");
        foreach(string name in new[]{"SavedArks","SavedArksLocal","SaveGames"})
        {
            string folder=Path.Combine(saved,name);
            if(!Directory.Exists(folder))continue;
            for(var node=new DirectoryInfo(folder);node!=null;node=node.Parent)
            {
                if((node.Attributes&FileAttributes.ReparsePoint)!=0)
                    Deny("SAVE-DIRECTORY-LINKED");
                if(node.FullName.Equals(root,StringComparison.OrdinalIgnoreCase))
                    break;
            }
            // Even 0-byte placeholders, loose profiles, transfer folders or
            // subdirectories count as preexisting content requiring review.
            if(Directory.EnumerateFileSystemEntries(folder).Any())
                Deny("PREEXISTING-SAVE-DATA");
        }
    }

    public static FutureStoreFreshFirstUsePlan Preview(
        LocalMap candidate,IReadOnlyCollection<LocalMap> localMaps,
        ProcessCheck stopped,string[] originalSourceImages,
        Func<ProcessCheck> freshProcessCheck)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(localMaps);
        ArgumentNullException.ThrowIfNull(freshProcessCheck);
        Stopped(stopped);
        if(candidate.Id==Guid.Empty||localMaps.Count(x=>x.Id==candidate.Id)!=1||
           candidate.HistoricalStoreProtected||
           !string.IsNullOrWhiteSpace(candidate.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(candidate.FreshStoreClusterId)||
           !string.IsNullOrWhiteSpace(candidate.StoreTestRoot)||
           candidate.Maintenance||candidate.AutoStart||
           candidate.GamePort is <1024 or >65535||
           candidate.QueryPort is <1024 or >65535||
           candidate.RconPort is <1024 or >65535||
           candidate.GamePort==candidate.QueryPort||
           AsaStoreArgumentSwitches.ContainsProtectedSwitch(candidate.Arguments))
            Deny("CANDIDATE");
        if(string.IsNullOrWhiteSpace(candidate.InstallationPath)||
           !Path.IsPathFullyQualified(candidate.InstallationPath)||
           candidate.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal))
            Deny("LOCAL-ROOT");
        string root=Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate.InstallationPath));
        if(!Directory.Exists(root))Deny("INSTALLATION-NOT-FOUND");
        for(var node=new DirectoryInfo(root);node!=null;node=node.Parent)
            if((node.Attributes&FileAttributes.ReparsePoint)!=0)
                Deny("INSTALLATION-LINKED");
        if(localMaps.Any(x=>x.Id!=candidate.Id&&
           !string.IsNullOrWhiteSpace(x.InstallationPath)&&
           Path.GetFullPath(x.InstallationPath).TrimEnd(
               Path.DirectorySeparatorChar).Equals(root,
               StringComparison.OrdinalIgnoreCase)))
            Deny("INSTALLATION-SHARED");
        LocalMapPortPolicy.RequireNoConflicts(candidate,localMaps);
        var source=FutureStoreConversionSourceAudit.Inspect(originalSourceImages);
        string key=AsaSaveLayoutResolver.MapToken(candidate);
        if(source.MapIds.Contains(candidate.Id.ToString("D"),
               StringComparer.OrdinalIgnoreCase)||
           source.MapKeys.Contains(key,StringComparer.OrdinalIgnoreCase)||
           source.LocalRoots.Contains(root,StringComparer.OrdinalIgnoreCase)||
           source.UdpPorts.Contains(candidate.GamePort)||
           source.UdpPorts.Contains(candidate.QueryPort))
            Deny("ORIGINAL-ROSTER-COLLISION");
        NoSavedData(root);
        string baseline=FreshStoreEnrollmentPolicy.ValidateBaseArguments(
            candidate,candidate.Arguments);
        Stopped(freshProcessCheck());
        // Explicitly omit -converttostore for a truly empty new-world Start.
        string path=source.ClusterPath.Contains(' ')?
            "\""+source.ClusterPath+"\"":source.ClusterPath;
        string args=baseline+" -clusterid="+VaultProtocol.ClusterId+
            " -ClusterDirOverride="+path+" -usestore -log";
        if(args.Length>8192||AsaStoreArgumentSwitches.ContainsConversion(args)||
           !AsaStoreArgumentSwitches.ContainsStoreWriter(args))
            Deny("ARGUMENTS");
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            candidate.Id.ToString("D")+"\n"+source.OriginalSourceSha256+
            "\n"+root+"\n"+args)));
        return new(candidate.Id,key,VaultProtocol.ClusterId,source.ClusterPath,
            args,source.OriginalSourceSha256,hash);
    }
}
