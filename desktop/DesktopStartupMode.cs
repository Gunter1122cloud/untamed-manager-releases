using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedDesktop;

/// <summary>
/// Explicit operator-selected installation acceptance mode. Suppresses only
/// autonomous, potentially mutating background schedules. All manually
/// approved local Start/Stop/Restart/SaveWorld/RCON and read-only discovery
/// remain wired to their normal native bridges; no fake ASA is substituted.
/// </summary>
public sealed record DesktopStartupMode(bool SmokeTest,bool ManualTest)
{
    public bool RunScheduledMutations => !SmokeTest && !ManualTest;
    public bool RunReadOnlyDiscovery => !SmokeTest;

    public static DesktopStartupMode Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var argv=args.ToArray();
        return new DesktopStartupMode(
            argv.Any(a=>string.Equals(a,"--smoke-test",
                StringComparison.OrdinalIgnoreCase)),
            argv.Any(a=>string.Equals(a,"--manual-test",
                StringComparison.OrdinalIgnoreCase)));
    }
}
