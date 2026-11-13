using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using UntamedDesktop;
using UntamedManager.Core;

internal static class ClusterRegistryTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("CLUSTER REGISTRY TEST FAILED: " + message);
        Console.WriteLine("PASS cluster registry: " + message);
    }

    private static string SyntheticHostId(int index) =>
        Guid.Parse($"00000000-0000-4000-8000-{index:D12}").ToString("N");

    private static Guid SyntheticMapId(int index) =>
        Guid.Parse($"00000000-0000-4000-9000-{index:D12}");

    private static async Task ValidateOriginalLegacyMembershipIsImmutable(string testRoot)
    {
        // TEN original synthetic identities only. The registry checks run
        // against a disposable Manager profile; no ASA, Store or LAN access.
        string root=Path.Combine(testRoot,"original-legacy-registry-protection");
        await using var service=new ClusterService("protected-legacy-fixture",root,
            _=>new ProcessCheck(false,"Synthetic test only",null,true));
        string hp=service.Config.LocalHostId;
        string ryzen=SyntheticHostId(801);
        string third=SyntheticHostId(802);
        service.Registry.RegisterHost(ryzen,"Synthetic Ryzen");
        string[] names={"Astraeos","Genesis Part 1","Valguero","The Island",
            "Extinction","Scorched Earth","Lost Colony","Ragnarok",
            "The Center","Aberration"};
        var originals=new Guid[10];
        for(int i=0;i<10;i++)
        {
            originals[i]=SyntheticMapId(810+i);
            if(i<5)
                service.AddMap(new LocalMap{
                    Id=originals[i],Name=names[i],
                    HistoricalStoreProtected=true,
                    HistoricalArchiveSha256=new string('A',64)
                });
            else service.Registry.RegisterRemoteMap(ryzen,originals[i],names[i]);
        }
        var legacy=service.Registry.CreateCluster("\u004BovaCluster",
            "Original Untamed Kingdom",Path.Combine(root,"original-transfer"));
        var lab=service.Registry.CreateCluster("expansion-test",
            "Independent synthetic expansion",Path.Combine(root,"test-transfer"));
        // Simulate the ALREADY imported original assignments without going
        // through the normal user-facing registration API. Those assignments
        // are immutable historical facts, not newly authorized transfer.
        foreach(var mapId in originals)
            service.Config.ClusterRegistry.MapAssignments.Add(
                new ClusterMapAssignment{MapId=mapId,ClusterId=legacy.ClusterId});
        var initial=service.Registry.Snapshot();
        Check(initial.ClusterRegistry.MapAssignments.Count==10 &&
              initial.HostRegistry.Maps.Count==10 &&
              initial.HostRegistry.Hosts.Count==2,
            "original 5+5 host/map IDs have a single immutable historical cluster");
        var beforeProtectedEdit=service.Registry.Snapshot();
        bool pathRebindDenied=false;
        try
        {
            service.Registry.UpdateCluster(legacy.ClusterId,legacy.Name,
                Path.Combine(root,"unverified-replacement-transfer"));
        }
        catch(UnauthorizedAccessException){pathRebindDenied=true;}
        Check(pathRebindDenied,
            "native registry refuses changing original Legacy transfer directory");
        bool nameEditDenied=false;
        try
        {
            service.Registry.UpdateCluster(legacy.ClusterId,"Renamed Historical Cluster",
                legacy.TransferDirectory);
        }
        catch(UnauthorizedAccessException){nameEditDenied=true;}
        Check(nameEditDenied,
            "generic cluster editor cannot rename original Store-v1 cluster");
        var unchangedOriginal=service.Registry.Snapshot();
        Check(unchangedOriginal.Revision==beforeProtectedEdit.Revision &&
              unchangedOriginal.ClusterRegistry.Clusters.Single(x=>
                x.ClusterId==legacy.ClusterId).TransferDirectory==legacy.TransferDirectory,
            "rejected original cluster metadata changes preserve revision and transfer path");
        var independentChange=service.Registry.UpdateCluster(lab.ClusterId,
            "Synthetic Other Cluster Updated",lab.TransferDirectory);
        Check(independentChange.Name=="Synthetic Other Cluster Updated",
            "ordinary independent non-Store cluster metadata remains editable");

        string sentinel=Path.Combine(root,"untouched-original-store.bin");
        byte[] bytes={0,255,13,0,5,11,99,128};
        File.WriteAllBytes(sentinel,bytes);
        string originalSha=Convert.ToHexString(SHA256.HashData(bytes));

        var harmless=service.Registry.PreviewMapAssignments(
            legacy.ClusterId,originals);
        Check(harmless.IsValid&&harmless.Additions.Count==0&&
              harmless.Removals.Count==0&&harmless.Moves.Count==0,
            "unchanged original Legacy membership remains reviewable");
        var removal=service.Registry.PreviewMapAssignments(
            legacy.ClusterId,originals.Take(9));
        Check(!removal.IsValid&&removal.Errors.Contains(
              "UM-REGISTRY-ORIGINAL-STORE-MEMBERSHIP-IMMUTABLE"),
            "removal of an original Legacy map denied by native preview");
        var move=service.Registry.PreviewMapAssignments(
            lab.ClusterId,new[]{originals[0],originals[5]});
        Check(!move.IsValid&&move.Errors.Contains(
              "UM-REGISTRY-ORIGINAL-STORE-MEMBERSHIP-IMMUTABLE"),
            "local historical and remote Legacy member cannot move through generic API");
        bool applyDenied=false;
        try{service.Registry.ApplyMapAssignments(
            lab.ClusterId,new[]{originals[0]},move.BaseRevision);}
        catch(InvalidDataException){applyDenied=true;}
        Check(applyDenied,
            "direct native membership apply also rejects protected Legacy map");
        Guid extra=SyntheticMapId(820);
        service.Registry.RegisterHost(third,"Third synthetic host");
        service.Registry.RegisterRemoteMap(third,extra,"Future map");
        var unauthorized=service.Registry.PreviewMapAssignments(
            legacy.ClusterId,originals.Append(extra));
        Check(!unauthorized.IsValid&&unauthorized.Errors.Contains(
              "UM-REGISTRY-ORIGINAL-STORE-MEMBERSHIP-IMMUTABLE"),
            "generic registration never grants an unverified eleventh map Legacy Store membership");
        var ordinary=service.Registry.PreviewMapAssignments(
            lab.ClusterId,new[]{extra});
        Check(ordinary.IsValid&&ordinary.Additions.Count==1,
            "independent non-Legacy cluster remains extensible on a third host");
        service.Registry.ApplyMapAssignments(lab.ClusterId,new[]{extra},
            ordinary.BaseRevision);
        var after=service.Registry.Snapshot();
        Check(after.ClusterRegistry.MapAssignments.Count(x=>
                  x.ClusterId==legacy.ClusterId)==10 &&
              originals.All(id=>after.ClusterRegistry.MapAssignments
                  .Count(x=>x.MapId==id&&x.ClusterId==legacy.ClusterId)==1) &&
              after.HostRegistry.Hosts.Count==3 &&
              after.HostRegistry.Maps.Count==11 &&
              after.ClusterRegistry.MapAssignments.Any(x=>
                  x.MapId==extra&&x.ClusterId==lab.ClusterId),
            "third host and eleventh map do not change any original Store assignment");
        Check(File.ReadAllBytes(sentinel).SequenceEqual(bytes)&&
              Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sentinel)))==
                  originalSha,
            "all original synthetic Store bytes remain unchanged by registry operations");

        // The true cluster ID must be copied from historical Legacy metadata.
        // A differently named source-pinned cluster has exactly the same
        // fail-closed generic-edit and fresh-map protection as LegacyCluster.
        var sourceNamed=service.Registry.CreateCluster("source-import-id-42",
            "Historical source-provided ID",
            Path.Combine(root,"different-historical-transfer"));
        Guid sourceMember=SyntheticMapId(821);
        service.AddMap(new LocalMap{
            Id=sourceMember,Name="Synthetic Historical Other",
            HistoricalStoreProtected=true,
            HistoricalArchiveSha256=new string('B',64)
        });
        // As with the original 5+5 fixture, model an ALREADY imported
        // source-pinned assignment without permitting generic re-enrollment.
        service.Config.ClusterRegistry.MapAssignments.Add(new ClusterMapAssignment{
            MapId=sourceMember,ClusterId=sourceNamed.ClusterId});
        var sourceBaseline=service.Registry.Snapshot();
        var noChange=service.Registry.PreviewMapAssignments(
            sourceNamed.ClusterId,new[]{sourceMember});
        Check(noChange.IsValid&&noChange.Additions.Count==0,
            "source-named historical cluster remains readable without membership changes");
        var addToSource=service.Registry.PreviewMapAssignments(
            sourceNamed.ClusterId,new[]{sourceMember,extra});
        Check(!addToSource.IsValid&&addToSource.Errors.Contains(
              "UM-REGISTRY-ORIGINAL-STORE-MEMBERSHIP-IMMUTABLE"),
            "unverified future map cannot enter a historical Store cluster with non-Legacy ID");
        bool sourceApplyDenied=false;
        try{service.Registry.ApplyMapAssignments(
            sourceNamed.ClusterId,new[]{sourceMember,extra},addToSource.BaseRevision);}
        catch(InvalidDataException){sourceApplyDenied=true;}
        Check(sourceApplyDenied&&
              service.Registry.Snapshot().Revision==sourceBaseline.Revision,
            "source-named historical cluster rejects direct apply without registry mutation");
        bool sourceRebindDenied=false;
        try{service.Registry.UpdateCluster(sourceNamed.ClusterId,sourceNamed.Name,
            Path.Combine(root,"unauthorized-source-transfer-rebind"));}
        catch(UnauthorizedAccessException){sourceRebindDenied=true;}
        Check(sourceRebindDenied&&
              File.ReadAllBytes(sentinel).SequenceEqual(bytes),
            "source-named historical cluster transfer path and original Store bytes are protected");
    }

    private static void ValidateLargeSyntheticTopology(string testRoot)
    {
        const int hostCount = 300;
        const int clusterCount = 520;
        const int mapCount = 4100;
        string localHostId = SyntheticHostId(0);
        string scaleRoot = Path.Combine(testRoot, "registry-scale-metadata");
        var hostIds = Enumerable.Range(0, hostCount).Select(SyntheticHostId).ToArray();
        var config = new ManagerConfig
        {
            LocalHostId = localHostId,
            LocalHostName = "Synthetic scale host",
            HostRegistry = new HostRegistry
            {
                SchemaVersion = 1,
                LocalHostId = localHostId,
                Hosts = hostIds.Select((id, i) => new RegisteredHost
                {
                    HostId = id,
                    HostName = $"Synthetic host {i:D3}",
                    IsLocal = i == 0,
                    IsPaired = false
                }).ToList(),
                Maps = Enumerable.Range(0, mapCount).Select(i => new RegisteredMap
                {
                    MapId = SyntheticMapId(i),
                    HostId = hostIds[i % hostCount],
                    MapName = $"Synthetic map {i:D4}",
                    LocalConfigurationPresent = false
                }).ToList()
            },
            ClusterRegistry = new ClusterRegistry
            {
                SchemaVersion = 1,
                Revision = 1,
                Clusters = Enumerable.Range(0, clusterCount).Select(i => new RegisteredCluster
                {
                    ClusterId = $"scale-{i:D3}",
                    Name = $"Synthetic cluster {i:D3}",
                    TransferDirectory = Path.Combine(scaleRoot, $"cluster-{i:D3}", "transfer"),
                    BackupDirectory = Path.Combine(scaleRoot, $"cluster-{i:D3}", "backup")
                }).ToList(),
                MapAssignments = Enumerable.Range(0, mapCount).Select(i => new ClusterMapAssignment
                {
                    MapId = SyntheticMapId(i),
                    ClusterId = $"scale-{i % clusterCount:D3}"
                }).ToList()
            },
            LocalMaps = new()
        };

        // This is object-only synthetic data. The directories are deliberately
        // never created and no listener, port, process, ASA, or Store is used.
        ClusterRegistrySchema.Validate(config);
        Check(config.HostRegistry.Hosts.Count == hostCount &&
            config.ClusterRegistry.Clusters.Count == clusterCount &&
            config.HostRegistry.Maps.Count == mapCount &&
            config.ClusterRegistry.MapAssignments.Count == mapCount,
            "product registries accept 300 hosts, 520 clusters, and 4100 maps beyond all v1 archive batch limits");

        var cloned = ClusterRegistrySchema.Clone(config);
        ClusterRegistrySchema.Validate(cloned);
        Check(cloned.HostRegistry.Hosts.Count == hostCount &&
            cloned.ClusterRegistry.Clusters.Count == clusterCount &&
            cloned.HostRegistry.Maps.Count == mapCount &&
            cloned.ClusterRegistry.MapAssignments.Select(x => x.MapId).Distinct().Count() == mapCount,
            "large N-host registry survives the same JSON clone path used by Manager snapshots");
    }

    public static async Task Run(string testRoot)
    {
        string migrationRoot = Path.Combine(testRoot, "registry-migration");
        Directory.CreateDirectory(migrationRoot);
        string originalHost = Guid.Parse("00000000-0000-4000-8000-000000000101").ToString("N");
        Guid originalMap = Guid.Parse("00000000-0000-4000-8000-000000000102");
        string oldConfig = Path.Combine(migrationRoot, "cluster-config.json");
        File.WriteAllText(oldConfig, JsonSerializer.Serialize(new
        {
            LocalHostId = originalHost,
            LocalHostName = "Legacy Host",
            LocalMaps = new[] { new { Id = originalMap, Name = "Legacy Island" } }
        }));
        string storeFixture = Path.Combine(migrationRoot, "historic-store-v1-bytes.fixture");
        byte[] storeBytes = { 0, 1, 2, 0, 255, 64, 128, 9, 7, 6, 5 };
        File.WriteAllBytes(storeFixture, storeBytes);
        string storeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(storeFixture)));
        {
            await using var migrated = new ClusterService("registry-migration", migrationRoot,
                _ => new ProcessCheck(false, "Synthetic test only", null, true));
            var snapshot = migrated.Registry.Snapshot();
            Check(snapshot.HostRegistry.SchemaVersion == 1 && snapshot.ClusterRegistry.SchemaVersion == 1,
                "older Manager profile gains versioned host and cluster schemas");
            Check(snapshot.HostRegistry.LocalHostId == originalHost &&
                snapshot.HostRegistry.Maps.Single().MapId == originalMap &&
                snapshot.HostRegistry.Maps.Single().MapName == "Legacy Island",
                "profile migration preserves existing host and map IDs and names");
            var legacyCluster = migrated.Registry.CreateCluster("legacy-cluster", "Legacy Cluster",
                Path.Combine(migrationRoot, "legacy-transfer"));
            var assignment = migrated.Registry.PreviewMapAssignments(legacyCluster.ClusterId,
                new[] { originalMap });
            migrated.Registry.ApplyMapAssignments(legacyCluster.ClusterId,
                new[] { originalMap }, assignment.BaseRevision);
            migrated.RemoveMap(originalMap);
            var retired = migrated.Registry.Snapshot();
            Check(!retired.HostRegistry.Maps.Single().LocalConfigurationPresent &&
                retired.HostRegistry.Maps.Single().MapId == originalMap &&
                retired.ClusterRegistry.MapAssignments.Single().MapId == originalMap,
                "removing a local map keeps its stable identity and cluster assignment as inactive metadata");
            Check(File.ReadAllBytes(storeFixture).SequenceEqual(storeBytes) &&
                storeHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(storeFixture))),
                "profile migration leaves historic Store-v1 bytes unchanged");
        }

        string profileRoot = Path.Combine(testRoot, "registry-topology");
        Guid[] mapIds = new Guid[100];
        string[] hostIds;
        long revisionAfterAssignments;
        {
            await using var service = new ClusterService("registry-topology", profileRoot,
                _ => new ProcessCheck(false, "Synthetic test only", null, true));
            var localMaps = new[] {
                new LocalMap { Id = Guid.Parse("00000000-0000-4000-8000-000000000201"), Name = "Island" },
                new LocalMap { Id = Guid.Parse("00000000-0000-4000-8000-000000000202"), Name = "Astraeos" },
                new LocalMap { Id = Guid.Parse("00000000-0000-4000-8000-000000000203"), Name = "Ragnarok" }
            };
            foreach (var map in localMaps) service.AddMap(map);
            for (int i = 0; i < localMaps.Length; i++) mapIds[i] = localMaps[i].Id;

            hostIds = new string[12];
            hostIds[0] = service.Config.LocalHostId;
            for (int i = 1; i < hostIds.Length; i++)
            {
                hostIds[i] = Guid.Parse("00000000-0000-4000-8000-" + (300 + i).ToString("D12")).ToString("N");
                service.Registry.RegisterHost(hostIds[i], "Host " + i);
            }
            for (int i = localMaps.Length; i < mapIds.Length; i++)
            {
                mapIds[i] = Guid.Parse("00000000-0000-4000-8000-" + (1000 + i).ToString("D12"));
                int hostIndex = 1 + (i - localMaps.Length) % (hostIds.Length - 1);
                service.Registry.RegisterRemoteMap(hostIds[hostIndex], mapIds[i], "Map " + i);
            }
            var registered = service.Registry.Snapshot();
            Check(registered.HostRegistry.Hosts.Count == 12 && registered.HostRegistry.Maps.Count == 100,
                "registry supports twelve hosts and one hundred maps with uneven ownership");
            Check(registered.HostRegistry.Hosts.Count(x => x.IsLocal) == 1 &&
                registered.HostRegistry.Hosts.Where(x => !x.IsLocal).All(x => !x.IsPaired),
                "registered remote identities remain explicitly unpaired");

            string root = Path.Combine(profileRoot, "clusters");
            Directory.CreateDirectory(root);
            var a = service.Registry.CreateCluster("untamed-kingdom", "Untamed Kingdom",
                Path.Combine(root, "uk-transfer"), Path.Combine(root, "uk-backups"));
            var b = service.Registry.CreateCluster("test-cluster", "Test Cluster",
                Path.Combine(root, "test-transfer"), Path.Combine(root, "test-backups"));
            var c = service.Registry.CreateCluster("event-cluster", "Event Cluster",
                Path.Combine(root, "event-transfer"), Path.Combine(root, "event-backups"));
            Check(a.ClusterId == "untamed-kingdom" && b.ClusterId != c.ClusterId &&
                service.Registry.Snapshot().ClusterRegistry.Clusters.Count == 3,
                "multiple independent clusters persist on a single Manager host");
            Check(!Directory.Exists(a.TransferDirectory) && !Directory.Exists(a.BackupDirectory),
                "cluster registration records paths without creating or writing directories");

            bool pathCollisionDenied = false;
            try { service.Registry.CreateCluster("bad-transfer", "Bad", a.TransferDirectory); }
            catch (InvalidOperationException) { pathCollisionDenied = true; }
            Check(pathCollisionDenied, "different clusters cannot share a transfer directory");
            bool crossRoleCollisionDenied = false;
            try { service.Registry.CreateCluster("bad-cross-role", "Bad",
                Path.Combine(root, "unused-transfer"), a.BackupDirectory); }
            catch (InvalidOperationException) { crossRoleCollisionDenied = true; }
            Check(crossRoleCollisionDenied,
                "a cluster backup path cannot collide with another cluster directory");
            bool invalidIdDenied = false;
            try { service.Registry.CreateCluster("../bad", "Bad", Path.Combine(root, "bad")); }
            catch (InvalidDataException) { invalidIdDenied = true; }
            Check(invalidIdDenied, "cluster IDs reject path syntax");

            DateTimeOffset originalCreatedUtc = b.CreatedUtc;
            var updated = service.Registry.UpdateCluster(b.ClusterId, "Test Cluster Updated",
                b.TransferDirectory, b.BackupDirectory);
            Check(updated.Name == "Test Cluster Updated" && updated.CreatedUtc == originalCreatedUtc &&
                updated.UpdatedUtc >= updated.CreatedUtc,
                "cluster updates preserve stable IDs and creation time while recording changed settings");

            var previewA = service.Registry.PreviewMapAssignments(a.ClusterId, mapIds.Take(26));
            Check(previewA.IsValid && previewA.Additions.Count == 26,
                "membership preview supports and summarizes twenty-six selected maps");
            service.Registry.ApplyMapAssignments(a.ClusterId, mapIds.Take(26), previewA.BaseRevision);
            var previewB = service.Registry.PreviewMapAssignments(b.ClusterId,
                new[] { mapIds[0], mapIds[1], mapIds[26], mapIds[27] });
            Check(previewB.IsValid && previewB.Moves.Count == 2 && previewB.Additions.Count == 2,
                "preview distinguishes map moves from unassigned map additions");
            service.Registry.ApplyMapAssignments(b.ClusterId,
                new[] { mapIds[0], mapIds[1], mapIds[26], mapIds[27] }, previewB.BaseRevision);
            var assigned = service.Registry.Snapshot().ClusterRegistry.MapAssignments;
            Check(assigned.Count == 28 && assigned.Select(x => x.MapId).Distinct().Count() == assigned.Count &&
                assigned.Count(x => x.ClusterId == a.ClusterId) == 24 &&
                assigned.Count(x => x.ClusterId == b.ClusterId) == 4,
                "a map has at most one cluster owner after approved reassignment");

            var stale = service.Registry.PreviewMapAssignments(c.ClusterId, new[] { mapIds[5] });
            string lateHost = Guid.Parse("00000000-0000-4000-8000-000000000399").ToString("N");
            service.Registry.RegisterHost(lateHost, "Late Host");
            bool staleDenied = false;
            try { service.Registry.ApplyMapAssignments(c.ClusterId, new[] { mapIds[5] }, stale.BaseRevision); }
            catch (InvalidOperationException) { staleDenied = true; }
            Check(staleDenied, "membership confirmation is rejected after the registry revision changes");

            Guid protectedMap = mapIds[50];
            string protectedOwner = registered.HostRegistry.Maps.Single(x => x.MapId == protectedMap).HostId;
            string otherHost = hostIds.First(x => !StringComparer.OrdinalIgnoreCase.Equals(x, protectedOwner));
            bool ownershipDenied = false;
            try { service.Registry.RegisterRemoteMap(otherHost, protectedMap, "Rebound Map"); }
            catch (UnauthorizedAccessException) { ownershipDenied = true; }
            Check(ownershipDenied && service.Registry.Snapshot().HostRegistry.Maps
                .Single(x => x.MapId == protectedMap).HostId == protectedOwner,
                "a registered map cannot be rebound to a different physical host");

            using var bridge = new DesktopClusterRegistryBridge(service);
            var bridgeSnapshot = await Call(bridge, "cluster.registry.snapshot");
            Check(bridgeSnapshot.GetProperty("ok").GetBoolean() &&
                bridgeSnapshot.GetProperty("data").GetProperty("hostRegistry").GetProperty("maps").GetArrayLength() == 100,
                "React/WebView2 native route returns the current versioned registry snapshot");
            var bridgePreview = await Call(bridge, "cluster.membership.preview", new {
                clusterId = c.ClusterId, mapIds = new[] { mapIds[90].ToString("D") }
            });
            Check(bridgePreview.GetProperty("ok").GetBoolean() &&
                bridgePreview.GetProperty("data").GetProperty("preview").GetProperty("isValid").GetBoolean(),
                "native bridge returns a reviewable map membership preview");
            string token = bridgePreview.GetProperty("data").GetProperty("token").GetString()!;
            var missingConfirmation = await Call(bridge, "cluster.membership.apply", new { token, confirm = false });
            Check(!missingConfirmation.GetProperty("ok").GetBoolean() &&
                missingConfirmation.GetProperty("error").GetString() == "UM-REGISTRY-CONFIRMATION-REQUIRED",
                "membership writes require an explicit renderer confirmation");
            var applied = await Call(bridge, "cluster.membership.apply", new { token, confirm = true });
            Check(applied.GetProperty("ok").GetBoolean() &&
                applied.GetProperty("data").GetProperty("addedCount").GetInt32() == 1,
                "approved native membership token applies exactly the previewed map set");
            var replay = await Call(bridge, "cluster.membership.apply", new { token, confirm = true });
            Check(!replay.GetProperty("ok").GetBoolean() &&
                replay.GetProperty("error").GetString() == "UM-REGISTRY-PREVIEW-EXPIRED",
                "a membership approval token cannot be replayed");
            var deniedRemoteOperation = await Call(bridge, "cluster.remoteStart", new { mapId = mapIds[90] });
            Check(!deniedRemoteOperation.GetProperty("ok").GetBoolean() &&
                deniedRemoteOperation.GetProperty("error").GetString() == "UM-METHOD-DENIED",
                "registry bridge exposes no remote server lifecycle route");
            revisionAfterAssignments = service.Registry.Snapshot().Revision;
        }

        await using var restarted = new ClusterService("registry-topology", profileRoot,
            _ => new ProcessCheck(false, "Synthetic test only", null, true));
        var persisted = restarted.Registry.Snapshot();
        Check(persisted.HostRegistry.Hosts.Count == 13 && persisted.HostRegistry.Maps.Count == 100 &&
            persisted.ClusterRegistry.Clusters.Count == 3 && persisted.Revision == revisionAfterAssignments,
            "host identities, uneven map catalog and clusters survive Manager restart");
        Check(persisted.ClusterRegistry.MapAssignments.Count(x => x.MapId == mapIds[90]) == 1,
            "approved map assignment survives Manager restart without duplicate ownership");

        await ValidateOriginalLegacyMembershipIsImmutable(testRoot);
        ValidateLargeSyntheticTopology(testRoot);
    }

    private static async Task<JsonElement> Call(DesktopClusterRegistryBridge bridge,
        string method, object? args = null)
    {
        var request = new System.Collections.Generic.Dictionary<string, object?> {
            ["id"] = Guid.NewGuid().ToString("D"), ["method"] = method
        };
        if (args != null)
        {
            var fields = JsonSerializer.SerializeToElement(args);
            foreach (var field in fields.EnumerateObject()) request[field.Name] = field.Value.Clone();
        }
        using var response = JsonDocument.Parse(await bridge.HandleAsync(JsonSerializer.Serialize(request)));
        return response.RootElement.Clone();
    }
}
