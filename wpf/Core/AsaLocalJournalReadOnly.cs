using System;
using System.IO;
using System.Security;
using System.Text.Json;

namespace UntamedManager.Core;

/// <summary>
/// Read-only, non-authorizing status of both installation-scoped durable
/// journals. Missing is different from unreadable: only the strict native
/// mutation preflights can authorize Start/Stop/Restart/SaveWorld.
/// </summary>
public sealed record AsaLocalJournalReadOnlySnapshot(
    UntamedOperationRecord? Operation,AsaRestartRecord? Restart,
    bool OperationUnreadable,bool RestartUnreadable,
    string? OperationErrorCode,string? RestartErrorCode)
{
    public bool AnyUnreadable=>OperationUnreadable||RestartUnreadable;
}

public static class AsaLocalJournalReadOnly
{
    private static string ErrorCode(Exception ex,bool restart)
    {
        string prefix=restart?"UM-ASA-RESTART-JOURNAL-READ":
            "UM-OPERATION-JOURNAL-READ";
        return ex switch {
            UnauthorizedAccessException or SecurityException=>prefix+"-ACCESS-DENIED",
            IOException=>prefix+"-IO-UNAVAILABLE",
            _=>prefix+"-INVALID"
        };
    }

    public static AsaLocalJournalReadOnlySnapshot Inspect(
        UntamedOperationCoordinator operations,string installation)
    {
        ArgumentNullException.ThrowIfNull(operations);
        UntamedOperationRecord? operation=null;
        AsaRestartRecord? restart=null;
        string? operationError=null,restartError=null;
        try { operation=operations.Observe(installation); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or SecurityException)
        { operationError=ErrorCode(ex,false); }
        try { restart=new AsaManualRestartJournal(installation).Observe(); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or SecurityException)
        { restartError=ErrorCode(ex,true); }
        return new(operation,restart,operationError!=null,restartError!=null,
            operationError,restartError);
    }
}
