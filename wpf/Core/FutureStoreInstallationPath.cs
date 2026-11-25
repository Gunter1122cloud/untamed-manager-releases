using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// Installation identity is independent of a map's level token.
// Pure Windows path validation; physical/lease verification remains at apply.
public static class FutureStoreInstallationPath
{
    public static string Canonical(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Length>900||
           !Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",StringComparison.Ordinal)||
           path.Any(char.IsControl)||path.Contains('~')||
           path.IndexOfAny(new[]{'"','<','>','|','*','?'})>=0)
            throw new InvalidDataException("UM-FUTURE-STORE-LOCAL-ROOT-INVALID");
        string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string volume=Path.GetPathRoot(full)??"";
        if(string.Equals(full,Path.TrimEndingDirectorySeparator(volume),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-FUTURE-STORE-DRIVE-ROOT");
        foreach(string part in path.Substring(Path.GetPathRoot(path)!.Length).Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries))
        {
            string stem=part.Split('.')[0];
            if(part is "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||
               part.Contains(':')||stem.Equals("CON",StringComparison.OrdinalIgnoreCase)||
               stem.Equals("PRN",StringComparison.OrdinalIgnoreCase)||stem.Equals("AUX",StringComparison.OrdinalIgnoreCase)||
               stem.Equals("NUL",StringComparison.OrdinalIgnoreCase)||
               (stem.Length==4&&(stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||
                 stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("UM-FUTURE-STORE-LOCAL-ROOT-ALIAS");
        }
        return full;
    }
    public static bool Overlaps(string first,string second)
    {
        string a=Canonical(first),b=Canonical(second);
        return a.Equals(b,StringComparison.OrdinalIgnoreCase)||
            a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
}
