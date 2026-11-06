using System;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

public static class LocalMapNameRules
{
    // Display labels are extensible; suggestions for known maps are not a whitelist.
    public static string Normalize(string? value)
    {
        string name=value?.Trim()??"";
        if(name.Length is < 1 or > 96 ||
           name is "." or ".." ||
           name.Any(c=>char.IsControl(c)||Path.GetInvalidFileNameChars().Contains(c)) ||
           name.Contains("..",StringComparison.Ordinal))
            throw new InvalidDataException("Érvénytelen mapnév (1–96 karakter, tiltott fájlnévkarakterek nélkül)");
        return name;
    }
}
