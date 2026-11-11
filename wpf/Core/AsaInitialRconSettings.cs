using System;
using System.IO;
using System.Linq;
using System.Text;

namespace UntamedManager.Core;

/// <summary>
/// One-time RCON bootstrap for a freshly SteamCMD-installed dedicated server.
/// Writes exactly one new GameUserSettings.ini in the official WindowsServer
/// location. It must NEVER import, merge, overwrite or normalize an existing
/// INI, saved world, tribe, player, Store-v1 or ArkAPI configuration.
/// </summary>
public static class AsaInitialRconSettings
{
    public static string NewIniPath(string installRoot)
    {
        if(string.IsNullOrWhiteSpace(installRoot) ||
           !Path.IsPathFullyQualified(installRoot) ||
           installRoot.StartsWith(@"\\",StringComparison.Ordinal) ||
           installRoot.Contains('~'))
            throw new InvalidDataException("ASA RCON bootstrap requires a fixed local install root");
        string root=Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("ASA RCON bootstrap cannot access network or removable disks");
        for(DirectoryInfo? dir=new(root);dir!=null;dir=dir.Parent)
            if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("ASA RCON bootstrap refuses linked folders");
        string exe=Path.Combine(root,"ShooterGame","Binaries","Win64","ArkAscendedServer.exe");
        if(!File.Exists(exe) || (File.GetAttributes(exe)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Fresh ASA installation executable is absent");
        return Path.Combine(root,"ShooterGame","Saved","Config",
            "WindowsServer","GameUserSettings.ini");
    }

    public static void ValidateAdminPassword(string password)
    {
        if(password.Length is <12 or >128 ||
           password.Any(c=>char.IsControl(c) || c is '=' or ';' or '[' or ']' or '"' or '\''))
            throw new InvalidDataException(
                "ASA RCON admin password must be 12–128 characters without INI metacharacters");
    }

    /// <summary>
    /// Existing ASA ServerAdminPassword values are NOT newly generated
    /// credentials. A historical Kova/ASA server may already use a short
    /// password or INI metacharacters that are valid after the first '='.
    /// Only reject values that cannot represent a single INI line; the
    /// read-only original-INI evidence must then match the exact credential
    /// before the Manager stores anything using DPAPI.
    /// </summary>
    public static void ValidateExistingAdminPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if(password.Length is <1 or >256 ||
           password.Any(char.IsControl))
            throw new InvalidDataException(
                "Existing ASA RCON password must be one nonempty INI line (max 256 characters)");
    }

    /// <summary>
    /// The official ASA GameUserSettings.ini requires an admin password in
    /// plaintext for RCON authentication. Never include it in logs, launch
    /// arguments, status JSON or a shared config. The Manager separately
    /// protects its own copy using Windows DPAPI.
    /// </summary>
    public static string CreateForNewServer(string installRoot,int rconPort,
        string adminPassword)
    {
        if(rconPort is <1 or >65535)
            throw new InvalidDataException("Invalid ASA RCON TCP port");
        ArgumentNullException.ThrowIfNull(adminPassword);
        ValidateAdminPassword(adminPassword);
        string path=NewIniPath(installRoot);
        string directory=Path.GetDirectoryName(path)!;
        if(Directory.Exists(directory) &&
           (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked WindowsServer config folder refused");
        // An imported or previously started server can already have this file.
        // Never touch it, even if it has no [ServerSettings] section.
        if(File.Exists(path) || Directory.Exists(path))
            throw new IOException("ASA configuration already exists; do not replace an imported server INI");
        Directory.CreateDirectory(directory);
        // Verify newly created intermediate directories were not replaced with
        // a junction after installation. Fail rather than following an alias.
        for(DirectoryInfo? node=new(directory);node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked config folder refused");
        string content="[ServerSettings]\r\n"+
            "RCONEnabled=True\r\n"+
            "RCONPort="+rconPort.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\r\n"+
            "ServerAdminPassword="+adminPassword+"\r\n";
        // Durable create-new: a concurrent creator cannot be overwritten.
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        using(var writer=new StreamWriter(file,new UTF8Encoding(false),1024,leaveOpen:true))
        {
            writer.Write(content);
            writer.Flush();
            file.Flush(true);
        }
        return path;
    }
}
