using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// A previously imported five-map Store-v1 fingerprint is immutable. If later
/// current metadata adds independent event or later maps AND the lock is
/// rewritten to a valid expanded configuration, its four-file hash changes.
/// This method never adopts that changed hash as proof of the old approval.
/// Instead it decides whether a second, explicitly typed, one-use operator
/// authorization may be offered for auditing OLD, STOPPED journals only.
/// No caller can use this result to authorize ASA/RCON, world writes or a
/// Store-v1 writer migration.
/// </summary>
public static class LegacyStoreV1ExpandedSourceReview
{
    private static bool ValidHash(string? sha)=>
        sha?.Length==64&&sha.All(Uri.IsHexDigit);

    public static bool RequireExplicitReview(LocalMap target,
        IEnumerable<LocalMap> registeredMaps,
        LegacyStoreV1LocalReader.Report local,
        LegacyStoreV1OriginalLocalSelection current)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(registeredMaps);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(current);
        if(!target.HistoricalStoreProtected||
           !ValidHash(target.HistoricalArchiveSha256)||
           !ValidHash(current.FourFilesSha256)||
           !ValidHash(current.LaunchProfileSha256))
            throw new InvalidDataException("UM-RECOVERY-SOURCE-HISTORICAL-ID-INVALID");
        if(string.Equals(target.HistoricalArchiveSha256,
                current.FourFilesSha256,StringComparison.Ordinal))
            return false; // Ordinary immutable previous source: no reapproval.

        // Any bounded 5+N local expansion can be reviewed: exactly five
        // immutable historical owners plus one or more independent maps.
        // The source readers validate every member/record and the full lock.
        // Never use a human-readable Evidence string as authorization.
        if(!local.LocalFilesVerified||!local.ClusterHashVerified||
           !local.LocalLockVerified||!local.AllLocalMapsActive||
           !local.LocalSnapshotReady||
           local.CurrentLocalMemberCount is <6 or >32||
           current.CurrentLocalMemberCount!=local.CurrentLocalMemberCount||
           !current.SelectedHistoricalSubsetOfExpandedRoster||
           local.Maps.Length!=5||current.Maps.Length!=5||
           !ValidHash(local.OriginalFourFilesTag)||
           !string.Equals(local.OriginalFourFilesTag,
               current.FourFilesSha256,StringComparison.Ordinal)||
           !ValidHash(local.OriginalMapRosterTag))
            throw new InvalidDataException(
                "UM-RECOVERY-STOPPED-HISTORICAL-SOURCE-CHANGED");

        var original=registeredMaps.Where(x=>x.HistoricalStoreProtected).ToArray();
        if(original.Length!=5||original.DistinctBy(x=>x.Id).Count()!=5||
           !original.Any(x=>ReferenceEquals(x,target))||
           current.Maps.Select(x=>x.MapId).Distinct().Count()!=5||
           local.Maps.Select(x=>x.MapId).Distinct().Count()!=5)
            throw new InvalidDataException(
                "UM-RECOVERY-STOPPED-ORIGINAL-ROSTER-CHANGED");
        var byId=original.ToDictionary(x=>x.Id);
        var selected=current.Maps.ToDictionary(x=>x.MapId);
        var inspected=local.Maps.ToDictionary(x=>x.MapId);
        if(!byId.Keys.ToHashSet().SetEquals(selected.Keys)||
           !byId.Keys.ToHashSet().SetEquals(inspected.Keys))
            throw new InvalidDataException(
                "UM-RECOVERY-STOPPED-ORIGINAL-ROSTER-CHANGED");

        // Compare all five HISTORICAL GUID, map key and installation roots
        // against the stored Obsidian profiles. A foreign event GUID, moved
        // map, changed original owner or separately imported source is not
        // accepted by an operator's one-time acknowledgement.
        foreach(var owner in original)
        {
            var source=selected[owner.Id];
            var evidence=inspected[owner.Id];
            if(!ValidHash(owner.HistoricalArchiveSha256)||
               !string.Equals(owner.HistoricalArchiveSha256,
                   target.HistoricalArchiveSha256,StringComparison.Ordinal)||
               !string.Equals(owner.HistoricalMapKey,source.MapKey,
                   StringComparison.Ordinal)||
               !string.Equals(evidence.MapKey,source.MapKey,
                   StringComparison.Ordinal)||
               !string.Equals(evidence.OriginalRoot,source.OriginalInstallationRoot,
                   StringComparison.OrdinalIgnoreCase)||
               !string.Equals(Path.GetFullPath(owner.InstallationPath),
                   Path.GetFullPath(source.OriginalInstallationRoot),
                   StringComparison.OrdinalIgnoreCase)||
               !evidence.Selected||!evidence.Active||
               !evidence.SessionMatches||!evidence.RootMatches||
               !evidence.BackupIdentityPresent)
                throw new InvalidDataException(
                    "UM-RECOVERY-STOPPED-ORIGINAL-ROSTER-CHANGED");
        }

        // Still not a claim that the old *bytes* match: that cannot be
        // proved without an old pinned source. Must remain an explicit,
        // separately acknowledged review and preserve the old archive hash.
        return true;
    }
}
