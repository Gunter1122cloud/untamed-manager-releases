using System;
using UntamedManager.Core;

public static class ScrollBoundaryTests
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("SCROLL REGRESSION: "+reason);
        Console.WriteLine("PASS scroll "+reason);
    }

    public static void Run()
    {
        Check(!ScrollBoundaryPolicy.ShouldChain(5, 12, 120),
            "interior upwards wheel remains in the list");
        Check(!ScrollBoundaryPolicy.ShouldChain(5, 12, -120),
            "interior downwards wheel remains in the list");
        Check(ScrollBoundaryPolicy.ShouldChain(0, 12, 120),
            "upwards wheel at first row reaches the page");
        Check(ScrollBoundaryPolicy.ShouldChain(12, 12, -120),
            "downwards wheel at last row reaches the page");
        Check(ScrollBoundaryPolicy.ShouldChain(0, 0, -120),
            "short list does not trap page scrolling");
        Check(!ScrollBoundaryPolicy.ShouldChain(0, 0, 0),
            "zero-delta events do not change scrolling");
        Check(ScrollBoundaryPolicy.ForwardOffset(250, 900, 400, -120, 3)>250,
            "page moves down after inner list reaches bottom");
        Check(ScrollBoundaryPolicy.ForwardOffset(250, 900, 400, 120, 3)<250,
            "page moves up after inner list reaches top");
        Check(ScrollBoundaryPolicy.ForwardOffset(900, 900, 400, -120, 3)==900,
            "page lower edge is clamped");
        Check(ScrollBoundaryPolicy.ForwardOffset(0, 900, 400, 120, 3)==0,
            "page upper edge is clamped");
        Check(ScrollBoundaryPolicy.ForwardOffset(250, 900, 400, -120, 0)==250,
            "OS disabled wheel scrolling is respected");
        Check(ScrollBoundaryPolicy.ForwardOffset(250, 900, 400, -120, -1)>250,
            "OS page-scrolling preference is respected");
    }
}
