using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace UntamedManager.Core;

/// <summary>
/// Verifies a completed, offline, stopped-map world backup made by the
/// Manager's existing immutable LocalBackupWorkflow. This proves exact
/// file-copy integrity and map identity only; it does NOT infer a successful
/// in-game SaveWorld, conversion, SQLite consistency or character travel.
/// </summary>
public sealed record FutureStoreBackupProof(Guid MapId,string ManifestSha256,
    string PackageDirectory,int Files,long Bytes,DateTimeOffset CopiedUtc)
{
    public bool GameSideSaveWorldConfirmed=>false;
    public bool ConversionAuthorized=>false;
}

public static class FutureStoreBackupVerifier
{
    private static void Stopped(ProcessCheck? state)
    {
        if(state==null||!state.ConfirmedStopped||state.Running||
           state.Identity!=null||state.LoaderIdentity!=null)
            throw new InvalidOperationException("UM-FUTURE-BACKUP-STOPPED-REQUIRED");
    }

    public static FutureStoreBackupProof Verify(LocalMap map,
        ProcessCheck state,string package,Func<ProcessCheck>? freshProcessCheck=null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if(map.Id==Guid.Empty||map.HistoricalStoreProtected||
           !string.IsNullOrWhiteSpace(map.HistoricalArchiveSha256)||
           !string.IsNullOrWhiteSpace(map.FreshStoreClusterId))
            throw new InvalidDataException("UM-FUTURE-BACKUP-NEW-MAP-ONLY");
        Stopped(state);
        string directory=Path.GetFullPath(package);
        // Existing native validation checks manifest ownership, complete
        // marker, size bounds, reparse links, exact file inventory and hash.
        var manifest=TestBackupPackage.Verify(directory,map.Id);
        if(!manifest.Scope.Split(';')[0].Split(',').Any(x=>
            x.Trim().Equals(nameof(LocalBackupScope.WorldAndProfiles),
                StringComparison.Ordinal))||
           manifest.CreatedUtc>DateTimeOffset.UtcNow.AddMinutes(5)||
           manifest.CreatedUtc<DateTimeOffset.UtcNow.AddHours(-6)||
           manifest.Files.Count==0)
            throw new InvalidDataException("UM-FUTURE-BACKUP-WORLD-SCOPE-OR-AGE");
        bool world=manifest.Files.Any(f=>
            f.Size>0&&f.Path.Replace('\\','/').StartsWith(
                "ShooterGame/Saved/",StringComparison.OrdinalIgnoreCase)&&
            f.Path.EndsWith(".ark",StringComparison.OrdinalIgnoreCase));
        if(!world)
            throw new InvalidDataException("UM-FUTURE-BACKUP-WORLD-NOT-FOUND");
        // Require a SECOND process identity observation after all backup
        // bytes have been hashed, not a stale stopped result from the UI.
        Stopped(freshProcessCheck?.Invoke()??state);
        string manifestPath=Path.Combine(directory,"manifest.json");
        using var stream=new FileStream(manifestPath,FileMode.Open,FileAccess.Read,
            FileShare.Read,4096,FileOptions.SequentialScan);
        string digest=Convert.ToHexString(SHA256.HashData(stream));
        return new(map.Id,digest,directory,manifest.Files.Count,
            manifest.Files.Sum(f=>f.Size),manifest.CreatedUtc);
    }
}
