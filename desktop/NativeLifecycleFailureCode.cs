using System;
using System.IO;
using System.Linq;

namespace UntamedDesktop;

/// <summary>
/// Bounded, nonsecret lifecycle failure code for the WebView status view.
/// Never expose raw exception messages (paths, credentials or Store metadata).
/// An ASA process failure needs a stable actionable code, not just "unknown".
/// </summary>
internal static class NativeLifecycleFailureCode
{
    public static string From(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        for(Exception? item=error;item!=null;item=item.InnerException)
        {
            var value=item.Message;
            if(value.Length is >=5 and <=160 &&
               value.StartsWith("UM-",StringComparison.Ordinal) &&
               value.All(ch=>char.IsAsciiLetterUpper(ch)||char.IsAsciiDigit(ch)||
                   ch is '-' or '_' or ':'))
                return value;
        }
        return error switch {
            OperationCanceledException=>"UM-NATIVE-LIFECYCLE-TIMEOUT",
            UnauthorizedAccessException=>"UM-NATIVE-LIFECYCLE-ACCESS-DENIED",
            InvalidDataException=>"UM-NATIVE-LIFECYCLE-EVIDENCE-INVALID",
            IOException=>"UM-NATIVE-LIFECYCLE-IO-FAILED",
            _=>"UM-NATIVE-LIFECYCLE-FAILED"
        };
    }
}
