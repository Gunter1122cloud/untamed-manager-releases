using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// Original Kova/ASA configuration parity: ASA handles survivor transfers.
/// This guard only checks source-owned map identity, launch flags and the
/// existing physical transfer path. No upload, world restore, character
/// handling, generated cluster ID or writes to Kova/ASA are performed.
/// </summary>
public static class LegacyStoreV1SourceLaunchParity
{
    public static LegacyStoreV1OriginalMap RequireOriginalMap(
        LocalMap map,LegacyStoreV1OriginalLocalSelection source)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(source);
        if(!map.HistoricalStoreProtected||map.Id==Guid.Empty||
           source.Maps?.Length!=5||
           source.Maps.Select(m=>m.MapId).Distinct().Count()!=5||
           !string.Equals(map.HistoricalArchiveSha256,
               source.FourFilesSha256,StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-LAUNCH-ORIGINAL-SOURCE-IDENTITY");
        var original=source.Maps.SingleOrDefault(m=>m.MapId==map.Id);
        if(original==null||
           !string.Equals(map.HistoricalMapKey,original.MapKey,
               StringComparison.Ordinal)||
           map.GamePort!=original.GamePort||
           map.QueryPort!=original.QueryPort||
           !string.Equals(Path.GetFullPath(map.InstallationPath),
               Path.GetFullPath(original.OriginalInstallationRoot),
               StringComparison.OrdinalIgnoreCase)||
           map.HistoricalEnabledMods==null||map.HistoricalPassiveMods==null||
           !map.HistoricalEnabledMods.SequenceEqual(original.EnabledMods,
               StringComparer.Ordinal)||
           !map.HistoricalPassiveMods.SequenceEqual(original.PassiveMods,
               StringComparer.Ordinal))
            throw new InvalidDataException("UM-STORE-LAUNCH-ORIGINAL-CONFIG-CHANGED");
        return original;
    }

    public static void RequireAsaLaunchArguments(
        LocalMap map,LegacyStoreV1OriginalLocalSelection source,
        string originalSharedTransferDirectory,string originalGameUserSettingsIni,
        string[] actualArguments)
    {
        var original=RequireOriginalMap(map,source);
        // The path passed here MUST come from the verified original ACTIVE/
        // DONE two-peer Store guard, not from React or a newly created folder.
        // Build is the exact original Kova active-phase grammar.
        var expected=LegacyStoreV1ActiveLaunch.Build(new(
            original.MapKey,original.GamePort,original.QueryPort,
            original.EnabledMods,original.PassiveMods,
            VaultProtocol.ClusterId,originalSharedTransferDirectory,
            "active",originalGameUserSettingsIni));
        if(actualArguments==null||
           !actualArguments.SequenceEqual(expected.Arguments,StringComparer.Ordinal)||
           actualArguments.Count(a=>a.Equals("-usestore",
               StringComparison.OrdinalIgnoreCase))!=1||
           actualArguments.Any(a=>a.Contains("converttostore",
               StringComparison.OrdinalIgnoreCase)||
               a.Contains("convertfromstore",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-STORE-LAUNCH-ASA-TRANSFER-PARITY");
    }
}
