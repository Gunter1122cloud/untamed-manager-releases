using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedDesktop;

// One short-lived Discord dispatch reservation, alongside per-map native
// scheduled workers. An unrelated worker cannot deny a different map.
internal sealed class DiscordMapReservation
{
    private readonly object gate=new();
    private readonly HashSet<Guid> workers=new();
    private HashSet<Guid>? reserved;
    internal bool TryReserve(Guid[] ids)
    {
        if(ids.Length==0||ids.Any(x=>x==Guid.Empty)||ids.Distinct().Count()!=ids.Length)return false;
        lock(gate)
        {
            if(reserved!=null||ids.Any(workers.Contains))return false;
            reserved=ids.ToHashSet();return true;
        }
    }
    internal void Release(){lock(gate)reserved=null;}
    internal bool TryBeginWorker(Guid id)
    {
        lock(gate)return id!=Guid.Empty&&reserved?.Contains(id)!=true&&workers.Add(id);
    }
    internal void EndWorker(Guid id){lock(gate)workers.Remove(id);}
    internal bool HasReservation {get{lock(gate)return reserved!=null;}}
}
