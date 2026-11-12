using System;
using System.Text;

namespace UntamedManager.Core;

/// <summary>
/// Classifies real Windows ASA command-line switches, never substrings of
/// quoted SessionName, custom map levels, paths or mod names. Fresh-map
/// preflight AND the final Core Process.Start boundary must agree: the UI
/// must not approve a Start that the native executor denies for the word
/// "Store" in an ordinary server name.
/// </summary>
public static class AsaStoreArgumentSwitches
{
    // This token walker deliberately removes Windows grouping quotes before
    // checking the beginning of an argument. A quoted "-usestore" is still
    // a real switch after ProcessStart argument parsing. A quoted session
    // value inside ?SessionName=... is not.
    private static bool Contains(string? arguments,params string[] switches)
    {
        if(string.IsNullOrEmpty(arguments))return false;
        bool quoted=false;
        var token=new StringBuilder();
        bool Matched()
        {
            if(token.Length==0)return false;
            string value=token.ToString();
            foreach(string item in switches)
                if(value.Equals("-"+item,StringComparison.OrdinalIgnoreCase)||
                   value.StartsWith("-"+item+"=",StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        foreach(char c in arguments)
        {
            if(c=='"'){quoted=!quoted;continue;}
            if(!quoted&&char.IsWhiteSpace(c))
            {
                if(Matched())return true;
                token.Clear();
            }
            else token.Append(c);
        }
        // An unclosed quoted argument is unsafe to pass through the native
        // boundary even if no forbidden flag could be classified reliably.
        return quoted||Matched();
    }

    public static bool ContainsStoreWriter(string? arguments)=>
        Contains(arguments,"usestore");

    public static bool ContainsConversion(string? arguments)=>
        Contains(arguments,"converttostore","convertfromstore");

    public static bool ContainsProtectedSwitch(string? arguments)=>
        Contains(arguments,"usestore","converttostore","convertfromstore",
            "clusterid","clusterdirOverride");
}
