using System;
using System.Collections.Generic;
using System.Linq;

namespace UntamedManager.Core;

/// <summary>
/// Pure, side-effect-free reconstruction of the shared Store-v1 start guard
/// from the operator's original two-peer source. This does NOT own locks,
/// mutate Store JSON, start ASA or certify a live shared filesystem.
/// A 'ready' decision is ONLY a historical protocol-shape observation.
/// </summary>
public static class LegacyStoreV1PhaseGate
{
    public sealed record Evidence(
        string MapPhase,
        string LocalPeerPhase,
        string OtherPeerPhase,
        bool LocalLockPresent,
        bool LocalLockMatchesSession,
        bool PeerSessionsMatch,
        bool ConfigurationHashesMatch,
        bool MapIdentityAndRootMatch,
        bool SelectedByLocalPeer,
        bool RunningMarkerPresent,
        bool MutationLockPresent,
        bool CancelledMarkerPresent,
        bool AttemptedMarkerPresent);

    public sealed record Decision(
        bool HistoricalNormalStartShape,
        bool MaintenanceLocked,
        bool AutoRecoveryAllowed,
        bool ConvertToStoreAllowed,
        bool ConvertFromStoreAllowed,
        bool LiveWriterCertified,
        string[] Blockers);

    public static Decision Evaluate(Evidence e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var blocks=new List<string>();
        if(e.MapPhase!="active")blocks.Add("STORE_MAP_NOT_ACTIVE");
        if(!e.LocalLockPresent)blocks.Add("STORE_LOCAL_LOCK_MISSING");
        if(!e.LocalLockMatchesSession)blocks.Add("STORE_LOCAL_LOCK_SESSION_MISMATCH");
        if(!e.PeerSessionsMatch)blocks.Add("STORE_PEER_SESSION_MISMATCH");
        if(!e.ConfigurationHashesMatch)blocks.Add("STORE_CONFIG_HASH_MISMATCH");
        if(!e.MapIdentityAndRootMatch)blocks.Add("STORE_MAP_BINDING_MISMATCH");
        if(!e.SelectedByLocalPeer)blocks.Add("STORE_MAP_NOT_SELECTED");
        if(e.LocalPeerPhase!="done"||e.OtherPeerPhase!="done")
            blocks.Add("STORE_BOTH_PEERS_NOT_DONE");
        if(e.RunningMarkerPresent)blocks.Add("STORE_RUNNING_MARKER");
        if(e.MutationLockPresent)blocks.Add("STORE_DURABLE_MUTATION_LOCK");
        if(e.CancelledMarkerPresent)blocks.Add("STORE_CANCELLED_MARKER");
        // Source's attempted marker is expected after a completed migration,
        // not a sign that active maps need re-conversion. Absence, however,
        // makes the historical completion evidence incomplete.
        if(!e.AttemptedMarkerPresent)blocks.Add("STORE_ATTEMPTED_MARKER_MISSING");
        bool normal=blocks.Count==0;
        return new(normal,!normal,
            AutoRecoveryAllowed:false,ConvertToStoreAllowed:false,
            ConvertFromStoreAllowed:false,LiveWriterCertified:false,
            Blockers:blocks.ToArray());
    }

    /// <summary>
    /// Normal active Start is an observation, never a migration transition.
    /// All prepared/review states remain blocked until controlled verification.
    /// The original migration lock is DURABLE; no TTL or force-release exists.
    /// </summary>
    public static bool IsRecognizedPhase(string? value)=>
        value is "frozen" or "backed" or "prepared" or "review" or
            "active" or "done" or "cancel" or "released";
}
