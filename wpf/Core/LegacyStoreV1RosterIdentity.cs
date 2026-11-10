using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

/// <summary>
/// Historical Kova's EXACT five map GUID/name selections, hashed in a
/// deterministic order. This is a passive source-vs-Manager registry check:
/// no original UUIDs, installation paths or source bytes leave the host.
/// Source metadata alone never certifies current registered ASA map ownership.
/// </summary>
internal static class LegacyStoreV1RosterIdentity
{
    internal static string Tag(IEnumerable<(Guid Id,string MapKey,string MapRoot)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var maps=entries.ToArray();
        if(maps.Length!=5 || maps.Any(m=>m.Id==Guid.Empty||
                string.IsNullOrWhiteSpace(m.MapKey)||m.MapKey.Length>100||
                m.MapKey.Any(char.IsControl)||
                string.IsNullOrWhiteSpace(m.MapRoot)||
                !Path.IsPathFullyQualified(m.MapRoot)||
                m.MapRoot.StartsWith(@"\\",StringComparison.Ordinal))||
           maps.Select(m=>m.Id).Distinct().Count()!=5||
           maps.Select(m=>m.MapKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=5)
            throw new InvalidDataException("UM-STORE-ROSTER-ORIGINAL-FIVE-MAPS-INVALID");
        string canonical=string.Join("\n",maps
            .OrderBy(m=>m.Id)
            .Select(m=>m.Id.ToString("D")+":"+m.MapKey.ToUpperInvariant()+
                ":"+Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(m.MapRoot)).ToUpperInvariant()));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("ORIGINAL_KOVA_MAP_ROSTER_V1\n"+canonical)));
    }
}
