using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// Fresh-ASA lifecycle operations are NEVER allowed to share an installation
/// with an imported/historic Store-v1 profile, even if a different map in the
/// same installation is missing its own historical flag. A local process start
/// or stop may otherwise load a shared historical Store profile indirectly.
/// This guard does not mutate config or store metadata.
/// </summary>
public static class FreshAsaInstallationSafety
{
    public static bool IsProvenFresh(ClusterService cluster,LocalMap map)
    {
        if(cluster==null||map==null||map.Id==Guid.Empty ||
           !cluster.Config.LocalMaps.Contains(map))
            return false;
        try
        {
            if(string.IsNullOrWhiteSpace(map.InstallationPath)||
               !Path.IsPathFullyQualified(map.InstallationPath)||
               map.InstallationPath.StartsWith(@"\\",StringComparison.Ordinal))
                return false;
            string root=Path.GetFullPath(map.InstallationPath)
                .TrimEnd(Path.DirectorySeparatorChar);
            var group=cluster.Config.LocalMaps.Where(x=>
                !string.IsNullOrWhiteSpace(x.InstallationPath)&&
                Path.GetFullPath(x.InstallationPath).TrimEnd(
                    Path.DirectorySeparatorChar).Equals(root,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if(group.Length==0)return false;
            return group.All(x=>
                !KovaCompatibleWorldBackup.HasLiveOrHistoricalStore(x) &&
                string.IsNullOrWhiteSpace(x.HistoricalArchiveSha256) &&
                !KovaCompatibleWorldBackup.InstallationRecoveryRequired(x));
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return false; // uncertain ownership and recovery state always deny
        }
    }

    public static void Demand(ClusterService cluster,LocalMap map)
    {
        if(!IsProvenFresh(cluster,map))
            throw new InvalidOperationException(
                "UM-FRESH-ASA-SHARED-INSTALLATION-HISTORICAL-OR-RECOVERY");
    }
}
