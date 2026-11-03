using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;
public static class LocalContentOperations
{
    private static readonly Regex ModsParameter =
        new(@"(?<!\S)-mods=(?<ids>[^ \t""]+)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    // Strict, read-only inspection of the exact launch argument order. Unknown or duplicate
    // entries are rejected rather than silently normalized, preserving existing settings.
    public static IReadOnlyList<string> ReadMods(string? existing)
    {
        existing??="";
        var matches=ModsParameter.Matches(existing);
        if(matches.Count>1 || Regex.Matches(existing,@"(?<!\S)-mods=",RegexOptions.IgnoreCase).Count!=matches.Count)
            throw new InvalidDataException("Ambiguous or malformed -mods argument; manual review required");
        if(matches.Count==0)return Array.Empty<string>();
        var mods=matches[0].Groups["ids"].Value.Split(',',StringSplitOptions.None);
        if(mods.Any(x=>!Regex.IsMatch(x,@"\A[0-9]{5,12}\z")) ||
           mods.Distinct(StringComparer.Ordinal).Count()!=mods.Length)
            throw new InvalidDataException("Invalid or duplicate mod IDs; manual review required");
        return mods;
    }
    // Reorder only; never installs, removes, or changes membership. Preserve all other
    // launch flags verbatim and leave the running server untouched.
    public static string ReorderMods(string existing,IEnumerable<string> order)
    {
        existing??="";
        if(order==null)throw new ArgumentNullException(nameof(order));
        var current=ReadMods(existing);
        if(current.Count==0)throw new InvalidOperationException("No existing -mods list to reorder");
        var proposed=order.Select(x=>x?.Trim()??"").ToArray();
        if(proposed.Length!=current.Count ||
           proposed.Any(x=>!Regex.IsMatch(x,@"\A[0-9]{5,12}\z")) ||
           proposed.Distinct(StringComparer.Ordinal).Count()!=proposed.Length ||
           proposed.Except(current,StringComparer.Ordinal).Any())
            throw new InvalidDataException("Reorder must contain each current mod ID exactly once");
        var match=ModsParameter.Match(existing);
        return existing[..match.Index]+"-mods="+string.Join(",",proposed)+existing[(match.Index+match.Length)..];
    }
    public static string ModifyMods(string existing,IEnumerable<string> input,bool add)
    {
        existing??="";
        var requested=input.Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
        if(requested.Length==0)throw new ArgumentException("At least one mod ID required");
        if(requested.Any(x=>!Regex.IsMatch(x,@"\A[0-9]{5,12}\z")))
            throw new InvalidDataException("CurseForge mod IDs must be 5-12 digits");
        var matches=ModsParameter.Matches(existing);
        var mods=ReadMods(existing).ToList();
        if(add){foreach(var id in requested)if(!mods.Contains(id,StringComparer.Ordinal))mods.Add(id);}
        else mods.RemoveAll(x=>requested.Contains(x,StringComparer.Ordinal));
        var without=matches.Count==1?existing.Remove(matches[0].Index,matches[0].Length).Trim():existing.Trim();
        return mods.Count==0?without:
            (without.Length==0?"":without+" ")+"-mods="+string.Join(",",mods);
    }
    // Removed legacy unsafe plugin directory replacement. All plugin ZIP changes
    // must flow through SafePluginZip with a separate SHA-verified backup and stop gate.
    [Obsolete("Use SafePluginZip.Install or PluginStateManager.Toggle")]
    public static string InstallLocalPluginFolder(LocalMap map,string sourceFolder)
        => throw new NotSupportedException(
            "Legacy plugin folder copy disabled; use SHA-verified plugin transaction");
    private static void CheckNoLinks(DirectoryInfo dir)
    {
        if((dir.Attributes & FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Symlink/junction plugin folder rejected");
        foreach(var file in dir.GetFiles())
            if((file.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked plugin file rejected");
        foreach(var child in dir.GetDirectories())CheckNoLinks(child);
    }
    private static void CopyTree(string source,string dest)
    {
        Directory.CreateDirectory(dest);
        foreach(var file in Directory.EnumerateFiles(source))
            File.Copy(file,Path.Combine(dest,Path.GetFileName(file)),false);
        foreach(var sub in Directory.EnumerateDirectories(source))
            CopyTree(sub,Path.Combine(dest,Path.GetFileName(sub)));
    }
}
