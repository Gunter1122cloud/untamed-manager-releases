'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const {buildControlPanel,buildAudit,describeError,validateHostInventory}=require('./obsidian_discord_admin.cjs');
const actions=['start','start-api','start-direct','stop','restart','save','wipe','rcon','players','backup','restore','points','update'];
const ids=Object.fromEntries(actions.map(x=>[x,'existing:'+x]));
test('all controls retain existing route IDs and are grouped',()=>{
 const p=buildControlPanel(ids);const buttons=p.components.flatMap(r=>r.components);
 assert.equal(buttons.length,13);assert.equal(buttons.find(b=>b.custom_id===ids.stop).style,4);
 assert.ok(p.components.every(r=>r.components.length<=5));assert.deepEqual(p.allowedMentions.parse,[]);
 assert.throws(()=>buildControlPanel({}));assert.throws(()=>buildControlPanel({...ids,stop:ids.start}));
});
test('receipt vs actual completion are distinct; no fabricated game effect',()=>{
 const p=buildAudit({actorName:'@everyone',host:'UK-HOME-02',mapNames:['Aberration'],action:'wipe',state:'native-receipt'});
 assert.match(p.embeds[0].title,/nyugta/);assert.match(p.embeds[0].fields[3].value,/még nem igazolt/);
 assert.ok(!p.embeds[0].fields[0].value.includes('@'));
 assert.match(buildAudit({state:'uncertain-no-replay'}).embeds[0].fields[3].value,/Ne ismételd/);
 assert.match(describeError('UM-BACKUP-CONFIRMED-STOP-REQUIRED'),/nem igazolt/);
});
test('dynamic host register validation, no hardcoded five; fail missing or duplicate',()=>{
 const ids=Array.from({length:9},(_,i)=>'00000000-0000-4000-8000-'+String(i+1).padStart(12,'0'));
 const inventory={hostId:'hp',maps:ids.map(mapId=>({mapId})),discoveredMaps:[{pid:12}]};
 assert.equal(validateHostInventory(inventory,{expectedHostId:'hp',expectedMapIds:ids}).registered,9);
 assert.equal(validateHostInventory(inventory,{expectedHostId:'hp',expectedMapIds:ids}).ok,true);
 assert.equal(validateHostInventory({...inventory,maps:inventory.maps.slice(1)},{expectedHostId:'hp',expectedMapIds:ids}).ok,false);
 assert.equal(validateHostInventory({...inventory,maps:[...inventory.maps,inventory.maps[0]]},{expectedHostId:'hp',expectedMapIds:ids}).ok,false);
 assert.equal(validateHostInventory(inventory,{expectedHostId:'wrong',expectedMapIds:ids}).ok,false);
});
