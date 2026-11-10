using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;

namespace UntamedDesktop;

/// <summary>
/// Reads only the embedded profile/tribe index from an operator-selected,
/// local OFFLINE COPY of an ASA .ark SQLite save. No player bytes are exported,
/// no full-world materialization, no SQLite or ASA write operations.
/// Index counts are CANDIDATES, not proof of playable survivors/tribe ownership.
/// </summary>
public static class EmbeddedStoreReadOnlyInspector
{
    private const int MaxSections=3;
    private const string PartialState="INDEX_PREFIX_VALID_TRAILING_UNVERIFIED";
    private const int MaxEntries=100_000;
    // Each class name requires a separate, bounded SQLite BLOB slice.
    // Larger sections retain structurally bounded index evidence but excess
    // records are UNCLASSIFIED rather than optimistically labeled as the
    // first seen player/tribe type.
    private const int MaxClassReadsPerSection=2_048;
    private const int EntrySize=16; // key[8], offset[int32], size[int32]
    private const string StoreKey="GameModeCustomBytes";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInfo
    {
        public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,
            WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,out NativeFileInfo info);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,StringBuilder path,uint length,uint flags);

    // Pin an original, single-link OFFLINE world throughout every SQLite
    // BLOB-slice read. A concurrent writer/rename or NTFS hardlink alias
    // must not produce an apparently stable index with a torn survivor.
    private static FileStream PinOfflineCopy(string file)
    {
        FileStream held=new(file,FileMode.Open,FileAccess.Read,
            FileShare.Read,4096,FileOptions.RandomAccess);
        try
        {
            if(!OperatingSystem.IsWindows()||
               !GetFileInformationByHandle(held.SafeFileHandle,out var info)||
               info.Links!=1||
               (info.Attributes&(uint)FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-STORE-COPY-LINK-UNVERIFIED");
            var final=new StringBuilder(32768);
            uint count=GetFinalPathNameByHandle(held.SafeFileHandle,final,
                (uint)final.Capacity,0);
            string actual=final.ToString();
            if(actual.StartsWith(@"\\?\",StringComparison.Ordinal))
                actual=actual[4..];
            if(count==0||count>=final.Capacity||
               !string.Equals(actual,file,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UM-STORE-COPY-PATH-CHANGED");
            return held;
        }
        catch {held.Dispose();throw;}
    }

    public sealed record Report(string FileName,string State,int Format,
        long StoreBytes,int Sections,int ProfileCandidates,int TribeCandidates,
        int UnclassifiedCandidates,int SupersededEntries,int DuplicateKeys,
        bool IndexBoundsVerified,bool SourceUnchanged,
        bool GameplayVerified,bool CharacterTravelVerified,string Evidence);

    private static string FixedLocalCopy(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||
            path.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("UM-STORE-COPY-LOCAL-ONLY");
        string full=Path.GetFullPath(path);
        if(!string.Equals(Path.GetExtension(full),".ark",StringComparison.OrdinalIgnoreCase)||
            !File.Exists(full))
            throw new InvalidDataException("UM-STORE-COPY-ARK-REQUIRED");
        var root=Path.GetPathRoot(full);
        if(root==null||new DriveInfo(root).DriveType!=DriveType.Fixed)
            throw new InvalidDataException("UM-STORE-COPY-FIXED-DISK-ONLY");
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(full)!);
            dir!=null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("UM-STORE-COPY-REPARSE-DENIED");
        if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STORE-COPY-REPARSE-DENIED");
        // A live SQLite WAL or shared-memory sidecar is not a stable offline
        // world copy. Never proceed with an ambiguous uncheckpointed snapshot.
        if(File.Exists(full+"-wal")||File.Exists(full+"-shm"))
            throw new InvalidDataException("UM-STORE-COPY-SQLITE-SIDECAR-PRESENT");
        return full;
    }

    /// <summary>Read only bounded BLOB slices; never select the full value.</summary>
    public static Report InspectOfflineCopy(string path)
    {
        string file=FixedLocalCopy(path);
        // Keep the read-only Windows handle until AFTER SQLite connection
        // disposal and the final snapshot metadata check. In particular, a
        // SQLite read transaction alone does not prohibit external writes.
        using var pinnedWorld=PinOfflineCopy(file);
        var before=new FileInfo(file);
        if(before.Length<128||before.Length!=pinnedWorld.Length)
            throw new InvalidDataException("UM-STORE-COPY-TOO-SMALL-OR-CHANGED");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=file,Mode=SqliteOpenMode.ReadOnly,
            Cache=SqliteCacheMode.Private,Pooling=false
        }.ToString());
        db.Open();
        using(var pragmas=db.CreateCommand())
        {
            pragmas.CommandText="PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
            pragmas.ExecuteNonQuery();
        }
        long blobSize;
        using(var size=db.CreateCommand())
        {
            size.CommandText="SELECT length(value) FROM custom WHERE key=$key LIMIT 1";
            size.Parameters.AddWithValue("$key",StoreKey);
            var value=size.ExecuteScalar();
            blobSize=value==null||value==DBNull.Value?0:Convert.ToInt64(value);
        }

        byte[] Read(long offset,int count)
        {
            if(offset<0||count<0||offset>blobSize||count>blobSize-offset)
                throw new InvalidDataException("UM-STORE-COPY-INDEX-OUT-OF-BOUNDS");
            if(count==0)return Array.Empty<byte>();
            using var command=db.CreateCommand();
            command.CommandText="SELECT substr(value,$start,$count) FROM custom WHERE key=$key";
            command.Parameters.AddWithValue("$start",checked(offset+1)); // SQLite is 1-based
            command.Parameters.AddWithValue("$count",count);
            command.Parameters.AddWithValue("$key",StoreKey);
            var bytes=command.ExecuteScalar() as byte[];
            if(bytes==null||bytes.Length!=count)
                throw new InvalidDataException("UM-STORE-COPY-TRUNCATED");
            return bytes;
        }

        Report Result(string state,int format,int sections,int players,int tribes,
            int unknown,int superseded,int duplicates,bool bounds)
        {
            var after=new FileInfo(file);
            bool same=before.Length==after.Length&&
                before.Length==pinnedWorld.Length&&
                before.LastWriteTimeUtc==after.LastWriteTimeUtc;
            if(!same)throw new InvalidDataException("UM-STORE-COPY-CHANGED-DURING-READ");
            return new(Path.GetFileName(file),state,format,blobSize,sections,
                players,tribes,unknown,superseded,duplicates,bounds,true,
                false,false,bounds?
                    "OFFLINE_SQLITE_EMBEDDED_STORE_INDEX_ONLY":
                    "OFFLINE_SQLITE_PARTIAL_STORE_INDEX_UNVERIFIED");
        }

        if(blobSize<=8)return Result("NO_EMBEDDED_STORE",0,0,0,0,0,0,0,false);
        int format=BinaryPrimitives.ReadInt32LittleEndian(Read(0,4));
        if(format==0)return Result("NO_EMBEDDED_STORE",0,0,0,0,0,0,0,false);
        if(format!=1)
            return Result("UNSUPPORTED_EMBEDDED_STORE_FORMAT",format,0,0,0,0,0,0,false);

        int sections=0,players=0,tribes=0,unknown=0,superseded=0,duplicates=0;
        long position=4;
        for(int section=0;section<=MaxSections;section++)
        {
            // A real original Genesis Part 1 offline copy has two valid
            // sections followed by a nonconforming trailing header. A
            // validated prefix can be reported as CANDIDATE counts only;
            // it must never pass IndexBoundsVerified or imply a playable
            // survivor, backup consistency or production restore.
            if(blobSize-position<8&&sections>0)
                return Result(PartialState,format,sections,players,tribes,
                    unknown,superseded,duplicates,false);
            var header=Read(position,8);
            position=checked(position+8);
            int dataSize=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0,4));
            int count=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4,4));
            if(dataSize==0&&count==0)
            {
                // A valid terminator must be the actual END of the BLOB.
                // Nonempty trailing bytes used to be silently ignored and
                // were incorrectly presented as a fully checked index.
                if(position!=blobSize)
                    return Result(PartialState,format,sections,players,tribes,
                        unknown,superseded,duplicates,false);
                string state=unknown>0||duplicates>0?
                    "INDEX_SCANNED_WITH_UNCLASSIFIED_OR_DUPLICATES":"INDEX_SCANNED";
                return Result(state,format,sections,players,tribes,unknown,
                    superseded,duplicates,true);
            }
            if(section==MaxSections||dataSize<0||count<0||count>MaxEntries)
            {
                if(sections>0)
                    return Result(PartialState,format,sections,players,tribes,
                        unknown,superseded,duplicates,false);
                throw new InvalidDataException("UM-STORE-COPY-INVALID-SECTION");
            }
            long dataStart=position;
            long indexStart=checked(dataStart+dataSize);
            long indexEnd=checked(indexStart+(long)count*EntrySize);
            if(indexEnd>blobSize)
            {
                if(sections>0)
                    return Result(PartialState,format,sections,players,tribes,
                        unknown,superseded,duplicates,false);
                throw new InvalidDataException("UM-STORE-COPY-INDEX-OUT-OF-BOUNDS");
            }
            var currentKeys=new HashSet<ulong>();
            int classReads=0,sectionPlayers=0,sectionTribes=0,sectionUnknown=0;
            // Small batches keep UI/Manager memory independent of survivor count.
            for(int first=0;first<count;first+=64)
            {
                int batch=Math.Min(64,count-first);
                byte[] index=Read(indexStart+(long)first*EntrySize,batch*EntrySize);
                for(int row=0;row<batch;row++)
                {
                    var entry=index.AsSpan(row*EntrySize,EntrySize);
                    ulong key=BinaryPrimitives.ReadUInt64LittleEndian(entry[..8]);
                    if(key==0){superseded++;continue;}
                    int offset=BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8,4));
                    int bytes=BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(12,4));
                    if(offset<0||bytes<=0||(long)offset+bytes>dataSize)
                        throw new InvalidDataException("UM-STORE-COPY-ENTRY-OUT-OF-BOUNDS");
                    if(!currentKeys.Add(key))duplicates++;
                    // Never infer every record's type from the first class
                    // observed in the section. A mixed player/tribe section
                    // or malformed class name would fabricate counts.
                    if(bytes<36||classReads>=MaxClassReadsPerSection)
                    {
                        sectionUnknown++;
                        continue;
                    }
                    classReads++;
                    byte[] prefix=Read(dataStart+offset,Math.Min(bytes,292));
                    int classBytes=BinaryPrimitives.ReadInt32LittleEndian(
                        prefix.AsSpan(32,4));
                    if(classBytes is <1 or >256||classBytes>prefix.Length-36)
                    {
                        sectionUnknown++;
                        continue;
                    }
                    string className=Encoding.Latin1.GetString(prefix,36,classBytes);
                    bool isProfile=className.Contains(
                        "PrimalPlayerData",StringComparison.Ordinal);
                    bool isTribe=className.Contains(
                        "PrimalTribeData",StringComparison.Ordinal);
                    if(isProfile&&!isTribe)sectionPlayers++;
                    else if(isTribe&&!isProfile)sectionTribes++;
                    else sectionUnknown++;
                }
            }
            players=checked(players+sectionPlayers);
            tribes=checked(tribes+sectionTribes);
            unknown=checked(unknown+sectionUnknown);
            sections++;
            position=indexEnd;
        }
        throw new InvalidDataException("UM-STORE-COPY-MISSING-TERMINATOR");
    }
}
