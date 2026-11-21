// Node 22 / discord.js adapter. The caller supplies the EXISTING pinned,
// per-host authenticated transport; never opens a second bot or reads HP files.
import fs from "node:fs/promises";
import path from "node:path";
import {createHash,randomUUID} from "node:crypto";

const phases={claimed:"Feladat lefoglalva",started:"Végrehajtás megkezdődött",
 "native-dispatched":"Natív művelet elfogadva","save-result":"Mentési eredmény","stop-result":"Régi folyamat kilépése",
 "new-process":"Új folyamat megfigyelve",readiness:"Játékosfogadási készültség","wipe-dispatch":"Dinówipe küldése",
 "wipe-result":"Dinówipe eredménye",receipt:"Natív bizonylat",terminal:"Lezárás"};
const operations={Recovery:"Automatikus helyreállítás",Restart:"Újraindítás",SaveWorld:"Világmentés",WildWipe:"Dinówipe",
 Start:"Indítás",Stop:"Leállítás",Backup:"Biztonsági mentés",Restore:"Visszaállítás",Update:"Frissítés",
 restart:"Újraindítás",start:"Indítás",stop:"Leállítás",save:"Világmentés",saveWorld:"Világmentés",
 wipe:"Dinówipe",backup:"Biztonsági mentés",restore:"Visszaállítás",update:"Frissítés",steamUpdate:"Frissítés"};
const evidence={
 LOCAL_SAVE_LOG_WORLD:"Helyi mentési napló és világfájl igazolva; fsync/restore nincs tanúsítva.",
 LOCAL_WORLD_METADATA_LOG_UNVERIFIED:"Világfájl metaadata megváltozott; SaveComplete-log nem igazolt.",
 OLD_PROCESS_STOPPED:"Régi ASA-folyamat kilépése igazolva.",
 NEW_PID_OBSERVED:"Új ASA PID megfigyelve; önmagában nem készültség.",
 OWN_LISTENER_AND_JOIN_ADVERTISED:"Saját listener és aktuális futás Join jelzése igazolva; kliensbelépés nem tanúsított.",
 WIPE_SENT_EFFECT_UNVERIFIED:"Dinówipe-parancs elküldve; játékbeli hatás külön nem igazolt.",
 NATIVE_DISPATCH_UNCERTAIN_NO_REPLAY:"A végrehajtás eredménye bizonytalan; nincs automatikus újraküldés.",
 LOCAL_WORLD_METADATA_LOG_UNVERIFIED_RESTART_READY_WIPE_SENT_EFFECT_UNVERIFIED:"Világfájl-metaadat igazolt, SaveComplete-log hiányzik. Újraindulás és készültség igazolva; dinówipe-parancs elküldve, játékbeli hatás külön nem igazolt.",
 LOCAL_SAVE_LOG_WORLD_RESTART_READY_WIPE_SENT_EFFECT_UNVERIFIED:"Helyi mentési napló és világfájl igazolva. Újraindulás és készültség igazolva; dinówipe-parancs elküldve, játékbeli hatás külön nem igazolt."
};
const clean=v=>String(v??"").replace(/[@`<>\r\n]/g," ").slice(0,350);
const key=(host,id)=>host+":"+id;
const digest=s=>createHash("sha256").update(s).digest("hex");
const nonce=s=>digest(s).slice(0,24);
const terminal=e=>e.phase==="terminal"||e.phase==="receipt";
function severity(e){
 if(/failed|blocked|rejected|denied|error/i.test(e.result))return "red";
 if(/uncertain|limited|interrupted|skipped|missed|unverified/i.test(e.result+" "+e.evidence)||
    /no-fsync-or-client-certification/i.test(e.result))return "yellow";
 return terminal(e)?"green":"blue";
}
const colors={blue:0x3498db,green:0x2ecc71,yellow:0xf1c40f,red:0xe74c3c};
const resultText=r=>/failed|error|rejected|blocked|denied/i.test(r)?"Hiba vagy elutasítás":
 /uncertain|interrupted/i.test(r)?"Bizonytalan, kézi ellenőrzés szükséges":
 /skipped|missed/i.test(r)?"Nem hajtódott végre":
 /limited|no-fsync/i.test(r)?"Korlátozott natív bizonyíték":
 r==="verified-local"?"Helyi natív bizonyíték igazolva":r==="in-progress"?"Folyamatban":"Natív eredmény";
Object.assign(evidence,{
 RECOVERY_FATAL_EXIT_CONFIRMED:"Megtartott folyamat-handle és végzetes kilépési kód, teljes leállás igazolva.",
 RECOVERY_DURABLE_START_CLAIM:"Tartós foglalás. Egyetlen natív Start engedélyezett.",
 RECOVERY_NATIVE_START_DISPATCHED:"Natív Start elfogadva; játékosfogadás még nem igazolt.",
 RECOVERY_RECONCILED_NATIVE_START:"Megszakadt foglaláshoz tartozó pontos natív Start-bizonylat visszaolvasva; újraküldés nincs.",
 RECOVERY_NEW_FAMILY_OBSERVED:"Új ASA/Loader folyamatcsalád megfigyelve; készültség még nem igazolt.",
 RECOVERY_NATIVE_READINESS_PROVEN:"Új folyamat és natív játékosfogadási készültség igazolva; crash előtti mentés és kliensbelépés nem tanúsított.",
 RECOVERY_COOLDOWN_NO_CRASH_LOOP:"30 perces korlát: nincs újabb automatikus indítás.",
 RECOVERY_READINESS_TIMEOUT_NO_RETRY:"Készültségi időtúllépés; kézi ellenőrzés, nincs újraküldés.",
 NATIVE_PREFLIGHT_PASSED:"A natív előellenőrzés sikeres, a végrehajtás megkezdődött.",
 NATIVE_JOB_ACCEPTED:"A natív munkásfeladat elfogadva; eredménye még nem igazolt.",
 RESTART_NEW_PID_READINESS_UNVERIFIED:"Új ASA-folyamat megfigyelve. Játékosfogadási készültség még nem igazolt.",
 START_PROCESS_ONLY_READINESS_UNVERIFIED:"Az indítás folyamatbizonylata megvan; a játékosfogadás még nem igazolt.",
 GRACEFUL_STOP_FINAL_SAVE_UNVERIFIED:"Szabályos leállítás megfigyelve; végső DoExit-mentés és restore külön nem igazolt.",
 SAVEWORLD_DISPATCHED_DISK_COMPLETION_UNVERIFIED:"SaveWorld elküldve; a lemezmentés befejezése nem igazolt.",
 STEAM_BUILD_AND_PROTECTED_FILES_VERIFIED:"Telepített build és védett fájlok helyi ellenőrzése sikeres.",
 BACKUP_BYTES_AND_HASHES_VERIFIED:"Mentett fájlok és hash-ek helyileg igazolva; játékbeli restore nem tanúsított.",
 RESTORE_BYTES_HASHES_VERIFIED_GAME_LOAD_UNVERIFIED:"Visszaállított bájtok és hash-ek igazolva; játékbeli betöltés külön nem igazolt.",
 "UM-SCHEDULE-WIPE-NOT-READY":"Az új ASA készültsége nem igazolt; a wipe nem indítható.",
 "UM-SCHEDULE-WIPE-PID-CHANGED":"Az új ASA PID azonossága megváltozott; a wipe megállt.",
 "UM-SCHEDULE-WIPE-RECEIPT":"A wipe küldési bizonylata nem igazolható; ne ismételd automatikusan."
});
const description=e=>evidence[e.evidence]??(resultText(e.result)+" ("+clean(e.evidence)+").");
const stamp=v=>v&&Number.isFinite(Date.parse(v))?"<t:"+Math.floor(Date.parse(v)/1000)+":F>":"Nem ismert";
function validatePage(page,host,old){
 if(page?.version!==1||page.hostId!==host.id||!Array.isArray(page.events)||
    !Number.isSafeInteger(page.latestSequence)||!Number.isSafeInteger(page.oldestSequence))
   throw Error("EVENT_API_CONTRACT_MISMATCH");
 if(page.gap||page.state==="cursorInvalid"||page.state==="journalChanged"||page.state==="publicationGap")
   throw Error("EVENT_JOURNAL_GAP_"+page.state);
 if(!["ok","notInitialized"].includes(page.state)||typeof page.nextCursor!=="string")throw Error("EVENT_API_UNAVAILABLE");
 if(page.state==="notInitialized"){
   if(page.nextCursor!=="empty"||page.events.length||page.latestSequence!==0||old?.cursor&&old.cursor!=="empty")throw Error("EVENT_JOURNAL_CHANGED");
   return;
 }
 if(!page.epoch||page.nextCursor.split(":")[0]!==String(page.epoch).replaceAll("-",""))throw Error("EVENT_EPOCH_MISMATCH");
 if(!old?.cursor&&page.events.length)throw Error("EVENT_BASELINE_MISMATCH");
 let last=old?.cursor==="empty"?0:Number(old?.cursor?.split(":")[1]??0);
 for(const e of page.events){
   if(!Number.isSafeInteger(e.sequence)||e.sequence!==last+1||
     e.eventId!==String(page.epoch).replaceAll("-","")+":"+e.sequence||
     !/^[0-9a-f-]{36}$/i.test(e.operationId)||!Array.isArray(e.mapIds)||!e.mapIds.length||
     !Number.isFinite(Date.parse(e.eventUtc)))throw Error("EVENT_SEQUENCE_GAP_OR_INVALID");
   last=e.sequence;
 }
 if(Number(page.nextCursor.split(":")[1])!==(old?.cursor?last:page.latestSequence))throw Error("EVENT_CURSOR_MISMATCH");
 if(page.more!==(Number(page.nextCursor.split(":")[1])<page.latestSequence))throw Error("EVENT_HAS_MORE_MISMATCH");
 if(old?.cursor&&old.cursor!=="empty"&&page.nextCursor.split(":")[0]!==old.cursor.split(":")[0])throw Error("EVENT_EPOCH_CHANGED");
}
async function atomic(file,state){
 const tmp=file+"."+randomUUID()+".tmp";
 await fs.mkdir(path.dirname(file),{recursive:true});
 let fd;
 try{
   fd=await fs.open(tmp,"wx",0o600);
   const bytes=JSON.stringify(state);
   if(Buffer.byteLength(bytes)>64*1024*1024)throw Error("EVENT_STATE_LIMIT_REVIEW_REQUIRED");
   await fd.writeFile(bytes);await fd.sync();await fd.close();fd=null;
   await fs.rename(tmp,file);
 }finally{if(fd)await fd.close();await fs.rm(tmp,{force:true});}
}
export class OperationFeed {
 constructor({statePath,hosts,channels,botUserId,request,channelFor,onProblem=()=>{},clock=()=>Date.now()}){
   this.file=statePath;this.hosts=hosts;this.channels=channels;this.bot=botUserId;
   this.request=request;this.channelFor=channelFor;this.onProblem=onProblem;this.clock=clock;
   this.busy=false;this.state=null;
 }
 async load(){
   if(this.state)return;
   try{if((await fs.stat(this.file)).size>64*1024*1024)throw Error("EVENT_STATE_LIMIT_REVIEW_REQUIRED");this.state=JSON.parse(await fs.readFile(this.file,"utf8"));}
   catch(e){if(e.code!=="ENOENT")throw e;this.state={schema:1,hosts:{},runs:{},outbox:{},deliveryCursor:null};}
   if(this.state.schema!==1||!this.state.hosts||!this.state.runs||!this.state.outbox)throw Error("EVENT_STATE_CORRUPT");
 }
 async commit(next){await atomic(this.file,next);this.state=next;}
 // Persist cursor and outbox in the same local atomic replacement BEFORE REST.
 // The caller must run this from a SINGLE bot process with its existing host
 // request exclusion. tick() itself is single-flight.
 async poll(host){
   let old=this.state.hosts[host.id];
   const page=await this.request(host,"events",{cursor:old?.cursor??null,limit:50});
   validatePage(page,host,old);
   if(old?.cursor===page.nextCursor&&!page.events.length&&!old.lastError)return page.more;
   const next=structuredClone(this.state);
   next.hosts[host.id]={...old,cursor:page.nextCursor,observedUtc:new Date(this.clock()).toISOString(),lastError:null};
   for(const event of page.events)this.ingest(next,host,event,false);
   await this.commit(next);
   return page.more;
 }
 ingest(state,host,e,backfill){
   const id=key(host.id,e.operationId);
   let run=state.runs[id];
   if(run&&e.sequence>0&&run.sequence>=e.sequence)return;
   // Recovered terminal receipts retain their original timestamp and run ID.
   // Do not reopen a completed run or generate a duplicate current alert.
   if(run&&run.finishedUtc&&e.historical&&terminal(e))return;
   backfill=backfill||e.historical===true;
   run=run??{hostId:host.id,hostName:host.name,operationId:e.operationId,mapIds:e.mapIds,mapNames:e.mapIds.map((id,i)=>e.mapNames?.[i]??host.mapNames?.[id]??"Ismeretlen map"),
      operation:e.operation,origin:e.origin,actorId:e.actorId,startedUtc:null,finishedUtc:null,sequence:0,phases:{},historical:backfill,scheduledUtc:e.scheduledUtc??null};
   if(e.sequence>0)run.sequence=e.sequence;
   run.latest=e;run.phases[e.phase]=e;
   if(e.phase==="started")run.startedUtc=e.eventUtc;
   if(e.phase==="claimed")run.claimedUtc=e.eventUtc;
   if(e.scheduledUtc)run.scheduledUtc=e.scheduledUtc;
   if(terminal(e))run.finishedUtc=e.eventUtc;
   state.runs[id]=run;
   // One mutable embed per operation, one independent immutable alert per
   // start/end group. Pending edits coalesce; sent targets keep message ID.
   const queue=(kind,channel)=>{
     if(!channel)throw Error("EVENT_CHANNEL_MISSING_"+kind);
     const k=id+":"+kind,previous=state.outbox[k];
     if(kind.startsWith("alert-")&&previous)return;
     state.outbox[k]={...previous,key:k,runId:id,kind,channel,
       status:previous?.status==="sending"||previous?.status==="uncertain"?previous.status:"pending",
       revision:(previous?.revision??0)+1,createdUtc:previous?.createdUtc??new Date(this.clock()).toISOString(),
       marker:previous?.marker??"UK-OP:"+digest(k).slice(0,24)};
   };
   queue("admin",this.channels.admin);
   if(/backup|restore/i.test(e.operation))queue("backup",this.channels.backup);
   if(/update|steam/i.test(e.operation))queue("update",this.channels.update);
   if(!backfill&&e.phase==="started")queue("alert-start",this.channels.alert);
   if(!backfill&&terminal(e))queue("alert-end",this.channels.alert);
 }
 payload(item){
   const run=this.state.runs[item.runId],e=run.latest;
   const start=run.phases.started??run.phases.claimed??e;
   const alert=item.kind==="alert-start";
   const shown=alert?start:e;
   const title=(operations[run.operation]??clean(run.operation))+(alert?" – kezdés":item.kind==="alert-end"?" – lezárás":"");
   const actor=run.origin==="recovery"?"Automatikus helyreállítás":run.origin==="scheduler"?"Automatikus ütemező":run.actorId?"Discord ID: "+run.actorId:run.origin==="manager"?"Manager (név nem ismert)":"Nem ismert";
   const fields=[{name:"Gép",value:clean(run.hostName??run.hostId),inline:true},
     {name:"Map(ok)",value:clean(run.mapNames.join(", ")),inline:true},
     {name:"Végrehajtó",value:actor,inline:true},
     {name:"Indulás",value:stamp(run.startedUtc),inline:true},{name:"Befejezés",value:stamp(alert?null:run.finishedUtc),inline:true},
     {name:"Fázis",value:phases[shown.phase]??clean(shown.phase),inline:true}];
   if(run.scheduledUtc)fields.push({name:"Esedékesség",value:stamp(run.scheduledUtc),inline:true});
   if(run.claimedUtc&&!run.startedUtc)fields.push({name:"Lefoglalás",value:stamp(run.claimedUtc),inline:true});
   if(!alert)for(const p of ["save-result","stop-result","new-process","readiness","wipe-result"]){
     const row=run.phases[p];if(row)fields.push({name:phases[p],value:description(row)});
   }
   const embed={title,description:(run.historical||shown.historical?"Utólagos átvétel · eredeti eseményidő: "+stamp(shown.eventUtc)+"\n":"")+description(shown),
     color:colors[severity(shown)],fields,footer:{text:item.marker+" · "+run.operationId},timestamp:shown.eventUtc};
   return {embeds:[embed],allowedMentions:{parse:[]}};
 }
 async findMessage(channel,item){
   // Reconcile the exact own author and footer marker. A bounded scan may
   // not prove absence; ambiguous sends NEVER trigger a blind resend.
   let before;
   for(let n=0;n<10;n++){
     const collection=await channel.messages.fetch({limit:100,...(before?{before}:{})});
     const rows=[...collection.values()];
     const found=rows.filter(m=>m.author?.id===this.bot&&m.embeds?.some(e=>e.footer?.text?.startsWith(item.marker+" · ")));
     if(found.length>1)throw Error("EVENT_DUPLICATE_DISCORD_MARKER");
     if(found.length)return found[0];
     if(rows.length<100)return null;
     before=rows.at(-1).id;
   }
   return null;
 }
 async updateItem(k,fields){
   const next=structuredClone(this.state);Object.assign(next.outbox[k],fields);await this.commit(next);
 }
 async deliver(k){
   let item=this.state.outbox[k];
   if(!item||item.status==="sent")return;
   if(item.retryAt&&this.clock()<item.retryAt)return;
   const channel=await this.channelFor(item.channel);
   if(!channel?.messages||typeof channel.send!=="function")throw Error("EVENT_CHANNEL_NOT_SENDABLE");
   let message;
   if(item.messageId){
     try{message=await channel.messages.fetch(item.messageId);}
     catch(e){if(e.code!==10008&&e.code!=="10008")throw e;
       // Confirmed unknown-message: do not recreate alerts; repair only the
       // editable admin/backup/update log with a new send intent.
       if(item.kind.startsWith("alert-")){await this.updateItem(k,{status:"sent",deleted:true});return;}
       await this.updateItem(k,{messageId:null,status:"pending",generation:(item.generation??0)+1});item=this.state.outbox[k];
     }
   }
   if(!message&&(item.status==="sending"||item.status==="uncertain")){
     message=await this.findMessage(channel,item);
     if(!message){await this.updateItem(k,{status:"uncertain",problem:"SEND_UNCERTAIN_REVIEW_REQUIRED",retryAt:this.clock()+300_000});return;}
   }
   const payload=this.payload(item);
   if(message){
     if(message.author?.id!==this.bot||!message.embeds?.some(e=>e.footer?.text?.startsWith(item.marker+" · ")))throw Error("EVENT_MESSAGE_IDENTITY_MISMATCH");
     if(!item.kind.startsWith("alert-"))await message.edit(payload);
     await this.updateItem(k,{status:"sent",messageId:message.id,problem:null,retryAt:null,checkedUtc:this.clock()});return;
   }
   await this.updateItem(k,{status:"sending",attemptUtc:new Date(this.clock()).toISOString()});
   try{
     // Discord nonce enforcement is only a bounded server-side dedup aid;
     // it is NOT relied on for exactly-once across arbitrary time gaps.
     message=await channel.send({...payload,nonce:nonce(k+":"+(item.generation??0)),enforceNonce:true});
     await this.updateItem(k,{status:"sent",messageId:message.id,problem:null});
   }catch(e){
     const status=e.status??e.httpStatus;
     if(status===429||status===403||status===400){
       await this.updateItem(k,{status:"pending",retryAt:this.clock()+Math.max(30_000,Number(e.retry_after??0)*1000),problem:"DISCORD_HTTP_"+status});
     }else await this.updateItem(k,{status:"uncertain",problem:"DISCORD_SEND_UNCERTAIN",retryAt:this.clock()+60_000});
     throw e;
   }
 }
 // Optional initial historical import: explicit invocation only, no alerts.
 // A live stream event outranks historical summary for the same run.
 async backfill(host,{maxPages=5}={}){
   if(this.busy)throw Error("EVENT_FEED_BUSY");this.busy=true;
   try{
     await this.load();let after=this.state.hosts[host.id]?.historyCursor??null;
     for(let i=0;i<maxPages;i++){
       const page=await this.request(host,"scheduleHistory",{afterRunId:after,limit:50});
       if(page.version!==1||page.hostId!==host.id||page.gap||page.state!=="ok"||!Array.isArray(page.runs))throw Error("EVENT_HISTORY_GAP");
       const next=structuredClone(this.state);next.hosts[host.id]??={};
       for(const row of page.runs){
         const existing=next.runs[key(host.id,row.runId)];
         if(existing&&(!row.finishedUtc||existing.finishedUtc))continue;
         const e={operationId:row.runId,mapIds:[row.mapId],mapNames:[row.currentMapName],operation:row.kind,origin:"scheduler",sequence:0,
           eventUtc:row.finishedUtc??row.claimedUtc,scheduledUtc:row.scheduledUtc,historical:true,phase:row.finishedUtc?"terminal":"claimed",result:row.status,evidence:row.evidence};
         this.ingest(next,host,e,true);
         const run=next.runs[key(host.id,row.runId)];
         if(!existing)run.startedUtc=null;run.claimedUtc=row.claimedUtc;
       }
       if(page.retentionMayBeTruncated)this.onProblem(host.id,"EVENT_HISTORY_RETENTION_MAY_BE_TRUNCATED");
       next.hosts[host.id].historyCursor=page.nextRunId;await this.commit(next);
       after=page.nextRunId;if(!page.more)break;
     }
   }finally{this.busy=false;}
 }
 // Poll host failures independently; no silent cursor reset. Delivery errors
 // retain the durable outbox. Existing bot decides how to display problems.
 async tick(){
   if(this.busy)return;this.busy=true;
   try{
     await this.load();
     for(const h of this.hosts){
       try{for(let i=0;i<4;i++)if(!await this.poll(h))break;}
       catch(e){
         const code=clean(e.message);this.onProblem(h.id,code);
         const next=structuredClone(this.state);next.hosts[h.id]={...next.hosts[h.id],lastError:code};
         try{await this.commit(next);}catch(write){this.onProblem(h.id,clean(write.message));}
       }
     }
     // Stable circular order, including a persisted checkpoint. A channel
     // failure never pins the head of the queue. All failure paths, not
     // just REST send, receive bounded retry delay.
     const keys=Object.keys(this.state.outbox).sort();
     const pivot=keys.findIndex(k=>k>(this.state.deliveryCursor??""));
     const ordered=pivot<0?keys:[...keys.slice(pivot),...keys.slice(0,pivot)];
     const eligible=ordered.filter(k=>this.state.outbox[k].status!=="sent"&&
       (!this.state.outbox[k].retryAt||this.state.outbox[k].retryAt<=this.clock())).slice(0,20);
     for(const k of eligible){
       let error=null;
       try{await this.deliver(k);if(this.state.outbox[k]?.status==="uncertain")this.onProblem(k,"SEND_UNCERTAIN_REVIEW_REQUIRED");}
       catch(e){error=clean(e.message);this.onProblem(k,error);}
       const next=structuredClone(this.state),item=next.outbox[k];
       next.deliveryCursor=k;
       if(error&&item.status!=="sent"){
         item.failureCount=Math.min(10,(item.failureCount??0)+1);
         item.retryAt=Math.max(item.retryAt??0,this.clock()+Math.min(900_000,30_000*2**(item.failureCount-1)));
         item.problem=item.problem??"DELIVERY_FAILED";
       }else if(!error&&item.status==="sent"){item.failureCount=0;item.retryAt=null;}
       // If storage itself fails, stop instead of operating against a
       // cursor/outbox checkpoint we could not durably acknowledge.
       await this.commit(next);
     }
     // Bounded audit of recently delivered mutable messages; deleted
     // embeds are repaired, but deleted historical alerts never re-alert.
     const due=Object.values(this.state.outbox).filter(x=>x.status==="sent"&&!x.kind.startsWith("alert-")&&x.messageId&&this.clock()-(x.checkedUtc??0)>3600_000)
       .sort((a,b)=>(a.checkedUtc??0)-(b.checkedUtc??0)).slice(0,2);
     for(const item of due){
       try{
         const channel=await this.channelFor(item.channel);
         const message=await channel.messages.fetch(item.messageId);
         if(message.author?.id!==this.bot||!message.embeds?.some(e=>e.footer?.text?.startsWith(item.marker+" · ")))throw Error("EVENT_MESSAGE_IDENTITY_MISMATCH");
         await this.updateItem(item.key,{checkedUtc:this.clock()});
       }catch(e){
         if(e.code===10008||e.code==="10008")await this.updateItem(item.key,{status:"pending",messageId:null,generation:(item.generation??0)+1,checkedUtc:this.clock()});
         else this.onProblem(item.key,clean(e.message));
       }
     }
   }finally{this.busy=false;}
 }
}
