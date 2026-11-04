using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// The release-candidate UI must NEVER turn a simulated proof into permission
// to mutate real ASA, KovaCluster or MariaDB instances.
public enum ManagerOperation
{
    LocalProcessObservation, LocalReadOnlyRcon, OfflineConfiguration,
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
        bool realSaveWorldProof=false,bool storeVerified=false)
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
        if(!string.IsNullOrWhiteSpace(map.ApiLoaderPath) &&
           !Path.GetFileName(map.ApiLoaderPath).Equals("FakeAsa.exe",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("API loader is not the test binary");
        var check=cluster.Check(map);
        if(!ReleaseCapabilityGate.Check(ManagerOperation.FakeAsaStart,true,
            map.Maintenance,check.ConfirmedStopped,check.Running).Allowed)
            throw new InvalidOperationException("FakeAsa process state is not verified stopped");
    }
}

