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
