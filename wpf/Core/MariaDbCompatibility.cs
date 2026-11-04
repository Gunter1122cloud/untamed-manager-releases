using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

public sealed record MariaDbCompatibilityResult(bool Compatible,bool LiveVersionVerified,
    Version? Server,Version? DumpTool,Version? RestoreTarget,IReadOnlyList<string> Problems);

// Version evidence is supplied by an explicitly authorized collector. This
// policy opens no database/process and cannot authorize a dump or restore.
public static class MariaDbCompatibility
{
    public static readonly Version DeploymentBaseline=new(10,11,19);
    public static readonly IReadOnlyList<string> Databases=Array.AsReadOnly(
        new[]{"arkshop","arkpermissions","lethalquests"});
    public static MariaDbCompatibilityResult Evaluate(string serverText,string dumpText,
        string restoreText,bool serverQueriedReadOnly,IEnumerable<string> databases)
    {
        var issues=new List<string>();
        Version? Parse(string input)
        {
            if(input==null||input.Length>256)return null;
            var match=Regex.Match(input,@"(?<![0-9.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![0-9.])");
            return match.Success&&Version.TryParse(match.Value,out var value)?value:null;
        }
        var server=Parse(serverText);var dump=Parse(dumpText);var target=Parse(restoreText);
        if(!serverQueriedReadOnly)issues.Add("SERVER_VERSION_NOT_QUERIED");
        if(server!=DeploymentBaseline)issues.Add("SERVER_BASELINE_MISMATCH");
        bool SameSeries(Version? v)=>v!=null&&v.Major==10&&v.Minor==11;
        if(!SameSeries(dump))issues.Add("DUMP_TOOL_SERIES_MISMATCH");
        else if(dump!=DeploymentBaseline)issues.Add("DUMP_TOOL_BASELINE_MISMATCH");
        if(!SameSeries(target))issues.Add("RESTORE_TARGET_SERIES_MISMATCH");
        else if(target!=DeploymentBaseline)issues.Add("RESTORE_TARGET_BASELINE_MISMATCH");
        var selected=databases?.ToArray()??Array.Empty<string>();
        if(selected.Length!=3||selected.Distinct(StringComparer.Ordinal).Count()!=3||
           !selected.ToHashSet(StringComparer.Ordinal).SetEquals(Databases))
            issues.Add("DATABASE_SET_MISMATCH");
        return new(issues.Count==0,serverQueriedReadOnly,server,dump,target,issues);
    }
}
