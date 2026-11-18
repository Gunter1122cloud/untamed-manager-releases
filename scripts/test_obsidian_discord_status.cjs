'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {buildStatusPanels}=require('./obsidian_discord_status.cjs');
const map=(n,extra={})=>({mapId:'00000000-0000-4000-8000-'+String(n).padStart(12,'0'),mapName:'Map '+n,running:true,confirmedStopped:false,playerCount:0,useApi:true,...extra});
const snap=(maps,discoveredMaps=[],fresh=true)=>[{label:'UK-HOME-02',status:{hostId:'ryzen',maps,discoveryFresh:fresh,discoveredMaps}}];
test('six maps, same-level event observation and future registration',()=>{
    const first=buildStatusPanels(snap(Array.from({length:5},(_,i)=>map(i+1))));
    const event={pid:999,startedUtc:'2026-11-18T10:00:00Z',mapKey:'Ragnarok_WP',gamePort:7999,processKind:'asaServer'};
    const second=buildStatusPanels(snap(Array.from({length:6},(_,i)=>map(i+1)),[event]),first.nextState);
    assert.equal(second.summary.total,6);assert.equal(second.summary.discovered,1);
    assert.equal(second.alerts.length,2);assert.match(second.pages[0].fields.at(-1).value,/7999/);
    assert.equal(buildStatusPanels(snap(Array.from({length:6},(_,i)=>map(i+1)),[event]),second.nextState).alerts.length,0);
});
test('failed host or stale discovery does not imply deletion or reappearance',()=>{
    const event={pid:10,startedUtc:'a',mapKey:'Event_WP'};
    const first=buildStatusPanels(snap([map(1)], [event]));
    const off=buildStatusPanels([{label:'UK-HOME-02',status:null}],first.nextState);
    assert.equal(off.summary.unavailable,1);
    const stale=buildStatusPanels(snap([map(1)],[],false),off.nextState);
    assert.equal(buildStatusPanels(snap([map(1)],[event]),stale.nextState).alerts.length,0);
});
test('all 72 registered maps render across bounded pages, no silent truncation',()=>{
    const result=buildStatusPanels(snap(Array.from({length:72},(_,i)=>map(i+1))));
    assert.equal(result.pages.flatMap(p=>p.fields).length,72);
    assert.equal(result.summary.online,72);
    for(const p of result.pages){assert.ok(p.fields.length<=25);assert.ok(p.fields.reduce((n,f)=>n+f.name.length+f.value.length,0)+p.description.length+p.title.length+p.footer.text.length<=6000);}
});
test('unknown PID, contradictory status and Loader-only are never counted online',()=>{
    const result=buildStatusPanels(snap([map(1,{running:false,confirmedStopped:false,playerCount:null}),map(2,{running:true,confirmedStopped:true,playerCount:8}),map(3,{playerCount:null})],[{pid:77,mapKey:'Next_WP',processKind:'apiLoader'}]));
    assert.equal(result.summary.online,1);assert.equal(result.summary.unknownPlayers,1);assert.equal(result.summary.knownPlayers,0);
    assert.match(result.pages[0].fields.at(-1).value,/Loader/);
});
test('pending draft and drift are separate; unsafe mentions stripped',()=>{
    const result=buildStatusPanels(snap([map(1,{mapName:'@everyone',liveModOrder:['1'],nextModOrder:['2'],livePassiveModOrder:[],nextPassiveModOrder:[],modChangesPending:true}),map(2,{liveModOrder:['2'],nextModOrder:['1'],livePassiveModOrder:[],nextPassiveModOrder:[]})]));
    const lines=result.pages[0].fields.map(x=>x.value);
    assert.ok(!lines[0].includes('@'));assert.match(lines[0],/modterv/);assert.match(lines[1],/modlista eltér/);
});
