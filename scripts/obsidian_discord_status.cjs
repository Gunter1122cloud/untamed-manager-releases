'use strict';
// Pure display-only adapter. No Discord login, network or control endpoints.
// State contains public IDs/labels and status only; never credentials.
const clean = (x,n=80) => String(x??'?').replace(/[\u0000-\u001f\u007f]/g,' ').replace(/[@\x60*_~|<>\\]/g,' ').slice(0,n);
const finite = x => typeof x==='number' && Number.isFinite(x);
const count = x => Number.isSafeInteger(x)&&x>=0 ? x : null;
const mark = m => m.running===true && m.confirmedStopped!==true ? '🟢' :
    m.running===false && m.confirmedStopped===true ? '🔴' : '⚪';
const guid = x => typeof x==='string'&&/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(x);
const stateKey = s => JSON.stringify([s.label,s.status?.hostId??null]);
const discoveryKey = m => JSON.stringify([m.pid,m.startedUtc??null,m.processKind??'asaServer']);
function buildStatusPanels(snapshots,previous={}) {
    if(!Array.isArray(snapshots))throw new TypeError('snapshots');
    const nextState=Object.assign(Object.create(null),previous),alerts=[],rows=[];
    let total=0,online=0,knownPlayers=0,unknownPlayers=0,unavailable=0,discovered=0;
    for(const snap of snapshots) {
        const label=clean(snap.label),status=snap.status;
        if(!status||!Array.isArray(status.maps)||
            status.maps.some(m=>!m||!guid(m.mapId))||
            new Set(status.maps.map(m=>m.mapId.toLowerCase())).size!==status.maps.length) {
            unavailable++;rows.push({label,line:'⚪ Host nem elérhető · állapot ismeretlen'});continue;
        }
        const key=stateKey(snap),old=previous[key],maps=Object.create(null);
        for(const m of status.maps) {
            const id=m.mapId.toLowerCase(),name=clean(m.mapName),symbol=mark(m);
            const pc=count(m.playerCount),running=symbol==='🟢';
            total++;if(running){online++;if(pc===null)unknownPlayers++;else knownPlayers+=pc;}
            maps[id]={name,symbol};
            if(old && !old.maps?.[id])alerts.push(label+': Új regisztrált map · '+name);
            else if(old?.maps?.[id] && old.maps[id].symbol!==symbol)alerts.push(label+': '+name+' · állapotváltozás '+symbol);
            const live=Array.isArray(m.liveModOrder)?m.liveModOrder:null;
            const next=Array.isArray(m.nextModOrder)?m.nextModOrder:null;
            const passive=Array.isArray(m.livePassiveModOrder)?m.livePassiveModOrder:null;
            const nextPassive=Array.isArray(m.nextPassiveModOrder)?m.nextPassiveModOrder:null;
            const equal=live&&next&&passive&&nextPassive&&JSON.stringify([live,passive])===JSON.stringify([next,nextPassive]);
            const mods=m.modChangesPending===true?' · modterv függőben':
                live&&next&&!equal?' · modlista eltér':live?' · '+live.length+' indítási mod':'';
            const mode=m.useApi===true?'API':m.useApi===false?'ASA':'?';
            rows.push({label,line:symbol+' '+name+' · '+(running&&pc!==null?pc:'?')+
                ' fő · '+mode+' · RAM '+(finite(m.memoryMiB)?(m.memoryMiB/1024).toFixed(1):'?')+
                ' GiB · CPU '+(finite(m.cpuPercent)?m.cpuPercent.toFixed(1):'?')+'%'+mods+
                (symbol==='⚪'&&m.status?' · '+clean(m.status,120):'')});
        }
        let observations=old?.observations??null;
        if(status.discoveryFresh===true&&Array.isArray(status.discoveredMaps)) {
            const current=Object.create(null);
            for(const m of status.discoveredMaps) {
                if(!m||!Number.isSafeInteger(m.pid)||m.pid<=0)continue;
                const id=discoveryKey(m),name=clean(m.mapKey),port=Number.isInteger(m.gamePort)?' :'+m.gamePort:'';
                if(current[id])continue;
                current[id]=name;discovered++;
                if(observations&&!observations[id])alerts.push(label+': Új felderített folyamat · '+name+port+' · csak megfigyelés');
                rows.push({label,line:'⚪ '+name+port+' · '+(m.processKind==='apiLoader'?'Loader észlelve; ASA még nem igazolt':'ASA folyamat észlelve')+' · csak megfigyelés'});
            }
            observations=current;
        } else rows.push({label,line:'⚪ Folyamatfelderítés nem igazolt'});
        if(!status.maps.length&&!status.discoveredMaps?.length)rows.push({label,line:'Nincs regisztrált map'});
        nextState[key]={maps,observations};
    }
    const description=online+'/'+total+' regisztrált map fut · '+knownPlayers+' igazolt játékos · '+
        unknownPlayers+' ismeretlen játékosszám · '+discovered+' külön megfigyelés · '+unavailable+' elérhetetlen host';
    // Discord embed constraints: preserve all rows using additional messages.
    // One field per row means no partial/truncated map; 15 fields and 4800
    // chars leave headroom below Discord's 25-field / 6000-char limits.
    const pages=[];let fields=[],used=0;
    const flush=()=>{pages.push({title:'Untamed Kingdom · Obsidian Status',description,color:0x3584E4,fields});fields=[];used=0;};
    for(const {label,line} of rows) {
        if(fields.length>=15||used+label.length+line.length>4800)flush();
        fields.push({name:label||'Host',value:line||'?',inline:false});used+=label.length+line.length;
    }
    if(fields.length||!pages.length)flush();
    pages.forEach((page,i)=>page.footer={text:'Automatikus frissítés · '+(i+1)+'/'+pages.length+' · ismeretlen ≠ nulla'});
    return {pages,alerts,nextState,summary:{total,online,knownPlayers,unknownPlayers,discovered,unavailable}};
}
module.exports={buildStatusPanels};
