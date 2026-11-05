using System;

namespace UntamedManager.Core;

/// <summary>
/// Testable, device-independent scroll boundary logic. Interior wheel input stays
/// with the inner control; only movement past its boundary can reach the page.
/// </summary>
public static class ScrollBoundaryPolicy
{
    private const double Tolerance = 0.5;

    public static bool ShouldChain(double innerOffset, double innerMaximum, int wheelDelta)
    {
        if (wheelDelta == 0 || double.IsNaN(innerOffset) || double.IsNaN(innerMaximum))
            return false;
        return wheelDelta > 0
            ? innerOffset <= Tolerance
            : innerOffset >= Math.Max(0, innerMaximum - Tolerance);
    }

    public static double ForwardOffset(
        double parentOffset, double parentMaximum, double parentViewport,
        int wheelDelta, int wheelLines)
    {
        if (wheelDelta == 0 || parentMaximum <= 0 ||
            double.IsNaN(parentOffset) || double.IsNaN(parentMaximum))
            return parentOffset;

        // Windows uses -1 for page scrolling and 0 for disabled wheel scrolling.
        if (wheelLines == 0) return parentOffset;
        double step = wheelLines < 0
            ? Math.Max(28, parentViewport * 0.8)
            : Math.Max(24, Math.Min(wheelLines, 12) * 24);
        double notches = Math.Max(1, Math.Abs(wheelDelta) / 120.0);
        return Math.Clamp(parentOffset - Math.Sign(wheelDelta) * step * notches,
            0, parentMaximum);
    }
}
