import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import {randomUUID} from "node:crypto";
import {OperationFeed} from "./discord-operation-feed.mjs";

async function lab(t){
 const folder=await fs.mkdtemp(path.join(os.tmpdir(),"obsidian-events-"));
 t.after(()=>fs.rm(folder,{recursive:true,force:true}));
 let now=Date.parse("2026-11-19T03:00:00Z"),serial=0;
 const hosts=["Ryzen","HP"].map(name=>({id:randomUUID(),name,epoch:randomUUID(),rows:[],history:[],mapNames:{}}));
 const problems=[],channels={admin:"admin",alert:"alert",backup:"backup",update:"update"};
 const sinks=new Map(),requests=[];
 for(const name of Object.values(channels)){
   const messages=new Map();
   const sink={messages:{async fetch(arg){
       if(typeof arg==="object")return new Map([...messages].reverse().slice(0,arg.limit));
       if(!messages.has(arg))throw Object.assign(Error("Unknown message"),{code:10008});return messages.get(arg);
     }},sent:0,edits:0,rows:messages,fail:null,
     async send(payload){
       if(this.fail==="429"){this.fail=null;throw Object.assign(Error("rate"),{status:429});}
       const id=String(++serial),m={id,author:{id:"bot"},embeds:payload.embeds,async edit(p){sink.edits++;this.embeds=p.embeds;return this;}};
       messages.set(id,m);this.sent++;
       if(this.fail==="timeout-after-send"){this.fail=null;throw Error("timeout");}
       return m;
     }};
   sinks.set(name,sink);
 }
 const request=async(host,kind,args)=>{
   requests.push({host:host.id,kind,args});
   if(host.error)throw Error(host.error);
   if(kind==="scheduleHistory")return {version:1,hostId:host.id,state:"ok",gap:false,more:false,nextRunId:host.history.at(-1)?.runId??null,runs:host.history};
   assert.equal(kind,"events");
   const prefix=host.epoch.replaceAll("-","");
   const after=args.cursor==null?host.rows.length:args.cursor==="empty"?0:Number(args.cursor.split(":")[1]);
   const rows=host.rows.slice(after,after+args.limit);
   return {version:1,hostId:host.id,epoch:host.epoch,oldestSequence:1,latestSequence:host.rows.length,
      state:host.gap?"cursorInvalid":"ok",gap:!!host.gap,more:after+rows.length<host.rows.length,
      nextCursor:prefix+":"+(after+rows.length),events:rows};
 };
 function evt(host,operationId,phase,result,evidence,mapId,origin="scheduler",operation="Restart"){
   const sequence=host.rows.length+1;host.mapNames[mapId]??="Fixture "+mapId.slice(0,4);
   host.rows.push({sequence,eventId:host.epoch.replaceAll("-","")+":"+sequence,operationId,mapIds:[mapId],operation,origin,actorId:origin==="discord"?"123456789012345678":null,
     eventUtc:new Date(now+sequence*1000).toISOString(),phase,result,evidence});
 }
 const args={statePath:path.join(folder,"state.json"),hosts,channels,botUserId:"bot",request,
  channelFor:async id=>sinks.get(id),onProblem:(id,e)=>problems.push([id,e]),clock:()=>now};
 return {hosts,sinks,requests,problems,evt,args,advance:n=>{now+=n},make:()=>new OperationFeed(args)};
}
test("five complete restarts between polls, two hosts, one embed and two alerts per run, no replay",async t=>{
 const l=await lab(t),[a,b]=l.hosts;let feed=l.make();await feed.tick();
 for(let i=0;i<5;i++){
   const run=randomUUID(),map=randomUUID();
   for(const [phase,res,ev] of [["claimed","in-progress","DURABLE_SLOT_CLAIMED_NO_REPLAY"],["started","in-progress","NATIVE_PREFLIGHT_PASSED"],
     ["save-result","limited","LOCAL_WORLD_METADATA_LOG_UNVERIFIED"],["stop-result","verified-local","OLD_PROCESS_STOPPED"],
     ["new-process","verified-local","NEW_PID_OBSERVED"],["readiness","verified-local","OWN_LISTENER_AND_JOIN_ADVERTISED"],
     ["wipe-result","limited","WIPE_SENT_EFFECT_UNVERIFIED"],["terminal","executed-no-fsync-or-client-certification","LOCAL_WORLD_METADATA_LOG_UNVERIFIED_RESTART_READY_WIPE_SENT_EFFECT_UNVERIFIED"]])
       l.evt(a,run,phase,res,ev,map);
 }
 const op=randomUUID(),map=randomUUID();
 l.evt(b,op,"claimed","in-progress","DISCORD_DURABLE_CLAIM",map,"discord");
 l.evt(b,op,"started","in-progress","NATIVE_RESTART_EXECUTION_STARTED",map,"discord");
 l.evt(b,op,"terminal","limited","RESTART_NEW_PID_READINESS_UNVERIFIED",map,"discord");
 await feed.tick();
 assert.equal(l.sinks.get("admin").sent,6);assert.equal(l.sinks.get("alert").sent,12);
 assert.equal(Object.keys(feed.state.runs).length,6);
 for(const m of l.sinks.get("admin").rows.values())assert.equal(m.embeds[0].color,0xf1c40f);
 assert.equal([...l.sinks.get("admin").rows.values()].filter(m=>m.embeds[0].description.includes("játékbeli hatás külön nem igazolt")).length,5);
 feed=l.make();await feed.tick();
 assert.equal(l.sinks.get("admin").sent,6);assert.equal(l.sinks.get("alert").sent,12);
});
test("REST timeout after actual send survives restart by exact own marker; confirmed deletion repaired without repeat alerts",async t=>{
 const l=await lab(t),[a]=l.hosts;let feed=l.make();await feed.tick();
 const run=randomUUID(),map=randomUUID();
 l.evt(a,run,"started","in-progress","NATIVE_EXECUTION_STARTED",map);
 l.evt(a,run,"terminal","limited","WIPE_SENT_EFFECT_UNVERIFIED",map);
 l.sinks.get("admin").fail="timeout-after-send";
 await feed.tick();assert.equal(l.sinks.get("admin").sent,1);
 const adminKey=Object.keys(feed.state.outbox).find(k=>k.endsWith(":admin"));
 assert.equal(feed.state.outbox[adminKey].status,"uncertain");
 l.advance(61_000);feed=l.make();await feed.tick();
 assert.equal(feed.state.outbox[adminKey].status,"sent");assert.equal(l.sinks.get("admin").sent,1);
 const id=feed.state.outbox[adminKey].messageId;l.sinks.get("admin").rows.delete(id);
 l.advance(3601_000);await feed.tick();await feed.tick();
 assert.equal(l.sinks.get("admin").sent,2);assert.equal(l.sinks.get("alert").sent,2);
});
test("429 retry, offline host and cursor gap never reset cursor or block healthy peer",async t=>{
 const l=await lab(t),[a,b]=l.hosts;const feed=l.make();await feed.tick();
 const before=feed.state.hosts[a.id].cursor;a.error="TLS_PIN_MISMATCH";
 l.evt(b,randomUUID(),"started","in-progress","BACKUP_EXECUTION_ATTEMPT",randomUUID(),"manager","Backup");
 l.sinks.get("backup").fail="429";await feed.tick();
 assert.equal(feed.state.hosts[a.id].cursor,before);assert.equal(l.sinks.get("admin").sent,1);assert.equal(l.sinks.get("backup").sent,0);
 a.error=null;a.gap=true;l.advance(31_000);await feed.tick();
 assert.equal(feed.state.hosts[a.id].cursor,before);assert.equal(l.sinks.get("backup").sent,1);
 assert.ok(l.problems.some(([,e])=>e.includes("GAP")));
});
test("explicit historical terminal backfill retains original timestamps, never sends current alerts",async t=>{
 const l=await lab(t),[a]=l.hosts;const feed=l.make();await feed.tick();
 a.history.push({runId:randomUUID(),mapId:randomUUID(),kind:"Restart",scheduledUtc:"2026-11-19T03:00:00Z",
   claimedUtc:"2026-11-19T03:00:01Z",finishedUtc:"2026-11-19T03:03:38Z",status:"executed-no-fsync-or-client-certification",
   evidence:"LOCAL_WORLD_METADATA_LOG_UNVERIFIED_RESTART_READY_WIPE_SENT_EFFECT_UNVERIFIED"});
 await feed.backfill(a);await feed.tick();assert.equal(l.sinks.get("alert").sent,0);assert.equal(l.sinks.get("admin").sent,1);
 const m=[...l.sinks.get("admin").rows.values()][0];assert.ok(m.embeds[0].description.includes("Utólagos átvétel"));
 assert.equal(m.embeds[0].timestamp,a.history[0].finishedUtc);
 assert.ok(m.embeds[0].fields.find(x=>x.name==="Indulás").value==="Nem ismert");
 assert.equal(feed.state.hosts[a.id].historyCursor,a.history[0].runId);
});

test("recovered native terminal retains original finish without current alerts or a duplicate embed",async t=>{
 const l=await lab(t),[a]=l.hosts;const feed=l.make();await feed.tick();
 const run=randomUUID(),map=randomUUID();
 l.evt(a,run,"started","in-progress","NATIVE_PREFLIGHT_PASSED",map);
 await feed.tick();assert.equal(l.sinks.get("admin").sent,1);assert.equal(l.sinks.get("alert").sent,1);
 l.evt(a,run,"terminal","executed-no-fsync-or-client-certification","LOCAL_WORLD_METADATA_LOG_UNVERIFIED_RESTART_READY_WIPE_SENT_EFFECT_UNVERIFIED",map);
 a.rows.at(-1).historical=true;
 const finished=a.rows.at(-1).eventUtc;
 await feed.tick();assert.equal(l.sinks.get("admin").sent,1);assert.equal(l.sinks.get("alert").sent,1);
 assert.equal(Object.values(feed.state.runs)[0].finishedUtc,finished);
 assert.ok([...l.sinks.get("admin").rows.values()][0].embeds[0].description.includes("Utólagos átvétel"));
 l.evt(a,run,"terminal","executed-no-fsync-or-client-certification","LOCAL_WORLD_METADATA_LOG_UNVERIFIED_RESTART_READY_WIPE_SENT_EFFECT_UNVERIFIED",map);
 a.rows.at(-1).historical=true;
 await feed.tick();assert.equal(l.sinks.get("admin").sent,1);assert.equal(l.sinks.get("alert").sent,1);
 assert.equal(Object.values(feed.state.runs)[0].finishedUtc,finished);
});

test("twenty or more inaccessible channel items do not starve later healthy alerts across ticks and restarts",async t=>{
 const l=await lab(t),[a]=l.hosts;let feed=l.make();await feed.tick();
 // Construct same host/run keys so the twenty failed items sort before a healthy run.
 for(let i=0;i<24;i++){
   const run="00000000-0000-4000-8000-"+String(i+1).padStart(12,"0");
   l.evt(a,run,"terminal","limited","BACKUP_BYTES_AND_HASHES_VERIFIED",randomUUID(),"manager","Backup");
 }
 const healthy="ffffffff-ffff-4fff-8fff-ffffffffffff";
 l.evt(a,healthy,"started","in-progress","NATIVE_PREFLIGHT_PASSED",randomUUID());
 const map=l.args.channelFor;
 l.args.channelFor=async id=>{if(id==="admin"||id==="backup")throw Error("CHANNEL_UNAVAILABLE");return map(id);};
 feed=l.make();await feed.tick();
 const failed=Object.values(feed.state.outbox).filter(x=>x.failureCount>0);
 assert.ok(failed.length>0&&failed.every(x=>x.retryAt>l.args.clock()));
 const cursor=feed.state.deliveryCursor;assert.ok(cursor);
 for(let i=0;i<5;i++){feed=l.make();await feed.tick();}
 const alert=Object.values(feed.state.outbox).find(x=>x.runId.endsWith(healthy)&&x.kind==="alert-start");
 assert.equal(alert.status,"sent");assert.ok(l.sinks.get("alert").sent>0);
 assert.ok(Object.values(feed.state.outbox).some(x=>x.failureCount===1&&x.status!=="sent"));
});

test("recovery on two hosts: stable independent run identity, separate phases and readiness-only success",async t=>{
 const l=await lab(t);let feed=l.make();await feed.tick();
 const sameRun=randomUUID();
 for(const [i,h] of l.hosts.entries()){
   const map=randomUUID();
   for(const [phase,result,evidence] of [["claimed","in-progress","RECOVERY_FATAL_EXIT_CONFIRMED"],["started","in-progress","RECOVERY_DURABLE_START_CLAIM"],["native-dispatched","in-progress","RECOVERY_NATIVE_START_DISPATCHED"],["new-process","in-progress","RECOVERY_NEW_FAMILY_OBSERVED"]])l.evt(h,sameRun,phase,result,evidence,map,"recovery","Recovery");
   h.recoveryMap=map;
 }
 await feed.tick();assert.equal(l.sinks.get("admin").sent,2);assert.equal(l.sinks.get("alert").sent,2);
 for(const m of l.sinks.get("admin").rows.values())assert.notEqual(m.embeds[0].color,0x2ecc71);
 const [a,b]=l.hosts;
 l.evt(a,sameRun,"readiness","verified-local","RECOVERY_NATIVE_READINESS_PROVEN",a.recoveryMap,"recovery","Recovery");
 l.evt(a,sameRun,"terminal","verified-local","RECOVERY_NATIVE_READINESS_PROVEN",a.recoveryMap,"recovery","Recovery");
 l.evt(b,sameRun,"terminal","uncertain","RECOVERY_READINESS_TIMEOUT_NO_RETRY",b.recoveryMap,"recovery","Recovery");
 feed=l.make();await feed.tick();assert.equal(l.sinks.get("admin").sent,2);assert.equal(l.sinks.get("alert").sent,4);
 const admin=[...l.sinks.get("admin").rows.values()].map(m=>m.embeds[0]);assert.equal(admin.filter(e=>e.color===0x2ecc71).length,1);
 assert.ok(admin.every(e=>e.fields.find(f=>f.name==="Végrehajtó").value==="Automatikus helyreállítás"));
 feed=l.make();await feed.tick();assert.equal(l.sinks.get("admin").sent,2);assert.equal(l.sinks.get("alert").sent,4);
});

test("never-settling host read cannot freeze feed or duplicate in-flight reads",async t=>{
 const l=await lab(t),[a,b]=l.hosts;
 const feed=new OperationFeed({...l.args,ioTimeoutMs:50});await feed.tick();
 const before=feed.state.hosts[a.id].cursor,original=feed.request;
 let attempts=0,release;
 const hanging=new Promise(resolve=>{release=resolve;});
 feed.request=async(host,kind,args)=>{
   if(host.id===a.id){attempts++;await hanging;}
   return original(host,kind,args);
 };
 l.evt(a,randomUUID(),"terminal","limited","BACKUP_BYTES_AND_HASHES_VERIFIED",randomUUID(),"manager","Backup");
 l.evt(b,randomUUID(),"started","in-progress","NATIVE_PREFLIGHT_PASSED",randomUUID());
 await feed.tick();
 assert.equal(feed.busy,false);assert.equal(feed.state.hosts[a.id].cursor,before);
 assert.equal(l.sinks.get("admin").sent,1);assert.equal(attempts,1);
 await feed.tick();assert.equal(attempts,1);assert.equal(feed.busy,false);
 release();await new Promise(resolve=>setImmediate(resolve));
 await feed.tick();assert.equal(attempts,2);assert.notEqual(feed.state.hosts[a.id].cursor,before);
});
test("hung Discord send stays uncertain, does not block other channels and is not sent twice",async t=>{
 const l=await lab(t),[a]=l.hosts;
 const feed=new OperationFeed({...l.args,ioTimeoutMs:50});await feed.tick();
 const admin=l.sinks.get("admin"),original=admin.send.bind(admin);
 let sends=0,release;
 const waiting=new Promise(resolve=>{release=resolve;});
 admin.send=async payload=>{sends++;await waiting;return original(payload);};
 l.evt(a,randomUUID(),"started","in-progress","NATIVE_PREFLIGHT_PASSED",randomUUID());
 await feed.tick();
 const adminKey=Object.keys(feed.state.outbox).find(k=>k.endsWith(":admin"));
 assert.equal(feed.state.outbox[adminKey].status,"uncertain");
 assert.equal(l.sinks.get("alert").sent,1);assert.equal(feed.busy,false);
 l.advance(61_000);await feed.tick();assert.equal(sends,1);
 release();await new Promise(resolve=>setImmediate(resolve));
 l.advance(301_000);await feed.tick();
 assert.equal(feed.state.outbox[adminKey].status,"sent");
 assert.equal(admin.sent,1);assert.equal(sends,1);
});
