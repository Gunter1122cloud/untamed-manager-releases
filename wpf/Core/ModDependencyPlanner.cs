using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UntamedManager.Core;

// T12: authoritative file-relation metadata becomes a deterministic dependency
// review order, NOT an entitlement to download, install, subscribe or restart.
public sealed record ModDependencyPreview(int RootModId,bool Consistent,
    IReadOnlyList<int> DependencyFirstOrder,IReadOnlyList<int> MissingRequired,
    IReadOnlyList<int> OptionalMods,IReadOnlyList<int> IncompatibleEnabled,
    IReadOnlyList<int> Unclassified,IReadOnlyList<int> Cyclic)
{
    public bool AutoInstallAllowed=>false;
}

public static class ModDependencyPlanner
{
    public static ModDependencyPreview Review(int rootModId,
        IEnumerable<CurseForgeFile> selectedFiles,IEnumerable<string> configuredModIds)
    {
        if(rootModId<10000||selectedFiles==null||configuredModIds==null)
            throw new InvalidDataException("Selected CurseForge project and local mod IDs required");
        var files=selectedFiles.Take(501).ToArray();
        if(files.Length==0||files.Length>500||
           files.Any(f=>f==null||f.ModId<10000||f.FileId<=0||!f.Available)||
           files.Select(f=>f.ModId).Distinct().Count()!=files.Length)
            throw new InvalidDataException("Unique available CurseForge file per mod is required");
        var dictionary=files.ToDictionary(x=>x.ModId);
        var configured=configuredModIds.ToArray();
        if(configured.Length>500||configured.Any(x=>x==null||
            !int.TryParse(x,out var id)||id<10000))
            throw new InvalidDataException("Invalid local mod configuration evidence");
        var enabled=configured.Select(int.Parse).ToHashSet();
        var ordered=new List<int>();
        var missing=new HashSet<int>();
        var optional=new HashSet<int>();
        var incompatibles=new HashSet<int>();
        var unknown=new HashSet<int>();
        var cycles=new HashSet<int>();
        var visiting=new HashSet<int>();
        var visited=new HashSet<int>();
        void Visit(int mod,int depth)
        {
            if(depth>100){cycles.Add(mod);return;}
            if(visited.Contains(mod))return;
            if(!dictionary.TryGetValue(mod,out var file)){missing.Add(mod);return;}
            if(!visiting.Add(mod)){cycles.Add(mod);return;}
            if(file.Relations.Count==0&&file.Dependencies.Count!=0)
            {
                foreach(var missingKind in file.Dependencies)unknown.Add(missingKind);
            }
            foreach(var relation in file.Relations)
            {
                int child=relation.ModId;
                if(child<10000||child==mod)
                {
                    unknown.Add(child);
                    if(child==mod)cycles.Add(mod);
                    continue;
                }
                switch(relation.Type)
                {
                    case CurseForgeRelationType.EmbeddedLibrary:
                    case CurseForgeRelationType.RequiredDependency:
                        Visit(child,depth+1);
                        break;
                    case CurseForgeRelationType.OptionalDependency:
                        optional.Add(child);
                        break;
                    case CurseForgeRelationType.Incompatible:
                        if(enabled.Contains(child)||dictionary.ContainsKey(child))
                            incompatibles.Add(child);
                        break;
                    case CurseForgeRelationType.None:
                        unknown.Add(child);
                        break;
                    case CurseForgeRelationType.Tool:
                    case CurseForgeRelationType.Include:
                        break;
                    default:
                        unknown.Add(child);
                        break;
                }
            }
            visiting.Remove(mod);
            visited.Add(mod);
            ordered.Add(mod);
        }
        Visit(rootModId,0);
        // A single API page may omit mandatory dependencies. Missing evidence
        // blocks the plan even if someone manually configured that ID locally.
        bool okay=missing.Count==0&&incompatibles.Count==0&&
            unknown.Count==0&&cycles.Count==0&&
            ordered.Count>0&&ordered.Last()==rootModId;
        return new(rootModId,okay,ordered,missing.Order().ToArray(),
            optional.Order().ToArray(),incompatibles.Order().ToArray(),
            unknown.Order().ToArray(),cycles.Order().ToArray());
    }
}
