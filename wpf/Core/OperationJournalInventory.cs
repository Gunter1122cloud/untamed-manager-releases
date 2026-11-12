using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed record OperationJournalInventoryEntry(
    string ResourceFingerprint,string Journal,string Kind,string Phase,
    DateTimeOffset? ChangedUtc,bool Registered,string? RegisteredMapNames,
    bool HistoricalSourceMatched,string? HistoricalMapKey,
    bool NeedsReview,bool Corrupt);
public sealed record OperationJournalInventorySnapshot(
    int RecordCount,int RegisteredRecords,int UnattributedResources,
    int UnattributedPendingResources,int HistoricalSourceRecords,
    int PendingRecords,int CorruptRecords,bool Truncated,
    bool HistoricalSourceReadable,
    OperationJournalInventoryEntry[] Entries)
{
    public string Evidence=>"BOUNDED_READ_ONLY_LOCAL_OPERATION_JOURNAL_INVENTORY";
    public bool DeletedOrReviewed=>false;
}

/// <summary>
/// Machine-local, READ-ONLY journal inventory. Its purpose is to distinguish
/// registered-map records from unknown resource hashes without pretending
/// the SHA-256 installation key can be reversed into an origin or PID.
/// Never performs a review, removes a journal or accesses ASA/Kova data.
/// </summary>
public static class OperationJournalInventory
{
    private const int MaxRecords=256;
    // Directory.EnumerateFiles is lazy, and no global sort is permitted.
    // Audit sidecars can outnumber journal records by orders of magnitude.
    private const int MaxCandidateFiles=4096;
    private static readonly JsonSerializerOptions RestartJson=
        new(JsonSerializerDefaults.Web);

    public static OperationJournalInventorySnapshot Inspect(
        IEnumerable<LocalMap> registered,
        IReadOnlyList<LegacyStoreV1OriginalMap>? trustedOriginalSource=null)
    {
        ArgumentNullException.ThrowIfNull(registered);
        string root=UntamedOperationCoordinator.DefaultRoot;
        CheckDirectory(root);
        var owners=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        foreach(var map in registered)
        {
            if(map==null||map.Id==Guid.Empty||
               string.IsNullOrWhiteSpace(map.InstallationPath))continue;
            try
            {
                string key=UntamedOperationCoordinator.ResourceKey(map.InstallationPath);
                if(!owners.TryGetValue(key,out var names))
                {
                    names=new List<string>();
                    owners.Add(key,names);
                }
                if(!names.Contains(map.Name,StringComparer.OrdinalIgnoreCase))
                    names.Add(map.Name);
            }
            catch(Exception ex) when(ex is IOException or ArgumentException or
                InvalidDataException or UnauthorizedAccessException)
            {
                // A malformed/remote map cannot authorize an unrelated hash.
                // Its own registration diagnostics remain independent.
            }
        }
        // A verified original Kova profile can identify otherwise foreign
        // resource hashes WITHOUT rewriting a journal or adopting a map.
        // This is SOURCE-path correlation only, never live ASA owner proof.
        var originalKeys=new Dictionary<string,string>(StringComparer.Ordinal);
        if(trustedOriginalSource!=null)
        {
            if(trustedOriginalSource.Count!=5 ||
               trustedOriginalSource.Select(m=>m.MapId).Distinct().Count()!=5)
                throw new InvalidDataException("UM-JOURNAL-ORIGINAL-SOURCE-MAP-COUNT");
            foreach(var old in trustedOriginalSource)
            {
                if(old==null||old.MapId==Guid.Empty||
                   string.IsNullOrWhiteSpace(old.OriginalInstallationRoot)||
                   string.IsNullOrWhiteSpace(old.MapKey))
                    throw new InvalidDataException("UM-JOURNAL-ORIGINAL-SOURCE-INVALID");
                string key=UntamedOperationCoordinator.ResourceKey(old.OriginalInstallationRoot);
                if(!originalKeys.TryAdd(key,old.MapKey))
                    throw new InvalidDataException("UM-JOURNAL-ORIGINAL-SOURCE-DUPLICATE");
            }
        }
        var entries=new List<OperationJournalInventoryEntry>();
        // The UI displays a short fingerprint, never a full installation hash.
        // Counts must nonetheless use the FULL 64-digit resource identity:
        // two different hashes can deliberately share a displayed prefix.
        var unknownKeys=new HashSet<string>(StringComparer.Ordinal);
        var unknownPendingKeys=new HashSet<string>(StringComparer.Ordinal);
        bool truncated=false;
        int candidates=0;
        foreach(var journal in new[]{"Operations-v1","Restart-v1"})
        {
            string folder=journal=="Operations-v1"?root:Path.Combine(root,"Restart-v1");
            CheckDirectory(folder);
            if(!Directory.Exists(folder))continue;
            // Eager OrderBy here scanned and materialized the entire folder
            // before MaxRecords could be enforced; stream and cap candidates.
            foreach(string file in Directory.EnumerateFiles(folder,"*.json",
                         SearchOption.TopDirectoryOnly))
            {
                if(++candidates>MaxCandidateFiles){truncated=true;break;}
                string key=Path.GetFileNameWithoutExtension(file);
                if(key.Length!=64||!key.All(Uri.IsHexDigit))continue;
                if(entries.Count>=MaxRecords){truncated=true;break;}
                owners.TryGetValue(key,out var names);
                bool registeredMap=names is {Count:>0};
                bool originalMatch=!registeredMap&&originalKeys.TryGetValue(key,out string? historicalName);
                string fingerprint=key[..16]; // No path or Store identifiers.
                try
                {
                    var info=new FileInfo(file);
                    int maximum=journal=="Operations-v1"?16384:8192;
                    if((info.Attributes&FileAttributes.ReparsePoint)!=0||
                       info.Length is <=0||info.Length>maximum)
                        throw new InvalidDataException("Invalid journal file");
                    using var stream=new FileStream(file,FileMode.Open,
                        FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    string kind,phase;
                    DateTimeOffset updated;
                    bool pending;
                    if(journal=="Operations-v1")
                    {
                        var record=JsonSerializer.Deserialize<UntamedOperationRecord>(stream);
                        if(record==null||record.Schema!=1||record.Resource!=key||
                           record.Operation==Guid.Empty||!Enum.IsDefined(record.Kind)||
                           !Enum.IsDefined(record.Phase))
                            throw new InvalidDataException("Journal identity mismatch");
                        kind=record.Kind.ToString();phase=record.Phase.ToString();
                        updated=record.UpdatedUtc;
                        pending=record.Phase is (UntamedOperationPhase.Prepared or
                            UntamedOperationPhase.Mutating or UntamedOperationPhase.NeedsOperator);
                    }
                    else
                    {
                        var record=JsonSerializer.Deserialize<AsaRestartRecord>(
                            stream,RestartJson);
                        if(record==null||record.Schema!=1||record.Resource!=key||
                           record.Id==Guid.Empty||record.MapId==Guid.Empty||
                           record.OldPid<=0||record.NewPid<0||
                           !Enum.IsDefined(record.Phase))
                            throw new InvalidDataException("Restart identity mismatch");
                        kind="Restart";phase=record.Phase.ToString();
                        updated=record.ChangedUtc;
                        pending=record.Phase is (AsaRestartPhase.Stopping or
                            AsaRestartPhase.Starting or AsaRestartPhase.NeedsOperator);
                    }
                    entries.Add(new(fingerprint,journal,kind,phase,updated,
                        registeredMap,registeredMap?string.Join(", ",names!):null,
                        originalMatch,originalMatch?historicalName:null,
                        pending,false));
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                    InvalidDataException or JsonException)
                {
                    // Diagnostics must not silently relabel corrupt journals
                    // as absent, but they also must not expose raw file paths.
                    entries.Add(new(fingerprint,journal,"Unknown","Corrupt",
                        null,registeredMap,registeredMap?string.Join(", ",names!):null,
                        originalMatch,originalMatch?historicalName:null,
                        true,true));
                }
                if(!registeredMap&&!originalMatch)
                {
                    unknownKeys.Add(key);
                    if(entries[^1].NeedsReview)unknownPendingKeys.Add(key);
                }
            }
            if(truncated)break;
        }
        return new(entries.Count,entries.Count(e=>e.Registered),
            unknownKeys.Count,unknownPendingKeys.Count,
            entries.Count(e=>e.HistoricalSourceMatched),
            entries.Count(e=>e.NeedsReview),entries.Count(e=>e.Corrupt),
            truncated,trustedOriginalSource!=null,entries.ToArray());
    }
    private static void CheckDirectory(string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",
            StringComparison.Ordinal)||path.StartsWith("//",StringComparison.Ordinal))
            throw new InvalidDataException("UM-OPERATION-JOURNAL-INVENTORY-ROOT");
        for(var node=new DirectoryInfo(Path.GetFullPath(path));
            node!=null;node=node.Parent)
            if(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-OPERATION-JOURNAL-INVENTORY-LINK");
    }
}
