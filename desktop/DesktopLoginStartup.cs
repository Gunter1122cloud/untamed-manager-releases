using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace UntamedDesktop;

/// <summary>
/// Per-user, explicit Windows login opt-in for the normal Manager shell.
/// No elevation, service, scheduled task, ASA autostart or Store access.
/// The Run value belongs to exactly one installation; never overwrite a
/// different executable path or remove an unrecognized value.
/// </summary>
internal static class DesktopLoginStartup
{
    private const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name="ObsidianCommandLogin";
    internal enum State { Disabled, Enabled, OtherInstallation }
    internal enum Action { NoChange, WriteOwn, DeleteOwn }

    internal static string CommandFor(string executable)
    {
        if(string.IsNullOrWhiteSpace(executable)||!Path.IsPathFullyQualified(executable)||
           executable.StartsWith(@"\\",StringComparison.Ordinal)||
           executable.StartsWith("//",StringComparison.Ordinal)||
           executable.Any(c=>char.IsControl(c)||c=='"'||c=='%')||
           !string.Equals(Path.GetFileName(executable),"UntamedDesktop.exe",
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UM-LOGIN-STARTUP-PATH");
        string path=Path.GetFullPath(executable);
        string command="\""+path+"\"";
        if(command.Length>260)
            throw new InvalidDataException("UM-LOGIN-STARTUP-PATH-LENGTH");
        return command;
    }

    internal static State Classify(object? existing,string ownCommand)=>
        existing==null?State.Disabled:
        existing is string text&&string.Equals(text,ownCommand,StringComparison.OrdinalIgnoreCase)?
            State.Enabled:State.OtherInstallation;

    internal static Action Plan(object? existing,string ownCommand,bool enable)
    {
        var state=Classify(existing,ownCommand);
        if(state==State.OtherInstallation)
            throw new InvalidOperationException("UM-LOGIN-STARTUP-OTHER-INSTALLATION");
        return enable?(state==State.Enabled?Action.NoChange:Action.WriteOwn):
            (state==State.Enabled?Action.DeleteOwn:Action.NoChange);
    }

    private static string CurrentCommand()
    {
        string executable=Environment.ProcessPath??throw new IOException("UM-LOGIN-STARTUP-PATH");
        string command=CommandFor(executable);
        if(!File.Exists(executable)||
           new DriveInfo(Path.GetPathRoot(executable)!).DriveType!=DriveType.Fixed)
            throw new IOException("UM-LOGIN-STARTUP-PATH");
        return command;
    }

    private static object? ReadValue(RegistryKey? key)=>
        key?.GetValue(Name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);

    internal static State Read()
    {
        string own=CurrentCommand();
        using var key=Registry.CurrentUser.OpenSubKey(Key);
        return Classify(ReadValue(key),own);
    }

    internal static void Set(bool enable)
    {
        string own=CurrentCommand();
        // Open writable only for the actual explicit toggle.
        using var key=enable?Registry.CurrentUser.CreateSubKey(Key,true):
            Registry.CurrentUser.OpenSubKey(Key,true);
        if(key==null)
        {
            if(enable)throw new IOException("UM-LOGIN-STARTUP-REGISTRY");
            return;
        }
        switch(Plan(ReadValue(key),own,enable))
        {
            case Action.WriteOwn: key.SetValue(Name,own,RegistryValueKind.String);break;
            case Action.DeleteOwn: key.DeleteValue(Name,false);break;
        }
        if(Classify(ReadValue(key),own)!=(enable?State.Enabled:State.Disabled))
            throw new IOException("UM-LOGIN-STARTUP-VERIFY");
    }
}
