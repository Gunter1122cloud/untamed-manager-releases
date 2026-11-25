using System;
using System.IO;

namespace UntamedManager.Core;

/// <summary>
/// Exact-layout, read-only ASA install probe. Does not enumerate drives,
/// launch binaries, scan the player's saves or modify installation files.
/// A user must explicitly choose a local installation root.
/// </summary>
public sealed record LocalAsaInstallation(string Root,string? ServerExecutable,
    string? ApiLoaderExecutable)
{
    public bool HasServerExecutable => ServerExecutable is not null;
    public bool HasApiLoader => ApiLoaderExecutable is not null;
}

public static class LocalAsaInstallLocator
{
    // Diagnostic-only classification; executable existence is NOT launch readiness.
    // Caller must first validate the fixed local root and refuse redirected paths.
    public static string? DiagnoseImport(string root)
    {
        try
        {
            string server=Path.Combine(root,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
            try
            {
                var attributes=File.GetAttributes(server);
                if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)
                    return "UM-ASA-EXE-INVALID";
                return new FileInfo(server).Length>0?null:"UM-ASA-EXE-EMPTY";
            }
            catch(FileNotFoundException){}
            catch(DirectoryNotFoundException){}
            using(var entries=Directory.EnumerateFileSystemEntries(root).GetEnumerator())
                if(!entries.MoveNext())return "UM-ASA-INSTALL-EMPTY";
            foreach(string marker in new[]{"ShooterGame","Engine","steamapps"})
            {
                try
                {
                    var attributes=File.GetAttributes(Path.Combine(root,marker));
                    if((attributes&FileAttributes.ReparsePoint)!=0)
                        return "UM-ASA-INSTALL-FOREIGN";
                    if((attributes&FileAttributes.Directory)!=0)
                        return "UM-ASA-INSTALL-INCOMPLETE";
                }
                catch(FileNotFoundException){}
                catch(DirectoryNotFoundException){}
            }
            return "UM-ASA-INSTALL-FOREIGN";
        }
        catch(UnauthorizedAccessException){return "UM-ASA-INSTALL-INACCESSIBLE";}
        catch(DirectoryNotFoundException){return "UM-IMPORT-DIRECTORY-MISSING";}
        catch(IOException){return "UM-ASA-INSTALL-UNKNOWN";}
    }

    public static LocalAsaInstallation Probe(string chosenRoot)
    {
        if(string.IsNullOrWhiteSpace(chosenRoot))
            throw new InvalidDataException("Az ASA telepítési könyvtár nincs megadva.");
        string root=Path.GetFullPath(chosenRoot.Trim().Trim('"'));
        if(!Directory.Exists(root))
            throw new DirectoryNotFoundException("Az ASA telepítési könyvtár nem található.");
        string directory=Path.Combine(root,"ShooterGame","Binaries","Win64");
        string server=Path.Combine(directory,"ArkAscendedServer.exe");
        string loader=Path.Combine(directory,"AsaApiLoader.exe");
        return new LocalAsaInstallation(root,File.Exists(server)?server:null,
            File.Exists(loader)?loader:null);
    }
}
