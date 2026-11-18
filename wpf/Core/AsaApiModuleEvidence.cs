using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace UntamedManager.Core;

/// <summary>
/// Read-only OS module evidence equivalent to the original agent's
/// child-process DLL inspection. This is not a plugin installer or loader.
/// A loader EXE, ArkApi.log line or DLL merely present on disk is insufficient.
/// </summary>
internal static class AsaApiModuleEvidence
{
    // A snapshot is valid only for the exact current, independently observed
    // ASA child. A missing module is not a diagnosis of failed plugin hooks.
    internal static HashSet<string>? ObserveLoadedPaths(LocalMap map,ProcessIdentity child)
    {
        if(!OperatingSystem.IsWindows()||map.ObservedReadOnlyImported||
           child.Pid<=0||child.StartedUtc==default||
           string.IsNullOrWhiteSpace(map.InstallationPath))return null;
        try
        {
            string expected=Path.GetFullPath(Path.Combine(map.InstallationPath,
                "ShooterGame","Binaries","Win64","ArkAscendedServer.exe"));
            if(!string.Equals(Path.GetFullPath(child.ExecutablePath),expected,
                StringComparison.OrdinalIgnoreCase))return null;
            using var process=Process.GetProcessById(child.Pid);
            bool OwnCurrent()=>!process.HasExited&&
                process.StartTime.ToUniversalTime()==child.StartedUtc.UtcDateTime&&
                string.Equals(Path.GetFullPath(process.MainModule!.FileName),expected,
                    StringComparison.OrdinalIgnoreCase);
            if(!OwnCurrent())return null;
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(ProcessModule module in process.Modules)
                paths.Add(Path.GetFullPath(module.FileName));
            return OwnCurrent()?paths:null;
        }
        catch(Exception ex) when(ex is InvalidOperationException or Win32Exception or
            IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or NullReferenceException or System.Security.SecurityException)
        {return null;}
    }

    internal static bool IsLoaded(LocalMap map,ProcessIdentity child)
    {
        if(!OperatingSystem.IsWindows() || map==null || child==null ||
           !map.StartWithAsaApi || child.Pid<=0 ||
           child.StartedUtc==default ||
           string.IsNullOrWhiteSpace(map.InstallationPath))
            return false;
        try
        {
            string root=Path.GetFullPath(map.InstallationPath);
            string bin=Path.Combine(root,"ShooterGame","Binaries","Win64");
            string expectedServer=Path.Combine(bin,"ArkAscendedServer.exe");
            string expectedModule=Path.Combine(bin,"ArkApi","AsaApi.dll");
            if(!string.Equals(Path.GetFullPath(child.ExecutablePath),
                    expectedServer,StringComparison.OrdinalIgnoreCase) ||
               !File.Exists(expectedModule) ||
               (File.GetAttributes(expectedModule)&FileAttributes.ReparsePoint)!=0)
                return false;
            for(DirectoryInfo? dir=new(Path.GetDirectoryName(expectedModule)!);
                dir!=null;dir=dir.Parent)
                if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                    return false;
            using var process=Process.GetProcessById(child.Pid);
            if(process.HasExited || process.StartTime.ToUniversalTime()!=
               child.StartedUtc.UtcDateTime)
                return false;
            bool found=false;
            foreach(ProcessModule loaded in process.Modules)
            {
                if(string.Equals(Path.GetFullPath(loaded.FileName),
                       expectedModule,StringComparison.OrdinalIgnoreCase))
                {
                    found=true;
                    break;
                }
            }
            return found && !process.HasExited &&
                process.StartTime.ToUniversalTime()==child.StartedUtc.UtcDateTime;
        }
        catch(Exception ex) when(ex is InvalidOperationException or
            Win32Exception or IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            // Access denied / process exit / module enumeration failure is
            // unverified, never "loaded". No process state is mutated.
            return false;
        }
    }
}
