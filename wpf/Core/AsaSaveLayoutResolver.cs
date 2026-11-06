using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

/// <summary>
/// Resolves the ACTUAL ASA world layout from the registered launch map token
/// and optional ?AltSaveDirectoryName=. All backup, restore, SaveComplete,
/// and character-inventory operations must use the same interpretation.
/// This never scans for a convenient .ark in another map's directory and
/// never creates, changes or deletes user/ASA data.
/// </summary>
public static class AsaSaveLayoutResolver
{
    public sealed record Layout(string MapToken,string SaveDirectoryName,
        string SavedArksRoot,string WorldDirectory,string WorldFile,
        bool ExplicitAlternate,string Evidence);
    private static readonly Regex NameRule=new(
        @"\A[A-Za-z0-9_\-]{3,80}\z",RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(150));
    private static readonly Regex LaunchToken=new(
        @"\A\s*[""']?([A-Za-z0-9_]{3,80})(?=[?\s""']|\z)",
        RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(150));
    private static readonly Regex Alternate=new(
        @"(?:\A|[?\s])-?AltSaveDirectoryName=([^?\s""']+)",
        RegexOptions.CultureInvariant|RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(150));
    private static readonly IReadOnlyDictionary<string,string> Known=
        new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["The Island"]="TheIsland_WP",["The Center"]="TheCenter_WP",
            ["Scorched Earth"]="ScorchedEarth_WP",["Aberration"]="Aberration_WP",
            ["Extinction"]="Extinction_WP",["Genesis Part 1"]="Genesis_WP",
            ["Valguero"]="Valguero_WP",["Ragnarok"]="Ragnarok_WP",
            ["Astraeos"]="Astraeos_WP",["Lost Colony"]="LostColony_WP"
        };
    private static string Token(string? s,string error)
    {
        if(string.IsNullOrWhiteSpace(s)||!NameRule.IsMatch(s))
            throw new InvalidDataException(error);
        return s;
    }
    private static string LocalDirectory(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value)||
           value.StartsWith(@"\\",StringComparison.Ordinal)||
           value.StartsWith("//",StringComparison.Ordinal)||
           value.Contains('~')||value.Contains('\0'))
            throw new InvalidDataException("UM-ASA-SAVE-LOCAL-PATH-REQUIRED");
        string full=Path.GetFullPath(value);
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-ASA-SAVE-FIXED-DISK-REQUIRED");
        for(var node=new DirectoryInfo(full);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-ASA-SAVE-SYMLINK-OR-JUNCTION");
        return full;
    }
    public static Layout Resolve(LocalMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        string root=LocalDirectory(map.InstallationPath);
        string args=map.Arguments??"";
        var launch=LaunchToken.Match(args);
        string? configured=launch.Success?launch.Groups[1].Value:null;
        string token;
        if(!string.IsNullOrWhiteSpace(map.HistoricalMapKey))
        {
            token=Token(map.HistoricalMapKey,"UM-ASA-SAVE-HISTORICAL-TOKEN-INVALID");
            if(configured!=null&&!configured.Equals(token,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-ASA-SAVE-LAUNCH-MAP-MISMATCH");
        }
        else if(Known.TryGetValue(map.Name??"",out var expected))
        {
            token=expected;
            if(configured!=null&&!configured.Equals(token,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-ASA-SAVE-LAUNCH-MAP-MISMATCH");
        }
        else if(configured!=null)
            token=Token(configured,"UM-ASA-SAVE-CUSTOM-MAP-TOKEN-INVALID");
        else throw new InvalidDataException("UM-ASA-SAVE-CUSTOM-MAP-TOKEN-REQUIRED");

        var alternate=Alternate.Matches(args);
        if(alternate.Count>1 ||
           (alternate.Count==0&&args.Contains("AltSaveDirectoryName",
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("UM-ASA-SAVE-ALTERNATE-INVALID");
        string folder=alternate.Count==1 ?
            Token(alternate[0].Groups[1].Value,"UM-ASA-SAVE-ALTERNATE-INVALID"):
            token;
        string saved=LocalDirectory(Path.Combine(root,"ShooterGame","Saved","SavedArks"));
        string directory=LocalDirectory(Path.Combine(saved,folder));
        string world=Path.Combine(directory,token+".ark");
        if(File.Exists(world)&&(File.GetAttributes(world)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-ASA-SAVE-WORLD-LINK");
        return new(token,folder,saved,directory,world,alternate.Count==1,
            alternate.Count==1?"EXPLICIT_ALT_SAVE_DIRECTORY":"MAP_TOKEN_SAVE_DIRECTORY");
    }

    public static void DemandUniqueOwner(LocalMap selected,
        IEnumerable<LocalMap> locallyRegistered)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(locallyRegistered);
        var current=Resolve(selected);
        foreach(var peer in locallyRegistered.Where(m=>m.Id!=selected.Id &&
            !string.IsNullOrWhiteSpace(m.InstallationPath)))
        {
            // A map whose layout is unresolvable is not safe evidence of
            // non-overlap: require the operator to repair its registration.
            var other=Resolve(peer);
            if(string.Equals(other.WorldDirectory,current.WorldDirectory,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(other.WorldFile,current.WorldFile,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UM-ASA-SAVE-MULTI-MAP-OWNERSHIP-COLLISION");
        }
    }
}
