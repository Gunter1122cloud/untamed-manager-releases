using System;
using System.Linq;

namespace UntamedDesktop;

internal static class DesktopTrayStatus
{
    internal static string Describe(DesktopAutomaticAsaDiscoverySupervisor.State state)
    {
        if(!state.Fresh||state.Status!="ok")
            return "ASA-folyamatok: állapot nem ismert";
        int observed=state.Processes.Where(p=>p.ProcessKind=="asaServer"&&p.Pid>0)
            .Select(p=>p.Pid).Distinct().Count();
        return "Megfigyelt helyi ASA-folyamatok: "+observed;
    }
}
