using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

/// <summary>
/// Read-only evidence that a fresh ASA server is manageable after Start:
/// the local Manager RCON port and DPAPI credential match exactly one active
/// [ServerSettings] value in the actual WindowsServer GUS.ini. Never writes the
/// file, returns the plaintext credential, or follows reparse-point aliases.
/// This is not proof that the future TCP RCON listener is already live.
/// </summary>
public static class AsaRconConfigurationEvidence
{
    public static bool Matches(LocalMap map)
    {
        if(map==null||map.RconHost!="127.0.0.1"||
           map.RconPort is <1024 or >65535||
           string.IsNullOrWhiteSpace(map.RconPasswordProtected)||
           !AsaReadyProbe.IsRegisteredServerBinary(map))
            return false;
        try
        {
            string path=AsaInitialRconSettings.NewIniPath(map.InstallationPath);
            string dir=Path.GetDirectoryName(path)!;
            for(DirectoryInfo? node=new(dir);node!=null;node=node.Parent)
                if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                    return false;
            var ini=new FileInfo(path);
            if(!ini.Exists||ini.Length is <8 or >131072 ||
               (ini.Attributes&FileAttributes.ReparsePoint)!=0)
                return false;
            string password=SecureStore.Unprotect(map.RconPasswordProtected);
            AsaInitialRconSettings.ValidateAdminPassword(password);
            int sections=0,enabled=0,ports=0,secrets=0;
            bool inside=false,matched=true;
            foreach(var source in File.ReadLines(path))
            {
                string text=source.Trim();
                if(text.StartsWith("[",StringComparison.Ordinal))
                {
                    inside=text.Equals("[ServerSettings]",StringComparison.OrdinalIgnoreCase);
                    if(inside)sections++;
                    continue;
                }
                if(!inside || text.Length==0 || text[0] is ';' or '#')
                    continue;
                int equals=text.IndexOf('=');
                if(equals<1)continue;
                string key=text[..equals].Trim();
                string value=text[(equals+1)..].Trim();
                if(key.Equals("RCONEnabled",StringComparison.OrdinalIgnoreCase))
                {
                    enabled++;
                    matched &= value.Equals("True",StringComparison.OrdinalIgnoreCase);
                }
                else if(key.Equals("RCONPort",StringComparison.OrdinalIgnoreCase))
                {
                    ports++;
                    matched &= value.Equals(
                        map.RconPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal);
                }
                else if(key.Equals("ServerAdminPassword",StringComparison.OrdinalIgnoreCase))
                {
                    secrets++;
                    byte[] expected=Encoding.UTF8.GetBytes(password);
                    byte[] actual=Encoding.UTF8.GetBytes(value);
                    matched &= actual.Length==expected.Length &&
                        CryptographicOperations.FixedTimeEquals(actual,expected);
                }
            }
            return matched && sections==1 && enabled==1 && ports==1 && secrets==1;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or
            CryptographicException or NotSupportedException)
        {
            return false;
        }
    }
}
