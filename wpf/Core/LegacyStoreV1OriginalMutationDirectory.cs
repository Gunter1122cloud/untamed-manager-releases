using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace UntamedManager.Core;

/// <summary>
/// Exact original Legacy shared-store.mjs maintenance mutex representation:
/// .legacy-store-v1/mutation.lock is a DIRECTORY atomically created on the
/// shared filesystem (Node fs.mkdir / EEXIST), not a second Store writer,
/// RCON lock, session, or expiring Manager lease.
///
/// Not used for ordinary ACTIVE/DONE ASA Start/SaveWorld/Stop. Only a
/// separately authorized, source-compatible Store MAINTENANCE transaction
/// may acquire this gate. This primitive by itself cannot certify live
/// writer exclusivity, a cross-host backup or production restore.
/// </summary>
internal sealed class LegacyStoreV1OriginalMutationDirectory : IDisposable
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,
        EntryPoint="CreateDirectoryW",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryExclusive(
        string path,IntPtr securityAttributes);

    private readonly string folder;
    private readonly string exactDirectoryId;
    private bool released;

    private LegacyStoreV1OriginalMutationDirectory(string folder)
    {
        this.folder=folder;
        exactDirectoryId=StorePathIdentity.LocalDirectoryFingerprint(folder);
    }

    internal static LegacyStoreV1OriginalMutationDirectory Acquire(
        string originalCoordinationDirectory)
    {
        if(!OperatingSystem.IsWindows()||
           string.IsNullOrWhiteSpace(originalCoordinationDirectory)||
           !Path.IsPathFullyQualified(originalCoordinationDirectory)||
           !Path.GetFileName(Path.TrimEndingDirectorySeparator(
               originalCoordinationDirectory)).Equals(OriginalStoreRuntimeIdentity.CoordinationDirectoryName,
                   StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "UM-STORE-ORIGINAL-MUTATION-COORDINATION-INVALID");

        string parent=Path.GetFullPath(originalCoordinationDirectory);
        if(!Directory.Exists(parent)||
           (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException(
                "UM-STORE-ORIGINAL-MUTATION-PARENT-UNVERIFIED");
        string target=Path.Combine(parent,"mutation.lock");
        // Atomic Windows/SMB CreateDirectoryW corresponds to the ORIGINAL
        // Legacy fs.mkdir. Directory.CreateDirectory is deliberately NOT used:
        // it succeeds when the directory ALREADY EXISTS, breaking exclusion.
        if(!CreateDirectoryExclusive(target,IntPtr.Zero))
            throw new IOException(
                "UM-STORE-ORIGINAL-MUTATION-BUSY-OR-ABANDONED");
        try
        {
            return new LegacyStoreV1OriginalMutationDirectory(target);
        }
        catch
        {
            // If identity confirmation fails, preserve the durable marker.
            // No TTL, guessed owner or implicit cleanup may remove it.
            throw;
        }
    }

    /// <summary>
    /// Called only after the original-compatible maintenance transaction
    /// reports verified completion. A failed/crashed operation must leave the
    /// original durable directory in place for explicit operator recovery.
    /// </summary>
    internal void ReleaseAfterVerifiedCompletion()
    {
        if(released)throw new InvalidOperationException(
            "UM-STORE-ORIGINAL-MUTATION-ALREADY-RELEASED");
        if(!string.Equals(
               StorePathIdentity.LocalDirectoryFingerprint(folder),
               exactDirectoryId,StringComparison.Ordinal))
            throw new InvalidDataException(
                "UM-STORE-ORIGINAL-MUTATION-DIRECTORY-REPLACED");
        if(Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidDataException(
                "UM-STORE-ORIGINAL-MUTATION-DIRECTORY-NOT-EMPTY");
        Directory.Delete(folder,recursive:false);
        released=true;
    }

    public void Dispose()
    {
        // Intentionally NO automatic cleanup. An aborted callback, Manager
        // crash or interrupted maintenance may not silently release Legacy's
        // original cross-process durable mutation.lock.
    }
}
