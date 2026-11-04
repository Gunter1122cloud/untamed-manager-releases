using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public sealed record StoreAssessment(bool Safe, string Reason);
public static class LocalStoreProbe
{
    public static StoreAssessment Inspect(string root, string clusterId, string arguments)
    {
        // No network probing in this development adapter, including mapped network drives.
        if(string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\"))
            return new(false,"Only an explicit local test copy can be inspected");
        try
        {
            string full=Path.GetFullPath(root);
            if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
                return new(false,"Store test copy must be on a fixed local disk");
            for(var dir=new DirectoryInfo(full);dir!=null;dir=dir.Parent)
                if(dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint)!=0)
                    return new(false,"Linked Store paths are forbidden");
            var tokens=arguments.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(clusterId!=VaultProtocol.ClusterId || !tokens.Contains("-usestore",StringComparer.OrdinalIgnoreCase) ||
                arguments.Contains("converttostore",StringComparison.OrdinalIgnoreCase))
                return new(false,"Cluster ID or Store launch parameters are unsafe");
            if(!Directory.Exists(full))return new(false,"Store test copy unavailable");
            // Actual Kova metadata schema has not been supplied or validated. Never invent compatibility.
            return new(false,"Store reachable; metadata schema unverified. Dangerous operations blocked");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false,"Store inspection failed: "+ex.Message); }
    }
}

