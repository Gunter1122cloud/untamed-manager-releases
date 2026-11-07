using System;
using System.IO;

namespace UntamedManager.Core;

/// <summary>
/// Fail-closed missing-marker evidence for the ORIGINAL Store-v1 shared
/// coordination directory. File.Exists/Directory.Exists intentionally
/// swallow SMB access errors and could misreport "not present" during a
/// network or permission fault. GetAttributes must distinguish a truly
/// missing marker from an unreadable shared state.
///
/// This is read-only. NEVER deletes or clears durable locks, and never
/// authorizes a new session or Store conversion.
/// </summary>
public static class LegacyStoreV1SharedMarkerProbe
{
    public static bool IsDefinitelyAbsent(string sharedCoordinationDirectory,
        string markerName)
    {
        if(markerName is not ("running.json" or "mutation.lock" or "cancelled.json"))
            throw new InvalidDataException("Unsupported original Store-v1 marker");
        if(string.IsNullOrWhiteSpace(sharedCoordinationDirectory) ||
           !Path.IsPathFullyQualified(sharedCoordinationDirectory) ||
           !Directory.Exists(sharedCoordinationDirectory) ||
           (File.GetAttributes(sharedCoordinationDirectory)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException(
                "Original Store-v1 coordination directory cannot be verified");
        string path=Path.Combine(sharedCoordinationDirectory,markerName);
        try
        {
            // Both existing files and directories are blockers. A linked
            // marker is also present and may never be silently discarded.
            _=File.GetAttributes(path);
            return false;
        }
        catch(FileNotFoundException)
        {
            // Network state may have changed during the probe. Recheck
            // parent directory; do not treat lost access as an absent lock.
            if(!Directory.Exists(sharedCoordinationDirectory) ||
               (File.GetAttributes(sharedCoordinationDirectory)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException(
                    "Original Store-v1 marker parent changed during read");
            return true;
        }
        // DirectoryNotFound, IOException, UnauthorizedAccessException or
        // SMB transport failure all propagate => DENY shared writer actions.
    }
}
