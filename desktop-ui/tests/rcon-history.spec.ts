import {test,expect} from "@playwright/test";
import {decodeHistory} from "../src/state/rconHistory";
test("stored command history accepts only fixed identifiers and strips private fields",()=>{
  const utc="2026-11-25T12:00:00Z";
  const decoded=decodeHistory(JSON.stringify({entries:[
    {action:"ListPlayers",kind:"reply",utc,response:"private player"},
    {action:"custom",kind:"dispatch",utc,command:"private argument"},
    {action:"Broadcast private",kind:"dispatch",utc},
    {action:"SaveWorld",kind:"reply",utc},
    {action:"GetChat",kind:"reply",utc:"invalid"}
  ],favorites:["ListPlayers","ListPlayers","SaveWorld","GetChat","secret"]}));
  expect(decoded.entries.map(x=>x.action)).toEqual(["ListPlayers","custom"]);
  expect(decoded.favorites).toEqual(["ListPlayers","GetChat"]);
  expect(JSON.stringify(decoded)).not.toContain("private");
  expect(JSON.stringify(decoded)).not.toContain("secret");
});
test("malformed storage is not executed or shown as verified history",()=>{
  expect(decodeHistory("broken")).toEqual({entries:[],favorites:[]});
});
