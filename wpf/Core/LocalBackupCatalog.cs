using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// Safe read-only history for explicitly owned local backup packages.
// This is not a restore facility and never reads SQL or shared Legacy Store data.
public sealed record LocalBackupHistoryEntry(string PackageDirectory,bool Valid,
    int FileCount,DateTimeOffset? CreatedUtc,string Status,long TotalBytes=0);

public static class LocalBackupCatalog
{
    public static IReadOnlyList<LocalBackupHistoryEntry> Inspect(string localRoot,Guid mapId,
        int maxPackages=15)
    {
        if(mapId==Guid.Empty)throw new InvalidDataException("Explicit map owner required");
        if(maxPackages<1||maxPackages>50)throw new ArgumentOutOfRangeException(nameof(maxPackages));
        if(string.IsNullOrWhiteSpace(localRoot)||!Path.IsPathFullyQualified(localRoot)||
           localRoot.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit local backup path required");
        string root=Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("Local fixed backup volume required");
        for(var parent=new DirectoryInfo(root);parent!=null;parent=parent.Parent)
            if(parent.Exists && (parent.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked backup path rejected");
        if(!Directory.Exists(root))return Array.Empty<LocalBackupHistoryEntry>();

        var result=new List<LocalBackupHistoryEntry>();
        foreach(var directory in new DirectoryInfo(root).GetDirectories()
            .OrderByDescending(x=>x.Name,StringComparer.OrdinalIgnoreCase).Take(maxPackages))
        {
            if((directory.Attributes&FileAttributes.ReparsePoint)!=0)
            {
                result.Add(new(directory.FullName,false,0,null,"Linked backup rejected"));
                continue;
            }
            if(directory.Name.EndsWith(".incomplete",StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new(directory.FullName,false,0,null,"Incomplete backup (not restorable)"));
                continue;
            }
            try
            {
                var manifest=TestBackupPackage.Verify(directory.FullName,mapId);
                result.Add(new(directory.FullName,true,manifest.Files.Count,
                    manifest.CreatedUtc,"SHA-256 verified; ASA consistency unproven",
                    manifest.Files.Sum(file=>file.Size)));
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or
                                     System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                result.Add(new(directory.FullName,false,0,null,"Invalid backup: "+ex.Message));
            }
        }
        return result;
    }
}
