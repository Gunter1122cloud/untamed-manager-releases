using System;
using System.IO;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Manager-local resource limits for newly created (not already-existing)
/// offline world backup packages. The operator can explicitly narrow the
/// budget by placing a UTF-8 backup-resource-limits-v1.json in the Obsidian
/// profile root. Missing config uses safe, visible defaults; invalid config
/// fails CLOSED, never falls back to an unrestricted copy. The limits are
/// per map/package, not an aggregate cap for a multi-host catalog.
/// </summary>
public sealed record BackupResourcePolicy(int MaxFiles,long MaxTotalBytes,bool Customized)
{
    public const int DefaultFiles=100_000;
    public const long DefaultBytes=2L*1024*1024*1024*1024;
    public const int MinFiles=1;
    public const long MinBytes=1L*1024*1024;
    public const string FileName="backup-resource-limits-v1.json";
    public static BackupResourcePolicy Default {get;}=
        new(DefaultFiles,DefaultBytes,false);

    public void DemandFits(int files,long bytes)
    {
        if(files<1||files>MaxFiles)
            throw new InvalidDataException("UM-BACKUP-RESOURCE-FILE-LIMIT");
        if(bytes<0||bytes>MaxTotalBytes)
            throw new InvalidDataException("UM-BACKUP-RESOURCE-BYTE-LIMIT");
    }

    public static BackupResourcePolicy ForManagerProfile(string managerProfile)
    {
        string directory=VaultReadSnapshot.LocalPath(managerProfile,true);
        string path=Path.Combine(directory,FileName);
        if(!File.Exists(path))
        {
            if(Directory.Exists(path))
                throw new InvalidDataException("UM-BACKUP-RESOURCE-POLICY-NOT-FILE");
            return Default;
        }
        using var held=new VaultReadSnapshot();
        JsonDocument parsed;
        try
        {
            parsed=JsonDocument.Parse(held.Read(path,4096),
                new JsonDocumentOptions{MaxDepth=5});
        }
        catch(JsonException e)
        {
            throw new InvalidDataException("UM-BACKUP-RESOURCE-POLICY-INVALID",e);
        }
        using var json=parsed;
        var root=json.RootElement;
        if(root.ValueKind!=JsonValueKind.Object ||
           !root.TryGetProperty("schema",out var version)||
           version.ValueKind!=JsonValueKind.Number||
           !version.TryGetInt32(out var schema)||schema!=1||
           !root.TryGetProperty("maxFiles",out var files)||
           files.ValueKind!=JsonValueKind.Number||
           !files.TryGetInt32(out var maxFiles)||
           !root.TryGetProperty("maxTotalBytes",out var bytes)||
           bytes.ValueKind!=JsonValueKind.Number||
           !bytes.TryGetInt64(out var maxBytes)||
           maxFiles is <MinFiles or >DefaultFiles ||
           maxBytes is <MinBytes or >DefaultBytes)
            throw new InvalidDataException("UM-BACKUP-RESOURCE-POLICY-INVALID");
        // The hard ceilings are deliberate: an operator may reduce resource
        // exposure without accidentally bypassing Core's archive validators.
        return new(maxFiles,maxBytes,true);
    }
}
