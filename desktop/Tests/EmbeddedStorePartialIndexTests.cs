using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UntamedDesktop;

// Regression based on an observed 2026-11-09 offline Genesis Part 1 ASA
// Store-index SHAPE: two valid bounded sections followed by a malformed
// trailing header. No original player IDs, tribe IDs, original world bytes,
// Kova metadata or private archives are committed to this repository.
internal static class EmbeddedStorePartialIndexTests
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateHardLinkW",
        SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSyntheticHardLink(
        string newFile,string existingFile,IntPtr security);
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(
            "EMBEDDED STORE PREFIX TEST FAILED: "+reason);
        Console.WriteLine("PASS embedded original ASA Store index: "+reason);
    }
    private static void WriteSection(BinaryWriter w,string kind,ulong key)
    {
        byte[] data=new byte[128];
        byte[] name=Encoding.ASCII.GetBytes(kind);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            data.AsSpan(32,4),name.Length);
        name.CopyTo(data.AsSpan(36));
        w.Write(data.Length);
        w.Write(1);
        w.Write(data);
        w.Write(key);
        w.Write(0);
        w.Write(data.Length);
    }
    private static byte[] Blob(string tail)
    {
        using var stream=new MemoryStream();
        using(var w=new BinaryWriter(stream,Encoding.UTF8,true))
        {
            w.Write(1); // original supported Store index version
            WriteSection(w,"PrimalTribeData",101);
            WriteSection(w,"PrimalPlayerData",202);
            switch(tail)
            {
                case "valid":
                    w.Write(0);w.Write(0);
                    break;
                case "genesis":
                    // No real private survivor payload is copied. The
                    // malformed section HEADER fields alone reproduce the
                    // actual parser result seen on an offline Genesis .ark.
                    w.Write(71);
                    w.Write(1111116252);
                    w.Write(new byte[3845-8]);
                    break;
                case "trailing":
                    w.Write(0);w.Write(0);
                    w.Write(new byte[]{1,2,3,4});
                    break;
                case "short":
                    w.Write(17);
                    break;
                default:throw new ArgumentException("Unsupported fixture",nameof(tail));
            }
            w.Flush();
        }
        return stream.ToArray();
    }
    private static void SetBlob(string path,byte[] blob)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{
            DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false
        }.ToString());
        db.Open();
        using(var create=db.CreateCommand())
        {
            create.CommandText="CREATE TABLE IF NOT EXISTS custom(key TEXT PRIMARY KEY,value BLOB)";
            create.ExecuteNonQuery();
        }
        using(var insert=db.CreateCommand())
        {
            insert.CommandText="INSERT OR REPLACE INTO custom(key,value) VALUES('GameModeCustomBytes',$data)";
            insert.Parameters.AddWithValue("$data",blob);
            insert.ExecuteNonQuery();
        }
    }
    private static bool Denies(Func<object> read)
    {
        try {_=read();return false;}
        catch(InvalidDataException){return true;}
    }
    public static async Task Run(string sandbox)
    {
        string folder=Path.Combine(sandbox,"synthetic-genesis-store-prefix");
        Directory.CreateDirectory(folder);
        string world=Path.Combine(folder,"Genesis_WP.ark");
        foreach(string mode in new[]{"valid","genesis","trailing","short"})
        {
            SetBlob(world,Blob(mode));
            byte[] fingerprint=SHA256.HashData(File.ReadAllBytes(world));
            var report=EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(world);
            Check(report.ProfileCandidates==1&&report.TribeCandidates==1&&
                  report.Sections==2&&report.SourceUnchanged&&
                  !report.GameplayVerified&&!report.CharacterTravelVerified,
                "known prefix yields only bounded synthetic profile/tribe index candidates");
            if(mode=="valid")
                Check(report.State=="INDEX_SCANNED"&&report.IndexBoundsVerified,
                    "proper terminator at exact blob end is fully index-scanned");
            else
                Check(report.State=="INDEX_PREFIX_VALID_TRAILING_UNVERIFIED"&&
                      !report.IndexBoundsVerified&&
                      report.Evidence=="OFFLINE_SQLITE_PARTIAL_STORE_INDEX_UNVERIFIED",
                    mode+" trailing condition is NOT certified as a complete Store index");
            Check(SHA256.HashData(File.ReadAllBytes(world)).SequenceEqual(fingerprint),
                "offline SQLite inspector never changes the original synthetic world bytes");
        }
        using(var bridge=new DesktopEmbeddedStoreBridge(()=>world))
        {
            string response=await bridge.HandleAsync(JsonSerializer.Serialize(
                new{id=Guid.NewGuid().ToString("D"),method="store.inspectCopy"}));
            using var doc=JsonDocument.Parse(response);
            var result=doc.RootElement;
            Check(result.GetProperty("ok").GetBoolean()&&
                  !result.GetProperty("data").GetProperty("indexBoundsVerified").GetBoolean()&&
                  result.GetProperty("data").GetProperty("state").GetString()==
                      "INDEX_PREFIX_VALID_TRAILING_UNVERIFIED"&&
                  !response.Contains(folder,StringComparison.OrdinalIgnoreCase),
                "native offline picker reports partial results but exposes no private source paths");
        }

        // When a separate process owns a writable handle the read-only
        // index inspector must not issue any SQLite BLOB reads or imply a
        // stable original world, even if file length and mtime did not move.
        SetBlob(world,Blob("valid"));
        using(var foreignWriter=new FileStream(world,FileMode.Open,
            FileAccess.ReadWrite,FileShare.ReadWrite))
        {
            Check(Denies(()=>EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(world)),
                "outstanding original .ark writable handle rejects offline index proof");
            using var protectedBridge=new DesktopEmbeddedStoreBridge(()=>world);
            var denied=await protectedBridge.HandleAsync(JsonSerializer.Serialize(
                new{id=Guid.NewGuid().ToString("D"),method="store.inspectCopy"}));
            using var deniedDoc=JsonDocument.Parse(denied);
            Check(!deniedDoc.RootElement.GetProperty("ok").GetBoolean(),
                "read-only Store UI bridge fails closed during concurrent world writes");
        }
        string alias=Path.Combine(folder,"linked-world.ark");
        Check(CreateSyntheticHardLink(alias,world,IntPtr.Zero),
            "synthetic original world hardlink alias created on Windows");
        Check(Denies(()=>EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(world))&&
              Denies(()=>EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(alias)),
            "both hardlinked .ark names are rejected even without a reparse point");
        File.Delete(alias);
        var ordinary=EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(world);
        Check(ordinary.IndexBoundsVerified&&ordinary.State=="INDEX_SCANNED",
            "single-link stable offline world remains readable after alias removal");

        using(var output=new MemoryStream())
        using(var w=new BinaryWriter(output,Encoding.UTF8,true))
        {
            w.Write(1);
            w.Write(71);
            w.Write(1111116252);
            w.Flush();
            SetBlob(world,output.ToArray());
        }
        Check(Denies(()=>EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(world)),
            "invalid FIRST section is still rejected: no prefix to trust");
    }
}
