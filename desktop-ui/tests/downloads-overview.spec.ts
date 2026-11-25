import {test,expect} from "@playwright/test";
import {checkedPercent,describeDownloads} from "../src/state/downloadsOverview";
test("downloads overview uses provider progress only and never converts batch position to bytes",()=>{
  expect(checkedPercent(null)).toBeNull();expect(checkedPercent(0)).toBe(0);
  expect(checkedPercent(101)).toBeNull();expect(checkedPercent(NaN)).toBeNull();
  const rows=describeDownloads({mapNames:{a:"Astraeos"},rolling:null,single:null,
    fresh:{jobId:"fresh",mapName:"Future DLC",stage:"download",detail:"SteamCMD",percent:12.5,verified:false,registered:false,errorCode:null,evidence:"NATIVE_STEAMCMD_JOB_STATE"},
    batch:{activeJob:{jobId:"batch",state:"running",stage:"update",position:2,total:4,activeMapName:"Astraeos",completedInstallations:1,changedUtc:"2026-11-25T12:00:00Z",serverStopRequested:false,remoteHostTouched:false,storeWriterTouched:false,automaticRetry:false},journalExists:true,journalPhase:"running",journalNextIndex:2,journalTotal:4,needsOperatorReview:false,automaticRetry:false,remoteWritesAllowed:false}});
  expect(rows).toHaveLength(2);expect(rows[0].progress).toBe(12.5);
  expect(rows[1].progress).toBeNull();expect(rows[1].detail).toContain("1 / 4");
});
test("unresolved journal is not represented as a finished download",()=>{
  const rows=describeDownloads({mapNames:{},fresh:null,batch:null,rolling:null,single:{exists:true,phase:"needsOperator",needsOperatorReview:true,autoResumeAllowed:false}});
  expect(rows[0].state).toBe("Eredmény nem igazolt");expect(rows[0].progress).toBeNull();
});
