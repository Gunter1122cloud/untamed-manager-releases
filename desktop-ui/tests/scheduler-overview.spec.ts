import {test,expect} from "@playwright/test";
import {scheduleRows} from "../src/state/schedulerOverview";
import type {OperationsSnapshot,NativeAsaScheduleOverview} from "../src/bridge";
const map={mapId:"map-a",mapName:"Astraeos"} as OperationsSnapshot["maps"][number];
test("scheduler merges local native and inspection records, not retired SteamCheck",()=>{
  const snapshot={maps:[map],schedules:[
    {id:"s1",mapId:"map-a",mapName:"Astraeos",kind:"SteamCheck",frequency:"Interval",enabled:true,nextUtc:"2026-11-25T08:00:00Z",state:"old",autoExecutionPermitted:false},
    {id:"s2",mapId:"map-a",mapName:"Astraeos",kind:"Observation",frequency:"Interval",enabled:true,nextUtc:"2026-11-25T10:00:00Z",state:"ready",autoExecutionPermitted:false}
  ]} as OperationsSnapshot;
  const native={jobs:[{mapId:"map-a",kind:"Restart",enabled:true,nextDueUtc:"2026-11-25T09:00:00Z",lastStatus:"ready"},
    {mapId:"foreign",kind:"Restart",enabled:true,nextDueUtc:"2026-11-25T07:00:00Z",lastStatus:"ready"}]} as NativeAsaScheduleOverview;
  const result=scheduleRows(snapshot,native);
  expect(result.map(x=>x.kind)).toEqual(["Újraindítás","PID ellenőrzés","Korábbi SteamCheck"]);
  expect(result[2].enabled).toBe(false);expect(result[2].nextUtc).toBeNull();
  expect(scheduleRows(snapshot,null)).toHaveLength(2);
});
