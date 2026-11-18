using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace UntamedManager.Core;

// Native-only helper. Callers must first validate the approved original
// source, process ownership and actual lists, and hold configuration leases.
internal static class HistoricalLiveModSync
{
    internal static bool WriteVerifiedBaseline(LocalMap map,
        LegacyStoreV1ProcessObserver.LiveModOrder observed,
        Action save,Action markMutation)
    {
        if(!map.HistoricalStoreProtected||map.ObservedReadOnlyImported||
           map.Maintenance||map.ModChangesPending)return false;
        var saved=ModStateController.Read(map);
        if(saved.EnabledOrder.SequenceEqual(observed.Enabled)&&
           saved.Passive.SequenceEqual(observed.Passive))return false;
        var oldEnabled=map.HistoricalEnabledMods;
        var oldPassive=map.HistoricalPassiveMods;
        var oldDisabled=map.DisabledModIds;
        markMutation();
        try
        {
            map.HistoricalEnabledMods=observed.Enabled.ToList();
            map.HistoricalPassiveMods=observed.Passive.ToList();
            map.DisabledModIds=(oldDisabled??new List<string>()).Except(observed.Enabled).ToList();
            save();
            return true;
        }
        catch
        {
            map.HistoricalEnabledMods=oldEnabled;
            map.HistoricalPassiveMods=oldPassive;
            map.DisabledModIds=oldDisabled;
            throw;
        }
    }
}
