using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

// Shared native SteamCMD contract; no raw provider output is published.
internal static class SteamCmdInvocation
{
    internal static string[] UpdateArguments(string install)=>new[]{
        // Non-interactive update switches from ChaosQK core/steamcmd.py
        // (MIT, cb9e50a2393fdf939e231df87b0d9d11e1da5268, lines 116–117)
        // and Ch4r0ne steamcmd_app_update (MIT, 1de9ed6d, lines 1591–1595).
        "+@ShutdownOnFailedCommand","1","+@NoPromptForPassword","1",
        "+force_install_dir",install,"+login","anonymous","+app_update","2430930","validate","+quit"};
    internal static void Configure(ProcessStartInfo psi,IEnumerable<string> arguments,bool synthetic=false)
    {
        if(!synthetic)
        {
            psi.ArgumentList.Add("-language");psi.ArgumentList.Add("english");
            psi.Environment["LANG"]="en_US.UTF-8";
            psi.Environment["LC_ALL"]="en_US.UTF-8";
            psi.Environment["LANGUAGE"]="en_US:en";
            psi.Environment["STEAMCMD_OUTPUT_BUFFERS"]="0";
        }
        foreach(string arg in arguments)psi.ArgumentList.Add(arg);
        foreach(string key in new[]{"STEAM_USERNAME","STEAM_PASSWORD","MYSQL_PWD","MARIADB_PWD","UNTAMED_RCON_PASSWORD"})
            psi.Environment.Remove(key);
    }
    private static bool Has(string output,params string[] markers)=>
        markers.Any(m=>output.Contains(m,StringComparison.OrdinalIgnoreCase));
    private static bool Permanent(string output)=>Has(output,"disk write failure","disk full",
        "not enough disk space","not enough free disk space","insufficient disk space","access denied",
        "permission denied","invalid password","corrupt update files","content file locked",
        "missing file privileges","invalid platform");
    private static bool KnownTransient(string output)=>!Permanent(output)&&
        Regex.IsMatch(output,@"Failed to (?:install|update) app\s*'2430930'\s*\((?:Missing configuration|No connection|Connection timeout|Timeout|Timed out|Content servers unavailable)\)",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(250));
    internal static bool CanRetryAppUpdate(int attempt,int exitCode,string output,bool synthetic)=>
        !synthetic&&attempt is >=0 and <2&&exitCode!=0&&KnownTransient(output);
    internal static string UpdateFailureCode(string output)
    {
        if(Has(output,"disk write failure","disk full","not enough disk space","not enough free disk space","insufficient disk space"))
            return "UM-STEAM-UPDATE-DISK";
        if(Has(output,"access denied","permission denied","missing file privileges","content file locked"))
            return "UM-STEAM-UPDATE-ACCESS";
        if(KnownTransient(output))return "UM-STEAM-UPDATE-TRANSIENT";
        return "UM-STEAM-UPDATE-NOT-ACKNOWLEDGED";
    }
    internal static bool CanUseDirectFallback(Exception ex)=>
        ex is IOException&&ex.Message=="UM-STEAM-CACHE-INSUFFICIENT-STAGING-SPACE";
}
