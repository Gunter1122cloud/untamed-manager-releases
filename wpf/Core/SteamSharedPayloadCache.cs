using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

// A host-local, public Windows ASA build cache. Only a clean, SteamCMD-
// acknowledged staging installation can publish a reusable entry. No map
// is ever a cache source. Copy only a narrow official payload allowlist,
// never Saved, Config, Mods, ArkApi, cluster paths or Steam manifests.
// Every target still runs Valve's own app_update validate.
internal static class SteamSharedPayloadCache
{
    internal const long MinimumStagingFreeBytes=100L*1024*1024*1024;
    internal sealed record Entry(string Relative,long Length,string Sha256);
    internal sealed record Manifest(int Schema,string AppId,string Branch,string Platform,string Build,Entry[] Files);
    internal sealed record Package(string Root,Manifest Index,bool CacheHit);
    private const string IndexName="obsidian-cache-index.json";
    private const string ServerExe=@"ShooterGame\Binaries\Win64\ArkAscendedServer.exe";
    private const string PakRoot=@"ShooterGame\Content\Paks";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static bool BuildValid(string build)=>build.Length is >=1 and <=20&&build.All(char.IsAsciiDigit);
    private static bool Allowed(string relative)
    {
        if(relative.Equals(ServerExe,StringComparison.OrdinalIgnoreCase))return true;
        if(!relative.StartsWith(PakRoot+@"\",StringComparison.OrdinalIgnoreCase))return false;
        string name=relative[(PakRoot.Length+1)..];
        return Regex.IsMatch(name,@"\A(?:pakchunk[0-9]+[A-Za-z0-9_-]*|global)\.(?:pak|ucas|utoc)\z",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    }
    private static string Fixed(string directory)
    {
        _=UntamedOperationCoordinator.ResourceKey(directory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }
    private static bool Overlap(string left,string right)=>
        left.Equals(right,StringComparison.OrdinalIgnoreCase)||
        left.StartsWith(right+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
        right.StartsWith(left+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static string FileAt(string root,string relative)
    {
        if(!Allowed(relative))throw new InvalidDataException("UM-STEAM-CACHE-ALLOWLIST");
        string file=Path.Combine(Fixed(root),relative);
        _=Fixed(Path.GetDirectoryName(file)!);
        if(Directory.Exists(file)||(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0))
            throw new InvalidDataException("UM-STEAM-CACHE-LINKED-FILE");
        return file;
    }
    private static Entry Digest(string root,string relative)
    {
        string file=FileAt(root,relative);
        using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.SequentialScan);
        if(input.Length<=0||input.Length>128L*1024*1024*1024)
            throw new InvalidDataException("UM-STEAM-CACHE-PAYLOAD-SIZE");
        long size=input.Length;
        string digest=Convert.ToHexString(SHA256.HashData(input));
        if(input.Length!=size)throw new IOException("UM-STEAM-CACHE-PAYLOAD-CHANGED");
        return new(relative,size,digest);
    }
    private static void CheckBuild(string root,string build)
    {
        _=Fixed(Path.Combine(root,"steamapps"));
        if(AsaSteamBuildCatalog.InstalledBuild(root)!=build)
            throw new InvalidDataException("UM-STEAM-CACHE-BUILD-MISMATCH");
    }
    private static Entry[] Inventory(string root)
    {
        string paks=Fixed(Path.Combine(root,PakRoot));
        var files=new List<string>{ServerExe};
        if(Directory.Exists(paks))
            files.AddRange(Directory.EnumerateFiles(paks,"*",SearchOption.TopDirectoryOnly)
                .Select(f=>Path.GetRelativePath(root,f)).Where(Allowed).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));
        if(files.Count<2||files.Count>5000||files.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count)
            throw new InvalidDataException("UM-STEAM-CACHE-PAYLOAD-INCOMPLETE");
        return files.Select(relative=>Digest(root,relative)).ToArray();
    }
    private static Package ReadVerified(string root,string build,bool hit)
    {
        root=Fixed(root);CheckBuild(root,build);
        string path=Path.Combine(root,IndexName);
        var file=new FileInfo(path);
        if(!file.Exists||file.Length is <2 or >2_000_000||(file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STEAM-CACHE-INDEX-MISSING");
        var index=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path),Json)
            ??throw new InvalidDataException("UM-STEAM-CACHE-INDEX");
        if(index.Schema!=1||index.AppId!="2430930"||index.Branch!="public"||index.Platform!="windows"||
            index.Build!=build||index.Files==null||index.Files.Length is <2 or >5000||
            index.Files.Any(f=>f==null||!Allowed(f.Relative)||f.Length<=0||f.Sha256==null||
                f.Sha256.Length!=64||!f.Sha256.All(Uri.IsHexDigit))||
            !index.Files.SequenceEqual(Inventory(root)))
            throw new InvalidDataException("UM-STEAM-CACHE-DIGEST-MISMATCH");
        return new(root,index,hit);
    }
    internal static async Task<Package> PrepareAsync(string profile,IEnumerable<LocalMap> maps,string build,
        Func<string,CancellationToken,Task> fetch,CancellationToken ct,long minimumFreeBytes=MinimumStagingFreeBytes)
    {
        if(!BuildValid(build)||minimumFreeBytes<0)throw new InvalidDataException("UM-STEAM-CACHE-BUILD");
        string cache=Fixed(Path.Combine(profile,"steam-payload-cache","2430930-public-windows"));
        foreach(var map in maps)
            if(!string.IsNullOrWhiteSpace(map.InstallationPath)&&Overlap(cache,Fixed(map.InstallationPath)))
                throw new InvalidDataException("UM-STEAM-CACHE-MAP-OVERLAP");
        string target=Fixed(Path.Combine(cache,build));
        Directory.CreateDirectory(cache);
        // The caller holds the host-wide Steam serial queue. This extra
        // file lease prevents an overlapping cache publisher in any profile.
        string owner=Path.Combine(cache,"owner.lock");
        if(File.Exists(owner)&&(File.GetAttributes(owner)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("UM-STEAM-CACHE-LINKED-LOCK");
        using var held=new FileStream(owner,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        ct.ThrowIfCancellationRequested();
        if(Directory.Exists(target))return ReadVerified(target,build,true);
        if(new DriveInfo(Path.GetPathRoot(cache)!).AvailableFreeSpace<minimumFreeBytes)
            throw new IOException("UM-STEAM-CACHE-INSUFFICIENT-STAGING-SPACE");
        string staging=Fixed(Path.Combine(cache,build+".pending-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        // Interrupted staging is never selected as a cache hit. Do not
        // delete unknown files on failure or treat staging as a map.
        await fetch(staging,ct);
        ct.ThrowIfCancellationRequested();
        CheckBuild(staging,build);
        var manifest=new Manifest(1,"2430930","public","windows",build,Inventory(staging));
        using(var output=new FileStream(Path.Combine(staging,IndexName),FileMode.CreateNew,FileAccess.Write,
            FileShare.None,4096,FileOptions.WriteThrough))
        {JsonSerializer.Serialize(output,manifest,Json);output.Flush(true);}
        Directory.Move(staging,target);
        return new(target,manifest,false);
    }
    // Called only after the destination world/INI/plugin backup and generic
    // Update mutation claim. An interrupted binary copy is operator-review
    // territory, not a full binary rollback or an automatic retry.
    internal static long Seed(Package package,string install,Action verifyStopped,CancellationToken ct)
    {
        string source=Fixed(package.Root),target=Fixed(install);
        if(Overlap(source,target))throw new InvalidDataException("UM-STEAM-CACHE-TARGET-OVERLAP");
        CheckBuild(source,package.Index.Build);
        long bytes=0;
        foreach(var item in package.Index.Files)
        {
            ct.ThrowIfCancellationRequested();verifyStopped();
            string src=FileAt(source,item.Relative),dst=FileAt(target,item.Relative);
            if(File.Exists(dst)&&new FileInfo(dst).Length==item.Length&&Digest(target,item.Relative)==item)continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            _=FileAt(target,item.Relative);
            if(new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace<checked(item.Length+64L*1024*1024))
                throw new IOException("UM-STEAM-CACHE-TARGET-SPACE");
            string tmp=dst+"."+Guid.NewGuid().ToString("N")+".obsidian-seed";
            try
            {
                using(var input=new FileStream(src,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.SequentialScan))
                using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.SequentialScan))
                {
                    if(input.Length!=item.Length)throw new IOException("UM-STEAM-CACHE-SOURCE-SIZE-CHANGED");
                    var buffer=new byte[1024*1024];long left=item.Length;
                    while(left>0){ct.ThrowIfCancellationRequested();int n=input.Read(buffer,0,(int)Math.Min(buffer.Length,left));
                        if(n==0)throw new EndOfStreamException();output.Write(buffer,0,n);left-=n;}
                    if(input.ReadByte()!=-1)throw new IOException("UM-STEAM-CACHE-SOURCE-SIZE-CHANGED");
                    output.Flush(true);
                }
                using(var verify=new FileStream(tmp,FileMode.Open,FileAccess.Read,FileShare.Read))
                    if(verify.Length!=item.Length||Convert.ToHexString(SHA256.HashData(verify))!=item.Sha256)
                        throw new InvalidDataException("UM-STEAM-CACHE-SEED-DIGEST");
                verifyStopped();ct.ThrowIfCancellationRequested();_=FileAt(target,item.Relative);
                File.Move(tmp,dst,true);bytes=checked(bytes+item.Length);
            }
            finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
        return bytes;
    }
}
