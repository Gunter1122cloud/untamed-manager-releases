using System;
using System.IO;
using UntamedManager.Core;

public static class NetworkLatencyTests
{
    private static void Check(bool b,string message)
    {
        if(!b)throw new Exception("INTEGRATION TEST FAILED network: "+message);
        Console.WriteLine("PASS network "+message);
    }
    public static void Run()
    {
        Check(NetworkLatencyDiagnostics.ValidTarget("192.168.1.254") &&
            NetworkLatencyDiagnostics.ValidTarget("1.1.1.1") &&
            !NetworkLatencyDiagnostics.ValidTarget("localhost") &&
            !NetworkLatencyDiagnostics.ValidTarget("127.0.0.1") &&
            !NetworkLatencyDiagnostics.ValidTarget("224.1.1.1") &&
            !NetworkLatencyDiagnostics.ValidTarget("1.1.1.1:443"),
            "diagnostic only accepts direct unambiguous unicast IPv4 targets");
        double?[] rows={1,2,5,null,4};
        var result=NetworkLatencyDiagnostics.Analyze("1.1.1.1",rows);
        Check(result.Samples==5&&result.Responses==4&&
            result.LossPercent==20 &&
            Math.Abs(result.MedianMs!.Value-3)<0.01&&
            result.P95Ms!.Value>4&&result.P95Ms.Value<5&&
            result.MaxMs==5&&result.MeanAbsoluteJitterMs is > 1 and < 2,
            "packet-loss, median, p95 and jitter calculations use only answered ICMP packets");
        var unknown=NetworkLatencyDiagnostics.Analyze("192.168.1.254",
            new double?[]{null,null,null});
        Check(unknown.LossPercent==100 &&unknown.Responses==0 &&
            unknown.P95Ms==null&&unknown.MedianMs==null &&
            unknown.MeanAbsoluteJitterMs==null,
            "no ICMP response means unknown RTT, never fabricated zero-millisecond latency");
        bool invalid=false;
        try{NetworkLatencyDiagnostics.Analyze("1.1.1.1",
            new double?[]{double.NaN,1,2});}
        catch(InvalidDataException){invalid=true;}
        Check(invalid,"invalid synthetic time samples rejected before interpretation");
        bool tooFew=false;
        try{NetworkLatencyDiagnostics.Analyze("1.1.1.1",
            new double?[]{1,2});}
        catch(InvalidDataException){tooFew=true;}
        Check(tooFew,"too-few latency samples cannot be reported as a stable network measurement");
    }
}
