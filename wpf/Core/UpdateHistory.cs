using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;
public static class UpdateHistory
{
    private static string LogPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UntamedManager","Updates","history.jsonl");
    public static void Append(string phase,string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,phase,version})+Environment.NewLine);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public static string ReadRecent()
    {
        try {return string.Join(Environment.NewLine,File.ReadLines(LogPath).TakeLast(30));}
        catch(IOException){return "Még nincs frissítési előzmény.";}catch(UnauthorizedAccessException){return "Az előzmények nem olvashatók.";}
    }
}
