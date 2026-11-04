using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

// A deliberately self-contained, in-memory training workspace. No production
// ClusterService, filesystem, socket, SQL, RCON or process dependency exists.
public enum DemoModMode { Active, Passive, Disabled }
public sealed record DemoMod(string ProjectId,DemoModMode Mode);
public sealed record DemoPlugin(string Name,bool Enabled);
public sealed class DemoHost
{
    public string Name { get; init; } = "";
    public bool Online { get; set; }
    public int CpuPercent { get; init; }
    public int UsedRamGiB { get; init; }
    public int TotalRamGiB { get; init; }
}
public sealed class DemoMap
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public bool Running { get; set; }
    public int Players { get; set; }
    public string ApiVersion { get; init; } = "1.0 (DEMO)";
    public List<DemoMod> Mods { get; } = new();
    public List<DemoPlugin> Plugins { get; } = new();
    public List<string> Backups { get; } = new();
    public Dictionary<string,string> Ini { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Logs { get; } = new();
}
public sealed class DemoWorkspace
{
    private readonly List<DemoHost> hosts=new();
    private readonly List<DemoMap> maps=new();
    private readonly List<string> events=new();
    // All snapshots are private, in-memory DEMO data; never touch files or ASA.
    private sealed record DemoSnapshot(bool Running,int Players,DemoMod[] Mods,
        DemoPlugin[] Plugins,Dictionary<string,string> Ini);
    private readonly Dictionary<Guid,Dictionary<string,DemoSnapshot>> snapshots=new();
    private readonly Dictionary<string,bool> schedules=new(StringComparer.OrdinalIgnoreCase)
    {
        ["Napi világmentés (DEMO)"]=true,
        ["RCON státuszfigyelés (DEMO)"]=true,
        ["Hétvégi boost (DEMO)"]=false
    };
    public IReadOnlyList<DemoHost> Hosts=>hosts;
    public IReadOnlyList<DemoMap> Maps=>maps;
    public IReadOnlyList<string> Events=>events;
    public IReadOnlyDictionary<string,bool> Schedules=>schedules;
    public bool Isolated=>true;

    public DemoWorkspace()
    {
        hosts.AddRange(new[]{
            new DemoHost{Name="DEMO-HOST-01",Online=true,CpuPercent=34,UsedRamGiB=52,TotalRamGiB=80},
            new DemoHost{Name="DEMO-HOST-02",Online=true,CpuPercent=21,UsedRamGiB=39,TotalRamGiB=64},
            new DemoHost{Name="DEMO-HOST-03",Online=false,CpuPercent=0,UsedRamGiB=0,TotalRamGiB=80}
        });
        var templates=new (string Name,int Host,int Players,bool Running)[]{
            ("The Island",0,5,true),("Astraeos",0,3,true),
            ("Ragnarok",1,4,true),("Aberration",1,0,false),
            ("Valguero",0,3,true),("Extinction",0,1,true),
            ("The Center",1,2,true),("Scorched Earth",1,0,false),
            ("Lost Colony",2,0,false),("Genesis Part 1",2,0,false)
        };
        foreach(var t in templates)
        {
            var map=new DemoMap{
                Name=t.Name,Host=hosts[t.Host].Name,Running=t.Running,
                Players=t.Running?t.Players:0
            };
            map.Mods.AddRange(new[]{
                new DemoMod("928548",DemoModMode.Active),
                new DemoMod("942024",DemoModMode.Active),
                new DemoMod("950914",DemoModMode.Passive)
            });
            map.Plugins.AddRange(new[]{
                new DemoPlugin("ArkShop",true),
                new DemoPlugin("Permissions",true),
                new DemoPlugin("Unicode RCON",false)
            });
            map.Ini["XPMultiplier"]="2.0";
            map.Ini["TamingSpeedMultiplier"]="5.0";
            map.Ini["HarvestAmountMultiplier"]="3.0";
            map.Backups.Add("MINTA-2026-11-03-22-00 · csak DEMO");
            Capture(map,map.Backups[0]);
            map.Logs.Add("[DEMO] Elkülönített mintanapló, valós ASA-napló nincs.");
            maps.Add(map);
        }
        Record("DEMO munkatér megnyitva; nincs hálózat, lemez- vagy SQL-művelet.");
    }
    public DemoMap Find(Guid id)=>maps.SingleOrDefault(x=>x.Id==id)
        ??throw new KeyNotFoundException("Unknown DEMO map");
    public DemoHost FindHost(string name)=>hosts.SingleOrDefault(x=>x.Name==name)
        ??throw new KeyNotFoundException("Unknown DEMO host");
    public string Status(DemoMap map)=>!FindHost(map.Host).Online
        ?"DEMO · OFFLINE GÉP":map.Running
        ?"DEMO · FUT ("+map.Players+" játékos)":"DEMO · LEÁLLÍTVA";

    private void Record(string text)
    {
        events.Insert(0,"[DEMO] "+text);
        if(events.Count>100)events.RemoveAt(events.Count-1);
    }
    public void SetHostOnline(string name,bool online)
    {
        var host=FindHost(name);
        host.Online=online;
        Record(name+" elérhetősége: "+(online?"online":"offline")+" (szimuláció)");
    }
    public void SetRunning(Guid mapId,bool running)
    {
        var map=Find(mapId);
        if(!FindHost(map.Host).Online)
            throw new InvalidOperationException("DEMO host is offline");
        map.Running=running;
        map.Players=running?Math.Max(map.Players,2):0;
        map.Logs.Add("[DEMO] "+(running?"Start":"Stop")+" szimuláció");
        Record(map.Name+": "+(running?"indítás":"leállítás")+" szimulálva.");
    }
    public void Restart(Guid mapId)
    {
        var map=Find(mapId);
        if(!FindHost(map.Host).Online || !map.Running)
            throw new InvalidOperationException("DEMO restart requires running map on online host");
        map.Logs.Add("[DEMO] SaveWorld → Stop → Start (szimuláció)");
        Record(map.Name+": SaveWorld → Stop → Start (kizárólag DEMO).");
    }
    public void SetMod(Guid mapId,string projectId,DemoModMode? mode)
    {
        if(!Regex.IsMatch(projectId??"",@"\A[0-9]{5,12}\z"))
            throw new ArgumentException("Invalid DEMO CurseForge ID",nameof(projectId));
        var map=Find(mapId);
        map.Mods.RemoveAll(x=>x.ProjectId==projectId);
        if(mode.HasValue)map.Mods.Add(new DemoMod(projectId,mode.Value));
        Record(map.Name+" mod "+projectId+": "+(mode?.ToString()??"removed"));
    }
    public bool MoveMod(Guid mapId,string projectId,int direction)
    {
        if(direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var map=Find(mapId);
        int from=map.Mods.FindIndex(x=>x.ProjectId==projectId);
        if(from<0)throw new KeyNotFoundException("Unknown DEMO mod");
        int to=from+direction;
        if(to<0||to>=map.Mods.Count)return false;
        (map.Mods[from],map.Mods[to])=(map.Mods[to],map.Mods[from]);
        Record(map.Name+": mod "+projectId+" betöltési sorrendje módosult (DEMO).");
        return true;
    }
    public void TogglePlugin(Guid mapId,string name)
    {
        var map=Find(mapId);
        var i=map.Plugins.FindIndex(x=>x.Name==name);
        if(i<0)throw new KeyNotFoundException("Unknown DEMO plugin");
        var previous=map.Plugins[i];
        map.Plugins[i]=previous with{Enabled=!previous.Enabled};
        Record(map.Name+" plugin "+name+" → "+(map.Plugins[i].Enabled?"enabled":"disabled"));
    }
    public void SetIni(Guid mapId,string key,string value)
    {
        if(string.IsNullOrWhiteSpace(key)||key.Length>80||
           !Regex.IsMatch(key,@"\A[A-Za-z][A-Za-z0-9_]*\z")||
           value==null||value.Length>180||value.IndexOfAny(new[]{'\r','\n','\0'})>=0)
            throw new ArgumentException("Invalid DEMO INI setting");
        var map=Find(mapId);
        map.Ini[key]=value;
        Record(map.Name+": INI "+key+" szerkesztése (csak memória).");
    }
    public string Rcon(Guid mapId,string command)
    {
        var map=Find(mapId);
        if(!map.Running || !FindHost(map.Host).Online)
            throw new InvalidOperationException("DEMO RCON requires running, online map");
        if(!new[]{"ListPlayers","SaveWorld","GetChat"}.Contains(command,StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("DEMO supports ListPlayers, SaveWorld and GetChat");
        Record(map.Name+": RCON "+command+" (szimulált válasz)");
        if(command.Equals("ListPlayers",StringComparison.OrdinalIgnoreCase))
            return "[DEMO] "+map.Players+" szimulált játékos: Explorer-01, Explorer-02";
        if(command.Equals("GetChat",StringComparison.OrdinalIgnoreCase))
            return "[DEMO] Explorer-01: Üdv az Untamed Kingdom mintaclusterben!";
        return "[DEMO] SaveWorld visszaigazolás (NEM történt valódi mentés).";
    }
    private void Capture(DemoMap map,string name)
    {
        if(!snapshots.TryGetValue(map.Id,out var perMap))
            snapshots[map.Id]=perMap=new Dictionary<string,DemoSnapshot>(StringComparer.Ordinal);
        perMap[name]=new DemoSnapshot(map.Running,map.Players,
            map.Mods.ToArray(),map.Plugins.ToArray(),
            new Dictionary<string,string>(map.Ini,StringComparer.OrdinalIgnoreCase));
    }
    public string Backup(Guid mapId)
    {
        var map=Find(mapId);
        string name="DEMO-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+
            "-"+Guid.NewGuid().ToString("N")[..6]+" · memóriabeli minta";
        Capture(map,name);
        map.Backups.Insert(0,name);
        Record(map.Name+": konfigurációs és állapotpillanatkép létrehozva (csak memória).");
        return name;
    }
    public void Restore(Guid mapId,string backupName)
    {
        var map=Find(mapId);
        if(backupName==null||!map.Backups.Contains(backupName)||
           !snapshots.TryGetValue(mapId,out var perMap)||
           !perMap.TryGetValue(backupName,out var saved))
            throw new KeyNotFoundException("Unknown DEMO backup snapshot");
        // DEMO-only: no real files, processes, RCON, Store-v1 or SQL accessed.
        map.Running=saved.Running;
        map.Players=saved.Players;
        map.Mods.Clear();map.Mods.AddRange(saved.Mods);
        map.Plugins.Clear();map.Plugins.AddRange(saved.Plugins);
        map.Ini.Clear();
        foreach(var pair in saved.Ini)map.Ini[pair.Key]=pair.Value;
        map.Logs.Add("[DEMO] Restore from "+backupName+" (memory only)");
        Record(map.Name+": mintapillanatkép visszaállítva: "+backupName+
            " (nincs lemez-, folyamat- vagy SQL-művelet).");
    }
    public void ToggleSchedule(string name)
    {
        if(!schedules.ContainsKey(name))throw new KeyNotFoundException("Unknown DEMO schedule");
        schedules[name]=!schedules[name];
        Record(name+" → "+(schedules[name]?"bekapcsolva":"kikapcsolva"));
    }
}
