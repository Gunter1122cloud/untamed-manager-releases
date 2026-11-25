import {test,expect} from "@playwright/test";
import {parsePlayerList,observePlayerList,type PlayerHistory} from "../src/state/playerActivity";
const map="12345678-1234-1234-1234-123456789abc";
const player={id:"0123456789abcdef",name:"Name, with comma"};
test("player list refuses malformed or duplicate incomplete observations",()=>{
  expect(parsePlayerList("No Players Connected")).toEqual([]);
  expect(parsePlayerList("0. Name, with comma, 0123456789abcdef")).toEqual([player]);
  expect(parsePlayerList("1. Name, with comma, 0123456789abcdef")).toEqual([player]);
  expect(parsePlayerList("0. A, 0123456789abcdef\n2. B, 1123456789abcdef")).toBeNull();
  expect(parsePlayerList("0. A, 0123456789abcdef\n1. B, 0123456789abcdef")).toBeNull();
  expect(parsePlayerList("partial")).toBeNull();
});
test("observed online estimate never accumulates unknown gaps or restart interval",()=>{
  const t=Date.parse("2026-11-25T12:00:00Z");
  const a={utc:t,players:[player]},b={utc:t+45000,players:[player]};
  let h:PlayerHistory=observePlayerList({records:[]},map,undefined,a);
  h=observePlayerList(h,map,a,b);expect(h.records[0].seconds).toBe(45);
  const c={utc:t+180000,players:[player]};h=observePlayerList(h,map,b,c);expect(h.records[0].seconds).toBe(45);
  const d={utc:t+225000,players:[player]};h=observePlayerList(h,map,undefined,d);expect(h.records[0].seconds).toBe(45);
  const empty={utc:t+270000,players:[]};h=observePlayerList(h,map,d,empty);
  h=observePlayerList(h,map,empty,{utc:t+315000,players:[player]});expect(h.records[0].seconds).toBe(45);
});
