using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// The release-candidate UI must NEVER turn a simulated proof into permission
// to mutate real ASA, LegacyCluster or MariaDB instances.
public enum ManagerOperation
{
    LocalProcessObservation, LocalReadOnlyRcon, LiveWorldSave, OfflineConfiguration,
    OfflineBackup, OfflineSqlArchive, OfflineStoreInventory,
    FakeAsaStart, FakeAsaRecoverySimulation, SteamCmdDryRun,
    RealAsaStart, RealAsaStop, RealAsaRestart, RealAsaKill,
    LiveSteamUpdate, LiveSqlRestore, LiveVaultStoreWrite
}
public sealed record OperationReadiness(ManagerOperation Operation,bool Allowed,string Reason);
public static class ReleaseCapabilityGate
{
    public static OperationReadiness Check(ManagerOperation operation,bool owned,
        bool maintenance=false,bool confirmedStopped=false,bool running=false,
        bool realSaveWorldProof=false,bool storeVerified=false,
        bool verifiedFreshInstall=false,bool explicitOperatorApproval=false)
    {
        if(!owned)return new(operation,false,"Not a registered local map/profile");
        switch(operation)
        {
            case ManagerOperation.LocalProcessObservation:
            case ManagerOperation.OfflineConfiguration:
            case ManagerOperation.OfflineStoreInventory:
            case ManagerOperation.OfflineBackup:
            case ManagerOperation.OfflineSqlArchive:
            case ManagerOperation.FakeAsaRecoverySimulation:
            case ManagerOperation.SteamCmdDryRun:
                return new(operation,true,"Local-only or synthetic test feature");
            case ManagerOperation.LocalReadOnlyRcon:
                return new(operation,running && !maintenance,
                    running && !maintenance?"Read-only ListPlayers on verified local map":"Not verified running or in maintenance");
            case ManagerOperation.LiveWorldSave:
                // A fixed SaveWorld command does not confer Start/Stop/Kill,
                // arbitrary RCON or Store writer privileges. The native
                // caller must also verify exact PID/start and RCON listener owner.
                return new(operation,running && !maintenance && !confirmedStopped,
                    running && !maintenance && !confirmedStopped?
                    "Explicit SaveWorld on exact owned local process":
                    "Local owned running server required for explicit SaveWorld");
            case ManagerOperation.RealAsaRestart:
                // A new non-Store ASA host can use the explicit one-shot
                // SaveWorld/DoExit -> confirmed exit -> Start sequence.
                // A missing independently witnessed SaveComplete is disclosed;
                // no automatic retry, kill or shared character write allowed.
                return new(operation,verifiedFreshInstall &&
                    explicitOperatorApproval && running &&
                    !confirmedStopped && !maintenance,
                    "Fresh local restart requires explicit approval and graceful exit");
            case ManagerOperation.RealAsaStop:
                // Operator-authorized, non-Store DoExit is implemented by
                // AsaGracefulStop, which binds every RCON write to an exact
                // Windows process identity and local port listener. No
                // readiness or independently witnessed SaveComplete is implied.
                return new(operation,verifiedFreshInstall &&
                    explicitOperatorApproval && running &&
                    !confirmedStopped && !maintenance,
                    "Local non-Store ASA graceful DoExit requires exact process ownership");
            case ManagerOperation.RealAsaStart:
                // The only authorized real start in this stage is a newly
                // installed, non-Store ASA map after an explicit UI approval.
                // The caller independently validates the official Steam files,
                // all launch arguments, ports and persistent operation lock.
                return new(operation,
                    verifiedFreshInstall && explicitOperatorApproval &&
                    confirmedStopped && !running && !maintenance,
                    "Fresh-install local ASA start requires verified Steam files, explicit approval and stopped process");
            case ManagerOperation.FakeAsaStart:
                return new(operation,!maintenance && confirmedStopped && !running,
                    !maintenance && confirmedStopped && !running?
                    "Verified stopped; FakeAsa test binary only":"Uncertain, running or maintenance");
            default:
                // No combination of unverified real-save proofs or UI toggles
                // is allowed to lift these production gates in v1.4.0 RC.
                return new(operation,false,"Production lifecycle/Store/SQL compatibility not certified");
        }
    }
    public static void DemandFakeAsaStart(ClusterService cluster,LocalMap map)
    {
        if(cluster==null||map==null||map.Id==Guid.Empty||
           !cluster.Config.LocalMaps.Contains(map))
            throw new UnauthorizedAccessException("Only registered local test map permitted");
        if((map.Arguments+" "+map.ApiLoaderArguments).Contains("store",
           StringComparison.OrdinalIgnoreCase) || map.Maintenance)
            throw new InvalidOperationException("Store or maintenance is not allowed in FakeAsa test launch");
        string executable=Path.GetFullPath(map.ExecutablePath);
        if(!Path.GetFileName(executable).Equals("FakeAsa.exe",StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable) || executable.StartsWith(@"\\"))
            throw new InvalidOperationException("Only a local FakeAsa.exe test process can be started");
        if(map.StartWithAsaApi)
            throw new InvalidOperationException("ASA API mode cannot be executed through the FakeAsa test gate");
        var check=cluster.Check(map);
        if(!ReleaseCapabilityGate.Check(ManagerOperation.FakeAsaStart,true,
            map.Maintenance,check.ConfirmedStopped,check.Running).Allowed)
            throw new InvalidOperationException("FakeAsa process state is not verified stopped");
    }
}

