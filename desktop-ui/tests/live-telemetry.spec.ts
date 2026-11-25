import {test,expect} from "@playwright/test";
import {appendPoint,percent,playerTotal,plotSegments,type Point} from "../src/state/liveTelemetry";
const now=Date.parse("2026-11-25T10:00:00Z");
test("telemetry refuses missing or invalid measurements and incomplete local player totals",()=>{
  expect(percent(null)).toBeNull();expect(percent(101)).toBeNull();expect(percent(0)).toBe(0);
  const proof={count:0,utc:new Date(now).toISOString()};
  expect(playerTotal(["a"],{a:proof},now)).toBe(0);
  expect(playerTotal(["a","b"],{a:proof},now)).toBeNull();
  expect(playerTotal(["a"],{a:proof},now+61000)).toBeNull();
  expect(playerTotal(["a"],{a:{...proof,count:-1}},now)).toBeNull();
  expect(playerTotal([],{},now)).toBeNull();
});
test("history is bounded, chronological and separates unavailable intervals",()=>{
  const sample=(time:number,cpu:number|null):Point=>({time,cpu,ram:null,players:null});
  let points:Point[]=[];
  for(let i=0;i<300;i++)points=appendPoint(points,sample(now+i*15000,20));
  expect(points.length).toBe(241);
  expect(appendPoint(points,sample(now,10))).toEqual(points);
  const gaps=[sample(now,10),sample(now+15000,null),sample(now+30000,20),sample(now+120000,30)];
  expect(plotSegments(gaps,"cpu",100)).toHaveLength(3);
  expect(plotSegments(gaps,"ram",100)).toEqual([]);
});
