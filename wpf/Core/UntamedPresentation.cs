using System;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

public static class UntamedPresentation
{
    public static string Display(string? text)=>(text??"")
        .Replace(VaultProtocol.ClusterId,"Untamed Cluster Vault",StringComparison.OrdinalIgnoreCase)
        .Replace(VaultProtocol.ProtectedNameToken,"Untamed Manager",StringComparison.OrdinalIgnoreCase);
    // Never echo an arbitrary Exception.Message in an unhandled-error popup.
    // Exceptions can contain access tokens, SQL credentials and private paths.
    public static string UnexpectedFailure(Exception exception)
    {
        if(exception==null)throw new ArgumentNullException(nameof(exception));
        return "Váratlan alkalmazáshiba (UM-UNHANDLED). " +
            "Részletek a helyi, titokszűrt crash.log fájlban. " +
            "A folyamatban lévő művelet állapotát ellenőrizd újrapróbálkozás előtt.";
    }
    public static string Redact(string? text)
    {
        string safe=Display(text);
        safe=Regex.Replace(safe,@"(?i)(ServerAdminPassword|ServerPassword|password|passwd|token|secret|apikey|api_key)([""']?\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s&?;,]+)","$1$2[REDACTED]");
        safe=Regex.Replace(safe,@"(?i)https://(?:discord(?:app)?\.com)/api/webhooks/[^\s]+","[REDACTED_WEBHOOK]");
        return safe;
    }
}
