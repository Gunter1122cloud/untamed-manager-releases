using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class LocalLogCenterTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException("LOG CENTER FAILED: "+reason);
        Console.WriteLine("PASS log-center "+reason);
    }
    public static Task Run(string root)
    {
        string work=Path.Combine(root,"premium-logs-test");
        string install=Path.Combine(work,"local-asa");
        string logs=Path.Combine(install,"ShooterGame","Saved","Logs");
        Directory.CreateDirectory(logs);
        string file=Path.Combine(logs,"ShooterGame.log");
        var lines=Enumerable.Range(0,2200).Select(i=>
            i%27==0?"[ASA] Warning: synthetic slow tick "+i:
            i%31==0?"[ASA] Error: synthetic plugin failure "+i:
                "[ASA] healthy synthetic trace "+i).ToList();
        lines.Add("ServerAdminPassword=NEVER_SHOW_THIS_TO_USER");
        lines.Add("Token=KEEP_PRIVATE in synthetic stream");
        lines.Add("https://discord.com/api/webhooks/12345/SECRET_WEBHOOK_PART");
        lines.Add("[ASA] Error: synthetic last error");
        File.WriteAllLines(file,lines);
        var map=new LocalMap{Id=Guid.NewGuid(),Name="Offline synthetic ASA",
            InstallationPath=install,LogFilePath=file};
        long fileSize=new FileInfo(file).Length;
        byte[] original=File.ReadAllBytes(file);
        var result=LocalLogCenter.Read(map,24_000,130);
        Check(result.MapId==map.Id && result.TotalBytes==fileSize &&
              result.Lines.Count<=130 &&result.Truncated&&
              result.FileName.EndsWith("ShooterGame.log",StringComparison.OrdinalIgnoreCase),
            "bounded tail reads only the selected local ASA file");
        Check(result.Lines.Last().Content.Contains("synthetic last error") &&
              result.Lines.All(x=>!x.Content.Contains("NEVER_SHOW_THIS_TO_USER") &&
                  !x.Content.Contains("KEEP_PRIVATE")&&!x.Content.Contains("SECRET_WEBHOOK_PART")) &&
              result.Lines.Any(x=>x.Content.Contains("[REDACTED]")),
            "read-only tail redacts password token and webhook before returning UI data");
        var errors=LocalLogCenter.Filter(result,"synthetic",LocalLogLevel.Error,10);
        Check(errors.Count>0&&errors.Count<=10&&errors.All(x=>x.Level==LocalLogLevel.Error),
            "case-insensitive search and error-only filter work together");
        Check(LocalLogCenter.Filter(result,"nonsense_never_matches",null).Count==0,
            "search returns empty results without changing file");
        Check(File.ReadAllBytes(file).SequenceEqual(original),
            "read, search and severity filtering leave log byte-identical");
        bool outside=false,badExtension=false,noMap=false,invalidBound=false;
        try{LocalLogCenter.Read(map with { LogFilePath=Path.Combine(work,"outside.log") });}
        catch(InvalidDataException){outside=true;}
        try{LocalLogCenter.Read(map with { LogFilePath=Path.Combine(logs,"not-a-log.dll") });}
        catch(InvalidDataException){badExtension=true;}
        try{LocalLogCenter.Read(map with { Id=Guid.Empty });}
        catch(InvalidDataException){noMap=true;}
        try{LocalLogCenter.Read(map,1_048_577,130);}
        catch(ArgumentOutOfRangeException){invalidBound=true;}
        Check(outside&&badExtension&&noMap&&invalidBound,
            "foreign paths, nontext extensions, missing ownership and unbounded reads fail closed");
        bool hugeQuery=false;
        try{LocalLogCenter.Filter(result,new string('X',129));}
        catch(InvalidDataException){hugeQuery=true;}
        Check(hugeQuery,"search input is bounded before displaying filtered content");
        string binary=Path.Combine(logs,"binary.log");
        File.WriteAllBytes(binary,new byte[]{65,0,66,10});
        bool binaryRejected=false;
        try{LocalLogCenter.Read(map with{LogFilePath=binary});}
        catch(InvalidDataException){binaryRejected=true;}
        Check(binaryRejected,"NUL-containing binary file is never rendered as an ASA text log");
        Check(result.Lines.All(x=>x.Content.Length<=8212),
            "individual displayed lines are length-limited");
        return Task.CompletedTask;
    }
}
