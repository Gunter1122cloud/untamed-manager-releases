using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    private const int EntrySize=16; // key[8], offset[int32], size[int32]
    private const string StoreKey="GameModeCustomBytes";

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
        var before=new FileInfo(file);
        if(before.Length<128)
            throw new InvalidDataException("UM-STORE-COPY-TOO-SMALL");
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
            int live=0;
            string kind="";
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
                    live++;
                    if(kind.Length==0&&bytes>=36)
                    {
                        // Embedded file class name follows its 32-byte header.
                        // Unknown classes remain unclassified, never guessed.
                        byte[] prefix=Read(dataStart+offset,Math.Min(bytes,292));
                        int classBytes=BinaryPrimitives.ReadInt32LittleEndian(
                            prefix.AsSpan(32,4));
                        if(classBytes is >=1 and <=256 && classBytes<=prefix.Length-36)
                        {
                            string className=Encoding.Latin1.GetString(prefix,36,classBytes);
                            if(className.Contains("PrimalTribeData",StringComparison.Ordinal))
                                kind="tribe";
                            else if(className.Contains("PrimalPlayerData",StringComparison.Ordinal))
                                kind="profile";
                        }
                    }
                }
            }
            switch(kind)
            {
                case "profile": players=checked(players+live);break;
                case "tribe": tribes=checked(tribes+live);break;
                default: unknown=checked(unknown+live);break;
            }
            sections++;
            position=indexEnd;
        }
        throw new InvalidDataException("UM-STORE-COPY-MISSING-TERMINATOR");
    }
}
