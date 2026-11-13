import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AlertTriangle, CheckCircle2, CirclePlus, RefreshCw, Save, Shield } from "lucide-react";
import {
  managerBridge, type ClusterMembershipPreviewResult,
  type ClusterRegistrySnapshot, type RegisteredHost,
  type HistoricalExpansionReadiness
} from "./bridge";
import "./cluster-registry.css";

type ClusterFields = {
  clusterId:string;name:string;transferDirectory:string;backupDirectory:string;
};
const blankCluster:ClusterFields={
  clusterId:"",name:"",transferDirectory:"",backupDirectory:""
};

function RegistryPanel({title,children,action}:{
  title:string;children:React.ReactNode;action?:React.ReactNode
}) {
  return <section className="glass panel cluster-registry-panel">
    <div className="panel-head"><h2>{title}</h2>{action}</div>
    <div className="panel-inner">{children}</div>
  </section>;
}

export function ClusterRegistryPanel({protectedMapIds=new Set<string>()}:{protectedMapIds?:Set<string>}={}) {
  const [snapshot,setSnapshot]=useState<ClusterRegistrySnapshot|null>(null);
  const [clusterId,setClusterId]=useState("");
  const clusterIdRef=useRef(clusterId);
  const [fields,setFields]=useState<ClusterFields>(blankCluster);
  const [editing,setEditing]=useState(false);
  const [selectedMapIds,setSelectedMapIds]=useState<Set<string>>(new Set());
  const [mapQuery,setMapQuery]=useState("");
  const [mapHostFilter,setMapHostFilter]=useState("all");
  const [preview,setPreview]=useState<ClusterMembershipPreviewResult|null>(null);
  const [expansionMapId,setExpansionMapId]=useState("");
  const [expansion,setExpansion]=useState<HistoricalExpansionReadiness|null>(null);
  const [hostId,setHostId]=useState("");
  const [hostName,setHostName]=useState("");
  const [remoteMapId,setRemoteMapId]=useState("");
  const [remoteMapName,setRemoteMapName]=useState("");
  const [remoteMapHostId,setRemoteMapHostId]=useState("");
  const [busy,setBusy]=useState(false);
  const [notice,setNotice]=useState("");
  const [error,setError]=useState("");

  const reload=useCallback(async (preferredClusterId?:string) => {
    setBusy(true);setError("");
    try {
      const value=await managerBridge.clusterRegistrySnapshot();
      setSnapshot(value);
      setExpansion(null);
      const preferred=preferredClusterId??clusterIdRef.current;
      const selected=value.clusterRegistry.clusters.find(x=>x.clusterId===preferred)
        ??value.clusterRegistry.clusters[0];
      if(selected){
        setClusterId(selected.clusterId);
        setFields({clusterId:selected.clusterId,name:selected.name,
          transferDirectory:selected.transferDirectory,backupDirectory:selected.backupDirectory});
        setEditing(true);
      }else if(!preferredClusterId){
        setClusterId("");setFields(blankCluster);setEditing(false);
      }
      const firstRemote=value.hostRegistry.hosts.find(x=>!x.isLocal);
      setRemoteMapHostId(firstRemote?.hostId??"");
    }catch(e){
      const code=e instanceof Error?e.message:"UM-REGISTRY-FAILED";
      setError("A natív cluster-regiszter nem olvasható ("+code+").");
    }finally{setBusy(false);}
  },[]);

  useEffect(()=>{clusterIdRef.current=clusterId;},[clusterId]);

  useEffect(()=>{void reload();},[reload]);

  const selectedCluster=useMemo(()=>snapshot?.clusterRegistry.clusters.find(
    x=>x.clusterId===clusterId)??null,[snapshot,clusterId]);
  const localCandidates=useMemo(()=>{
    const host=snapshot?.hostRegistry.hosts.find(x=>x.isLocal);
    return (snapshot?.hostRegistry.maps??[]).filter(map=>
      host?.hostId===map.hostId && map.localConfigurationPresent &&
      !protectedMapIds.has(map.mapId.toLowerCase()));
  },[snapshot,protectedMapIds]);
  const owners=useMemo(()=>new Map((snapshot?.hostRegistry.hosts??[])
    .map(host=>[host.hostId,host] as const)),[snapshot]);
  const groupedMaps=useMemo(()=>{
    const groups=new Map<string,{host:RegisteredHost|null;maps:NonNullable<ClusterRegistrySnapshot["hostRegistry"]["maps"]>}>();
    for(const map of snapshot?.hostRegistry.maps??[]){
      let group=groups.get(map.hostId);
      if(!group){group={host:owners.get(map.hostId)??null,maps:[]};groups.set(map.hostId,group);}
      group.maps.push(map);
    }
    return [...groups.values()].sort((a,b)=>(a.host?.hostName??"").localeCompare(
      b.host?.hostName??"","hu-HU"));
  },[snapshot,owners]);
  const visibleMapGroups=useMemo(()=>{
    const needle=mapQuery.trim().toLocaleLowerCase("hu-HU");
    return groupedMaps.map(group=>({...group,maps:group.maps.filter(map=>{
      const hostId=group.host?.hostId??"";
      const hostMatches=mapHostFilter==="all"||
        (mapHostFilter==="local"?group.host?.isLocal===true:hostId===mapHostFilter);
      const queryMatches=!needle||map.mapName.toLocaleLowerCase("hu-HU").includes(needle)||
        map.mapId.toLocaleLowerCase("hu-HU").includes(needle)||
        (group.host?.hostName??"").toLocaleLowerCase("hu-HU").includes(needle);
      return hostMatches&&queryMatches;
    })})).filter(group=>group.maps.length>0);
  },[groupedMaps,mapQuery,mapHostFilter]);

  useEffect(()=>{
    if(!snapshot||!selectedCluster)return;
    const active=new Set(snapshot.hostRegistry.maps.filter(map=>{
      const host=owners.get(map.hostId);
      return !host?.isLocal||map.localConfigurationPresent;
    }).map(map=>map.mapId.toLowerCase()));
    setSelectedMapIds(new Set(snapshot.clusterRegistry.mapAssignments
      .filter(x=>x.clusterId===selectedCluster.clusterId&&active.has(x.mapId.toLowerCase()))
      .map(x=>x.mapId.toLowerCase())));
    setPreview(null);
  },[snapshot,selectedCluster,owners]);

  const beginNew=()=>{
    setClusterId("");setFields(blankCluster);setEditing(false);
    setSelectedMapIds(new Set());setPreview(null);setExpansion(null);setNotice("");setError("");
  };
  const selectCluster=(value:string)=>{
    setClusterId(value);setPreview(null);setExpansion(null);setNotice("");setError("");
    const selected=snapshot?.clusterRegistry.clusters.find(x=>x.clusterId===value);
    if(selected){
      setFields({clusterId:selected.clusterId,name:selected.name,
        transferDirectory:selected.transferDirectory,backupDirectory:selected.backupDirectory});
      setEditing(true);
    }
  };
  const saveCluster=async()=>{
    setBusy(true);setError("");setNotice("");setPreview(null);
    try{
      const saved=editing
        ?await managerBridge.clusterUpdate(fields.clusterId,fields.name,fields.transferDirectory,fields.backupDirectory)
        :await managerBridge.clusterCreate(fields.clusterId,fields.name,fields.transferDirectory,fields.backupDirectory);
      setClusterId(saved.clusterId);setFields({clusterId:saved.clusterId,name:saved.name,
        transferDirectory:saved.transferDirectory,backupDirectory:saved.backupDirectory});
      setEditing(true);setNotice(editing?"A cluster beállításai elmentve.":"A cluster létrejött.");
      await reload(saved.clusterId);
    }catch(e){
      const code=e instanceof Error?e.message:"UM-REGISTRY-FAILED";
      setError("A cluster beállításait a natív ellenőrzés elutasította ("+code+").");
    }finally{setBusy(false);}
  };
  const registerHost=async()=>{
    setBusy(true);setError("");setNotice("");
    try{
      await managerBridge.clusterRegisterHost(hostId,hostName);
      setHostName("");setNotice("A gazdagép azonosítója metaadatként regisztrálva; párosítás még nincs.");
      await reload(clusterId);
    }catch(e){
      const code=e instanceof Error?e.message:"UM-REGISTRY-FAILED";
      setError("A gazdagép regisztrálása sikertelen ("+code+").");
    }finally{setBusy(false);}
  };
  const registerMap=async()=>{
    setBusy(true);setError("");setNotice("");
    try{
      await managerBridge.clusterRegisterMap(remoteMapHostId,remoteMapId,remoteMapName);
      setRemoteMapId("");setRemoteMapName("");
      setNotice("A map metaadata regisztrálva. Élő állapotot csak hitelesített LAN-párosítás adhat.");
      await reload(clusterId);
    }catch(e){
      const code=e instanceof Error?e.message:"UM-REGISTRY-FAILED";
      setError("A map regisztrálása sikertelen ("+code+").");
    }finally{setBusy(false);}
  };
  const showPreview=async()=>{
    if(!selectedCluster)return;
    setBusy(true);setError("");setNotice("");
    try{
      setPreview(await managerBridge.clusterMembershipPreview(
        selectedCluster.clusterId,[...selectedMapIds]));
    }catch(e){
      const code=e instanceof Error?e.message:"UM-REGISTRY-FAILED";
      setPreview(null);setError("A hozzárendelés előnézete nem készült el ("+code+").");
    }finally{setBusy(false);}
  };
  const applyPreview=async()=>{
    const token=preview?.token;
    if(!token)return;
    setBusy(true);setError("");setNotice("");
    try{
      const result=await managerBridge.clusterMembershipApply(token);
      setNotice(`Jóváhagyva: ${result.addedCount} hozzáadás, ${result.movedCount} áthelyezés, ${result.removedCount} eltávolítás.`);
      setPreview(null);await reload(result.clusterId);
    }catch(e){
      const code=e instanceof Error?e.message:"UM-REGISTRY-FAILED";
      setPreview(null);setError("A jóváhagyás elutasítva vagy az előnézet elavult ("+code+"). Készíts új előnézetet.");
    }finally{setBusy(false);}
  };

  const inspectHistoricalExpansion=async()=>{
    if(!selectedCluster || !expansionMapId || busy)return;
    setExpansion(null);setBusy(true);setError("");
    try{
      setExpansion(await managerBridge.historicalExpansionInspect(
        expansionMapId,selectedCluster.clusterId));
    }catch(e){
      setError("A történeti Store-v1 előellenőrzés sikertelen ("+
        (e instanceof Error?e.message:"UM-EXPANSION-PREFLIGHT-FAILED")+").");
    }finally{setBusy(false);}
  };

  const toggleMap=(mapId:string,checked:boolean)=>{
    setSelectedMapIds(previous=>{
      const next=new Set(previous);
      if(checked)next.add(mapId.toLowerCase());else next.delete(mapId.toLowerCase());
      return next;
    });
    setPreview(null);
  };

  return <div className="cluster-registry-workbench">
    <div className="cluster-registry-heading">
      <div><span className="eyebrow">VERSIONED HOST / MAP OWNERSHIP</span>
        <h2>Cluster-regiszter</h2>
        <p>A host- és mapazonosítók a Manager profiljában maradnak. A pipák csak a jóváhagyás után módosítják a cluster-tagság metaadatait.</p></div>
      <button className="quiet" type="button" disabled={busy} onClick={()=>{void reload(clusterId);}}>
        <RefreshCw size={16}/> Frissítés
      </button>
    </div>

    {snapshot&&<div className="cluster-registry-metrics">
      <div><b>{snapshot.hostRegistry.hosts.length}</b><span>regisztrált gazdagép</span></div>
      <div><b>{snapshot.hostRegistry.maps.length}</b><span>regisztrált map</span></div>
      <div><b>{snapshot.clusterRegistry.clusters.length}</b><span>cluster</span></div>
      <div><b>v{snapshot.clusterRegistry.schemaVersion} · r{snapshot.revision}</b><span>adatmodell / revízió</span></div>
    </div>}

    <div className="cluster-registry-columns">
      <RegistryPanel title="Cluster adatai" action={<button className="quiet" type="button" onClick={beginNew}>
        <CirclePlus size={16}/> Új cluster</button>}>
        {snapshot?.clusterRegistry.clusters.length?<label className="cluster-registry-field">Kiválasztott cluster
          <select aria-label="Kiválasztott cluster" value={clusterId} onChange={e=>selectCluster(e.target.value)}>
            {snapshot.clusterRegistry.clusters.map(item=><option key={item.clusterId} value={item.clusterId}>{item.name} · {item.clusterId}</option>)}
          </select>
        </label>:<div className="empty">Még nincs cluster. Adj meg egy saját azonosítót és egy közös transferkönyvtárat.</div>}
        <div className="cluster-registry-form">
          <label className="cluster-registry-field">Clusterazonosító
            <input value={fields.clusterId} disabled={editing} maxLength={64}
              onChange={e=>setFields({...fields,clusterId:e.target.value})} placeholder="untamed-kingdom"/>
          </label>
          <label className="cluster-registry-field">Megjelenített név
            <input value={fields.name} maxLength={100}
              onChange={e=>setFields({...fields,name:e.target.value})} placeholder="Untamed Kingdom"/>
          </label>
          <label className="cluster-registry-field">Közös transferkönyvtár
            <input value={fields.transferDirectory} maxLength={520}
              onChange={e=>setFields({...fields,transferDirectory:e.target.value})}
              placeholder="\\\\szerver\\Untamed\\Transfer"/>
          </label>
          <label className="cluster-registry-field">Cluster-mentések könyvtára
            <input value={fields.backupDirectory} maxLength={520}
              onChange={e=>setFields({...fields,backupDirectory:e.target.value})}
              placeholder="\\\\szerver\\Untamed\\Backups"/>
          </label>
          <button className="primary" type="button" disabled={busy||!fields.clusterId.trim()||!fields.name.trim()||!fields.transferDirectory.trim()}
            onClick={()=>{void saveCluster();}}><Save size={16}/>{editing?"Cluster beállításainak mentése":"Cluster létrehozása"}</button>
        </div>
        <div className="warning-inline"><Shield size={17}/> A könyvtárak útvonala metaadatként mentődik; a regiszter nem hoz létre mappát, nem ír Store-v1 fájlt, és nem indít távoli műveletet.</div>
      </RegistryPanel>

      <RegistryPanel title="Gazdagépek és mapok">
        {snapshot&&<>
          <div className="cluster-registry-host-list">
            {snapshot.hostRegistry.hosts.map(host=><div className="cluster-registry-host" key={host.hostId}>
              <span className={host.isLocal?"cluster-registry-host-dot local":"cluster-registry-host-dot"}/>
              <span><b>{host.hostName}</b><small>{host.hostId}</small></span>
              <em>{host.isLocal?"Helyi":host.isPaired?"Párosított":"Nincs párosítva"}</em>
            </div>)}
          </div>
          <details className="cluster-registry-add-host">
            <summary>Gazdagép vagy távoli map regisztrálása</summary>
            <div className="cluster-registry-form">
              <label className="cluster-registry-field">Tartós host GUID
                <input value={hostId} maxLength={36} onChange={e=>setHostId(e.target.value)} placeholder="00000000-0000-4000-8000-000000000001"/>
              </label>
              <label className="cluster-registry-field">Gazdagép neve
                <input value={hostName} maxLength={100} onChange={e=>setHostName(e.target.value)} placeholder="UK-HOME-02"/>
              </label>
              <button className="secondary" type="button" disabled={busy||!hostId.trim()||!hostName.trim()}
                onClick={()=>{void registerHost();}}>Gazdagép regisztrálása</button>
              <label className="cluster-registry-field">Map tulajdonos gazdagépe
                <select aria-label="Map tulajdonos gazdagépe" value={remoteMapHostId}
                  onChange={e=>setRemoteMapHostId(e.target.value)}>
                  <option value="">Válassz gazdagépet</option>
                  {snapshot.hostRegistry.hosts.filter(host=>!host.isLocal).map(host=><option key={host.hostId} value={host.hostId}>{host.hostName}</option>)}
                </select>
              </label>
              <label className="cluster-registry-field">Map tartós GUID
                <input value={remoteMapId} maxLength={36} onChange={e=>setRemoteMapId(e.target.value)} placeholder="00000000-0000-4000-8000-000000000002"/>
              </label>
              <label className="cluster-registry-field">Map neve
                <input value={remoteMapName} maxLength={100} onChange={e=>setRemoteMapName(e.target.value)} placeholder="Ragnarok"/>
              </label>
              <button className="secondary" type="button" disabled={busy||!remoteMapHostId||!remoteMapId.trim()||!remoteMapName.trim()}
                onClick={()=>{void registerMap();}}>Távoli map metaadatának mentése</button>
            </div>
          </details>
        </>}
        {!snapshot&&<div className="empty">A gazdagép-regiszter betöltése...</div>}
      </RegistryPanel>
    </div>

    <RegistryPanel title="Map-hozzárendelés jóváhagyása"
      action={selectedCluster&&<span className="mod-tag grey">{selectedCluster.name}</span>}>
      {!selectedCluster&&<div className="empty">Először hozz létre vagy válassz clustert.</div>}
      {selectedCluster&&<>
        <p className="muted small">A map egy időben legfeljebb egy clusterhez tartozhat. A másik clusterből érkező map az előnézetben áthelyezésként jelenik meg.</p>
        <div className="cluster-registry-map-controls">
          <label className="cluster-registry-field">Map vagy gazdagép keresése
            <input aria-label="Cluster map keresése" value={mapQuery}
              onChange={e=>setMapQuery(e.target.value)} placeholder="Mapnév, GUID vagy host"/>
          </label>
          <label className="cluster-registry-field">Gazdagép szűrő
            <select aria-label="Cluster gazdagép szűrő" value={mapHostFilter}
              onChange={e=>setMapHostFilter(e.target.value)}>
              <option value="all">Minden gazdagép</option>
              <option value="local">Csak helyi</option>
              {(snapshot?.hostRegistry.hosts??[]).filter(host=>!host.isLocal).map(host=>
                <option key={host.hostId} value={host.hostId}>{host.hostName}</option>)}
            </select>
          </label>
          <span className="muted small">{visibleMapGroups.reduce((sum,group)=>sum+group.maps.length,0)} map megjelenítve</span>
        </div>
        <div className="cluster-registry-map-groups">
          {visibleMapGroups.map(group=><fieldset className="cluster-registry-map-group" key={group.host?.hostId??"unknown"}>
            <legend>{group.host?.hostName??"Ismeretlen gazdagép"} · {group.maps.length} map
              {!group.host?.isLocal&&<span> · {group.host?.isPaired?"LAN-párosított":"csak regiszteradat"}</span>}
            </legend>
            {group.maps.map(map=>{
              const inactive=group.host?.isLocal===true&&!map.localConfigurationPresent;
              const key=map.mapId.toLowerCase();
              const protectedStore=protectedMapIds.has(key);
              return <label className={"cluster-registry-map"+(inactive||protectedStore?" inactive":"")} key={key}>
                <input type="checkbox" checked={selectedMapIds.has(key)} disabled={busy||inactive||protectedStore}
                  onChange={e=>toggleMap(map.mapId,e.target.checked)}/>
                <span><b>{map.mapName}</b><small>{protectedStore?"eredeti Store-v1 védett":inactive?"Már nincs a helyi Managerben":map.mapId}</small></span>
                {(inactive||protectedStore)&&<em>{protectedStore?"Védett":"Inaktív"}</em>}
              </label>;
            })}
          </fieldset>)}
          {visibleMapGroups.length===0&&<div className="empty">Nincs regisztrált map a kiválasztott szűrőhöz.</div>}
        </div>
        <div className="cluster-registry-actions">
          <button className="secondary" type="button" disabled={busy}
            onClick={()=>{void showPreview();}}>Tagságváltozás előnézete</button>
          <span className="muted small">Kijelölve: {selectedMapIds.size} map</span>
        </div>
        {preview&&<div className="cluster-membership-preview" role="region" aria-label="Map-tagság előnézete">
          <strong>{preview.preview.isValid?"Ellenőrzött előnézet":"Javítandó hozzárendelés"}</strong>
          <div className="cluster-membership-counts">
            <span>{preview.preview.additions.length} hozzáadás</span>
            <span>{preview.preview.moves.length} áthelyezés</span>
            <span>{preview.preview.removals.length} eltávolítás</span>
            <span>Revízió {preview.preview.baseRevision}</span>
          </div>
          {[...preview.preview.moves,...preview.preview.additions,...preview.preview.removals].map(change=><div className="cluster-membership-change" key={change.mapId}>
            <b>{change.mapName}</b><span>{change.hostName}</span>
            <small>{change.previousClusterId??"Nincs tagság"} → {change.targetClusterId??"Nincs tagság"}</small>
          </div>)}
          {preview.preview.errors.map((message,index)=><div className="error-inline" key={index}><AlertTriangle size={16}/>{message}</div>)}
          <button className="primary" type="button" disabled={busy||!preview.token||!preview.preview.isValid}
            onClick={()=>{void applyPreview();}}>Előnézet jóváhagyása és mentése</button>
        </div>}
      </>}
    </RegistryPanel>
    <RegistryPanel title="Új map a meglévő eredeti Store-v1 clusterhez">
      <p className="muted small">A történeti, eredeti öt helyi mapot és a korábbi Manager
        négy Store-metaadatát változatlanul ellenőrzi. A rendszer csak az
        eredeti, forrásból olvasott clusterazonosítót és az igazolt helyi
        transferkönyvtárat fogadhatja el. Ez az ellenőrzés nem indít ASA-t,
        nem hoz létre új clustert és nem módosít karakteradatot.</p>
      <div className="cluster-registry-form">
        <label className="cluster-registry-field">Új, helyi map
          <select aria-label="Bővítendő helyi map" disabled={busy}
            value={expansionMapId}
            onChange={e=>{setExpansionMapId(e.target.value);setExpansion(null);}}>
            <option value="">Válassz egy új mapot</option>
            {localCandidates.map(map=><option key={map.mapId} value={map.mapId}>
              {map.mapName}</option>)}
          </select>
        </label>
        <button className="secondary" type="button"
          disabled={busy||!selectedCluster||!expansionMapId}
          onClick={()=>{void inspectHistoricalExpansion();}}>
          <RefreshCw size={16}/> Eredeti cluster-kompatibilitás ellenőrzése
        </button>
      </div>
      {expansion&&<div className="cluster-membership-preview" role="region"
        aria-label="Történeti Store-map ellenőrzése">
        <strong>{expansion.mapName} – {expansion.requestedClusterId}</strong>
        <div className="cluster-membership-counts">
          <span>Eredeti Store-forrás: {expansion.originalSourceVerified?"igazolt":"nem igazolt"}</span>
          <span>Eredeti 5 map: {expansion.originalFivePreserved?"megőrizve":"nem igazolt"}</span>
          <span>Transfer fizikai azonossága: {expansion.samePhysicalTransferDirectory?"igazolt":"nem igazolt"}</span>
          <span>Helyi profil: {expansion.nativeLocalSetupVerified?"ellenőrizve":"hiányos"}</span>
        </div>
        {expansion.blockers.map(code=><div className="error-inline" key={code}>
          <AlertTriangle size={16}/>{code}</div>)}
        {expansion.localPreflightReady&&<div className="success-inline">
          <CheckCircle2 size={16}/> A helyi, írásmentes előfeltételek megfelelnek.
        </div>}
        <div className="warning-inline"><Shield size={17}/>
          Az új map Store-v1 tagsága és a kétgépes karakterutazás még
          nincs engedélyezve. Ehhez külön writer/session/lock kompatibilitási
          és kétgépes utazási igazolás szükséges.</div>
      </div>}
    </RegistryPanel>
    {notice&&<div className="success-inline" role="status"><CheckCircle2 size={17}/>{notice}</div>}
    {error&&<div className="error-inline" role="alert"><AlertTriangle size={17}/>{error}</div>}
    <div className="warning-inline"><Shield size={17}/> A regisztrált vagy kijelölt távoli host még nem hitelesített peer. A cluster-tagság módosítása nem engedélyez Start, Stop, RCON vagy távoli fájlírás műveletet.</div>
  </div>;
}
