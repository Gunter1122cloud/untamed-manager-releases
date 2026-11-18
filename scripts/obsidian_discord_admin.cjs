'use strict';
// Pure Discord presentation: existing handlers, permissions and tokens stay private.
// Custom IDs must be provided from the installed adapter's existing routes.
const clean=(x,n=100)=>String(x??'Ismeretlen').replace(/[\u0000-\u001f\u007f@*_~|<>\\\x60]/g,' ').slice(0,n);
const colors={info:0x3498DB,success:0x2ECC71,warning:0xF1C40F,danger:0xE74C3C,maintenance:0x9B59B6};
const labels={start:'Indítás', 'start-api':'Indítás ASA API-val','start-direct':'Indítás API nélkül',stop:'Leállítás',restart:'Újraindítás',save:'Világmentés',wipe:'Vad dínók törlése',rcon:'RCON',backup:'Biztonsági mentés',restore:'Visszaállítás',update:'SteamCMD-frissítés',players:'Játékoslista',points:'Mentési pontok'};
const phases={
 'native-receipt':['Natív nyugta','A Manager átvette a műveletet. A játékbeli eredmény még nem igazolt.','info'],
 'receipt-only':['Kiküldés nyugtázva','A játékbeli eredmény külön ellenőrzendő.','info'],
 'completed-native':['Natív művelet befejezve','A Manager befejezést igazolt; játékbeli eredmény külön ellenőrzendő.','success'],
 'completed':['Natív művelet befejezve','Játékbeli eredmény külön ellenőrzendő.','success'],
 'running':['Folyamatban','A Manager végrehajtása folyamatban.','info'],
 'needs-review':['Ellenőrzés szükséges','Nincs automatikus újraküldés.','warning'],
 'needsOperator':['Ellenőrzés szükséges','Nincs automatikus újraküldés.','warning'],
 'uncertain-no-replay':['Bizonytalan eredmény','Ne ismételd automatikusan a műveletet.','warning'],
 'notDispatched':['Nem került kiküldésre','Új jóváhagyás előtt ellenőrizd az előfeltételeket.','warning'],
 'rejected':['Elutasítva','A művelet nem kapott engedélyt.','danger'],
 'claimed-no-replay':['Feldolgozásra rögzítve','A végrehajtás még nem igazolt.','info']
};
const errors={
 'UM-BACKUP-CONFIRMED-STOP-REQUIRED':'A map fut vagy leállítása nem igazolt. Nem indul automatikus leállítás.',
 'UM-BACKUP-STORE-V1-RESTORE-LOCKED':'Store-v1 éles restore: közös karakterállapotot kezelő tranzakció szükséges.',
 'UM-BACKUP-NATIVE-SAFETY-CHECK-FAILED':'A mentési előfeltételek nem igazoltak.',
 'UM-BACKUP-INSUFFICIENT-RESTORE-CAPACITY':'Nincs elegendő szabad tárhely.',
 'UM-STORE-NATIVE-PORT-IN-USE':'A map portja használatban van; indítás nem engedélyezett.',
 'UM-STEAM-STOPPED-ONLY':'A manuális szerverfrissítés csak igazoltan leállított mapnál engedélyezett.',
 'UM-DC-PROFILE-CHANGED':'A map beállítása megváltozott. Új előnézet szükséges.',
 'UM-DC-HOST-BUSY':'A Manager foglalt. Ellenőrizd az állapotot.',
 'UM-DC-APPROVAL':'Lejárt vagy érvénytelen jóváhagyás.',
 'UM-DC-AUTH':'Nincs jogosultság ehhez a művelethez.'
};
function describeError(code){return errors[code]??'A művelet állapota nem igazolt. Ellenőrizd a Manager naplóját.';}
function buildControlPanel(ids,{hostReady=false,restoreSupported=false}={}){
 const rows=[
 [['start',1,'▶️'],['start-api',3,'🟢'],['start-direct',3,'⚙️'],['stop',4,'⏹️'],['restart',1,'🔄']],
 [['save',1,'💾'],['wipe',4,'🦖'],['rcon',2,'⌨️'],['players',2,'👥']],
 [['backup',2,'📦'],['restore',4,'♻️'],['points',2,'🗂️'],['update',2,'⬆️']]
 ];
 const used=new Set();
 const components=rows.map(row=>({type:1,components:row.map(([action,style,emoji])=>{
   const id=ids?.[action];if(typeof id!=='string'||!id||id.length>100||used.has(id))throw new Error('Missing or duplicate existing action route: '+action);
   used.add(id);return {type:2,custom_id:id,label:labels[action],style,emoji:{name:emoji},disabled:false};
 })}));
 return {embeds:[{title:'OBSIDIAN COMMAND | Szervervezérlés',color:colors.info,description:'Válassz műveletet, majd hostot és mapot. A módosítás előtt külön jóváhagyás szükséges.',fields:[
 {name:'🟢 Szervervezérlés',value:'Indítás · Leállítás · Újraindítás'},
 {name:'🛠️ Adminisztráció',value:'Világmentés · Dino Wipe · RCON · Játékoslista'},
 {name:'📦 Karbantartás',value:'Mentések · Visszaállítás · Manuális frissítés'},
 {name:'Host állapot',value:hostReady?'A kiválasztott host elérhető.':'A hostok állapotát a státuszpanelen ellenőrizd.'},
 {name:'Jogosultság és adatvédelem',value:'Tulajdonos / Admin. A DC MOD nem vezérelhet. '+(restoreSupported?'A visszaállítás hatókörét a Manager ellenőrzi.':'A Store-v1 éles visszaállítás korlátozott.') }
 ],footer:{text:'Untamed Kingdom • A natív előnézet és jóváhagyás kötelező'}}],components,allowedMentions:{parse:[]}};
}
function buildAudit({actorName,host,mapNames,action,state,errorCode,operationId,utc}={}){
 const phase=phases[state]??['Ismeretlen állapot','Az eredmény nincs igazolva.','warning'];
 const names=Array.isArray(mapNames)?mapNames.map(x=>clean(x,80)).join(', '):'Ismeretlen map';
 const fields=[{name:'Admin',value:clean(actorName),inline:true},{name:'Host',value:clean(host),inline:true},{name:'Map(ok)',value:clean(names,950),inline:false},{name:'Eredmény',value:phase[1],inline:false}];
 if(errorCode)fields.push({name:'Ok',value:describeError(errorCode),inline:false});
 const safeId=typeof operationId==='string'&&/^[a-f0-9-]{36}$/i.test(operationId)?operationId:'nem elérhető';
 const embed={title:(labels[action]??'Szerverművelet')+' • '+phase[0],color:colors[phase[2]],fields,footer:{text:'Műveletazonosító: '+safeId}};
 if(typeof utc==='string'&&Number.isFinite(Date.parse(utc)))embed.timestamp=new Date(utc).toISOString();
 return {embeds:[embed],allowedMentions:{parse:[]}};
}
// Provisioning helper: compare installer-selected GUIDs with the authenticated
// remote status inventory. Map count is never fixed and discoveries are not
// registered targets. Missing expected ownership must keep host disabled.
function validateHostInventory(status,{expectedHostId,expectedMapIds=[]}){
 const validGuid=x=>typeof x==='string'&&/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(x)&&!/^0{8}(-0{4}){3}-0{12}$/.test(x);
 if(!expectedHostId||status?.hostId!==expectedHostId||!Array.isArray(status.maps)||
    !Array.isArray(expectedMapIds)||!expectedMapIds.length||!expectedMapIds.every(validGuid))
    return {ok:false,reason:'Host vagy elvárt tulajdonosi leltár nem igazolt'};
 const ids=status.maps.map(m=>m?.mapId);
 if(!ids.every(validGuid)||new Set(ids.map(x=>x.toLowerCase())).size!==ids.length)
    return {ok:false,reason:'Érvénytelen vagy ismétlődő mapazonosító'};
 const present=new Set(ids.map(x=>x.toLowerCase()));
 const missing=expectedMapIds.filter(x=>!present.has(x.toLowerCase()));
 return {ok:missing.length===0,missing,registered:ids.length,discovered:Array.isArray(status.discoveredMaps)?status.discoveredMaps.length:0};
}
module.exports={buildControlPanel,buildAudit,describeError,validateHostInventory};
