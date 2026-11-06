using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record DevelopmentRequirement(string Id,string Title,string SourceDocument,
    string CodeReferences,string[] BacklogTasks,string Status,string Changes,
    string TestType,string TestResult,string ImplementationCommit,string ImplementationPr,string Remaining);
public sealed record DevelopmentWorkLedger(int Schema,string Product,string AuditCommit,string Branch,
    string ImplementationCommit,string ImplementationPr,bool FinalReleaseReady,string DatabaseBaseline,
    string[] SourceDocuments,DevelopmentRequirement[] Records);
public static class DevelopmentLedger
{
    public static DevelopmentWorkLedger Read()
    {
        using var resource=typeof(DevelopmentLedger).Assembly.GetManifestResourceStream("UntamedManager.WorkLedger.json")
            ??throw new InvalidDataException("UNTAMED_LEDGER_RESOURCE_MISSING");
        var result=JsonSerializer.Deserialize<DevelopmentWorkLedger>(resource)
            ??throw new InvalidDataException("UNTAMED_LEDGER_INVALID");
        Validate(result);return result;
    }
    public static void Validate(DevelopmentWorkLedger ledger)
    {
        string[] states={"MISSING","PARTIAL","IMPLEMENTED","TESTED","BLOCKED","VERIFIED_ON_ISOLATED_ASA","PRODUCTION_ACCEPTED"};
        if(ledger.Schema!=1||ledger.SourceDocuments.Length!=7||ledger.SourceDocuments.Distinct().Count()!=7||
           ledger.Records.Select(x=>x.Id).Distinct().Count()!=ledger.Records.Length||
           ledger.Records.Any(x=>!states.Contains(x.Status)||!ledger.SourceDocuments.Contains(x.SourceDocument)||
               string.IsNullOrWhiteSpace(x.CodeReferences)||string.IsNullOrWhiteSpace(x.TestType)||
               x.BacklogTasks.Any(t=>!ledger.Records.Any(r=>r.Id==t))))
            throw new InvalidDataException("UNTAMED_LEDGER_COVERAGE_INVALID");
        foreach(var required in new[]{("OP",61),("M",44),("I",42),("T",33),("G",41)})
            if(ledger.Records.Count(x=>System.Text.RegularExpressions.Regex.IsMatch(x.Id,"^"+required.Item1+"[0-9]{2}$"))!=required.Item2)
                throw new InvalidDataException("UNTAMED_LEDGER_COUNT_INVALID");
        if(ledger.DatabaseBaseline!="10.11.19")throw new InvalidDataException("UNTAMED_LEDGER_DATABASE_BASELINE");
        if(ledger.FinalReleaseReady&&(ledger.Records.Any(x=>x.Status is not ("TESTED" or "VERIFIED_ON_ISOLATED_ASA" or "PRODUCTION_ACCEPTED"))||
           ledger.Records.Where(x=>new[]{"G02","G03","G04","G06","G07","G08","G09","G10"}.Contains(x.Id))
               .Any(x=>x.Status is not ("VERIFIED_ON_ISOLATED_ASA" or "PRODUCTION_ACCEPTED"))))
            throw new InvalidDataException("UNTAMED_LEDGER_UNVERIFIED_RELEASE");
    }
}
