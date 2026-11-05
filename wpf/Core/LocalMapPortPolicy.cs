using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// Local-map socket collisions. Game + Query both use UDP; RCON uses TCP.
/// Other machines are managed by their own Manager, never checked here.
/// Old profiles with unknown (0) ports remain readable.
/// No remote/network probing is performed.
/// </summary>
public static class LocalMapPortPolicy
{
    public static IReadOnlyList<string> Conflicts(LocalMap candidate,
        IEnumerable<LocalMap> existingMaps)
    {
        if(candidate is null)throw new ArgumentNullException(nameof(candidate));
        if(existingMaps is null)throw new ArgumentNullException(nameof(existingMaps));
        var found=new List<string>();
        if(candidate.GamePort>0 && candidate.GamePort==candidate.QueryPort)
            found.Add("A Game és a Query ugyanazt az UDP-portot használja: "+candidate.GamePort);
        if(candidate.GamePort is < 0 or > 65535 ||
           candidate.QueryPort is < 0 or > 65535 ||
           candidate.RconPort is < 0 or > 65535)
            found.Add("Érvénytelen map-port (0–65535).");
        foreach(var other in existingMaps.Where(m=>m.Id!=candidate.Id))
        {
            if(other is null)continue;
            foreach(var port in new[]{(Number:candidate.GamePort,Label:"Game UDP"),
                (Number:candidate.QueryPort,Label:"Query UDP")})
            {
                if(port.Number>0 && (port.Number==other.GamePort ||
                   port.Number==other.QueryPort))
                    found.Add(port.Label+" "+port.Number+
                        " ütközik a helyi "+other.Name+" map UDP-portjával.");
            }
            if(candidate.RconPort>0 && candidate.RconPort==other.RconPort)
                found.Add("RCON TCP "+candidate.RconPort+
                    " ütközik a helyi "+other.Name+" map RCON-portjával.");
        }
        return found;
    }

    public static void RequireNoConflicts(LocalMap candidate,
        IEnumerable<LocalMap> existingMaps)
    {
        var problems=Conflicts(candidate,existingMaps);
        if(problems.Count>0)
            throw new InvalidDataException(string.Join(Environment.NewLine,problems));
    }
}
