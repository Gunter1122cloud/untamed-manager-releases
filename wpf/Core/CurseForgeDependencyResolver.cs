using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed record ModDependencyResolution(int RootModId,bool FullMetadataRetrieved,
    int HttpPagesRead,ModDependencyPreview? Preview,string Reason)
{
    public bool DownloadOrInstallAllowed=>false;
}

// Read-only traversal of *required* CurseForge relations, including pagination.
// Optional mods are displayed but never queried, downloaded or activated.
public sealed class CurseForgeDependencyResolver(CurseForgeCatalog catalog)
{
    public async Task<ModDependencyResolution> ResolveAsync(
        string approvedKey,int rootModId,IEnumerable<string> configured,
        CancellationToken cancellationToken=default)
    {
        if(catalog==null)throw new ArgumentNullException(nameof(catalog));
        if(rootModId<10000||string.IsNullOrWhiteSpace(approvedKey)||
           configured==null)throw new ArgumentException("Explicit root, approved key and local state required");
        string[] enabled=configured.ToArray();
        if(enabled.Length>500||enabled.Any(id=>!int.TryParse(id,out int value)||value<10000))
            throw new InvalidDataException("Local enabled project IDs are unverified");
        const int maxModCount=24,maxPagesPerMod=8,maxPagesTotal=48;
        var wanted=new Queue<int>();wanted.Enqueue(rootModId);
        var seen=new HashSet<int>();
        var files=new List<CurseForgeFile>();
        int pages=0;
        while(wanted.Count>0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int modId=wanted.Dequeue();
            if(!seen.Add(modId))continue;
            if(seen.Count>maxModCount)
                return new(rootModId,false,pages,null,
                    "Dependency graph exceeds bounded 24-mod read-only inspection");
            var versions=new List<CurseForgeFile>();
            bool more;
            int index=0,modPages=0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(modPages>=maxPagesPerMod||pages>=maxPagesTotal)
                    return new(rootModId,false,pages,null,
                        "CurseForge file pagination exceeded safety bound; full version ordering unknown");
                var page=await catalog.GetModFilesAsync(
                    approvedKey,modId,index,cancellationToken);
                if(page.ModId!=modId||page.Index!=index||page.Total<page.Files.Count ||
                   page.Files.Any(f=>f.ModId!=modId))
                    throw new InvalidDataException("Inconsistent CurseForge pagination identity");
                pages++;modPages++;
                versions.AddRange(page.Files);
                more=page.HasMore;
                index+=25;
            }while(more);

            var latest=versions.Where(f=>f.Available)
                .OrderByDescending(f=>f.Published??DateTimeOffset.MinValue)
                .ThenByDescending(f=>f.FileId).FirstOrDefault();
            if(latest==null)
                return new(rootModId,false,pages,null,
                    "CurseForge does not provide an available file for a required project");
            if(versions.Where(f=>f.Available).Count()>1 &&
               versions.Any(f=>f.Available&&!f.Published.HasValue))
                return new(rootModId,false,pages,null,
                    "File publication dates are incomplete: latest mod cannot be proven");

            files.Add(latest);
            foreach(var required in latest.Relations.Where(r=>r.Type is
                CurseForgeRelationType.RequiredDependency or
                CurseForgeRelationType.EmbeddedLibrary))
                if(required.ModId>=10000&&!seen.Contains(required.ModId))
                    wanted.Enqueue(required.ModId);
        }
        var preview=ModDependencyPlanner.Review(rootModId,files,enabled);
        return new(rootModId,preview.Consistent,pages,preview,
            preview.Consistent?
                "Bounded complete metadata pages for required mod graph; install entitlement and crossplay not verified":
                "Dependency ordering is blocked by cycles, incompatible, missing or unknown relations");
    }
}
