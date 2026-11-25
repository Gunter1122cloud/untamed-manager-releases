import {test,expect} from "@playwright/test";
import {emptyEngram,formatNamedEngram,inspectNamedEngrams,editNamedEngram} from "../src/state/namedEngrams";
const head="[/Script/ShooterGame.ShooterGameMode]\r\n";
const v={...emptyEngram(),className:"EngramEntry_Example_C",cost:"0",hidden:"False" as const};
test("named engram add and replace only the targeted row and preserve other config",()=>{
  const source=head+";comment\r\nOverrideEngramEntries=(EngramIndex=2)\r\nUnknownMod=Yes\r\n[Other]\r\nValue=3";
  const added=editNamedEngram(source,null,v);
  expect(added).toContain("UnknownMod=Yes\r\n"+formatNamedEngram(v)+"\r\n[Other]");
  const row=inspectNamedEngrams(added).rows[0];
  expect(row.value).toEqual(v);
  const changed=editNamedEngram(added,row,{...v,level:"25"});
  expect(changed).toContain("EngramLevelRequirement=25");
  expect(changed).toContain("OverrideEngramEntries=(EngramIndex=2)");
  expect(editNamedEngram(added,row,null)).toBe(source);
  expect(()=>editNamedEngram(added,null,v)).toThrow();
  expect(()=>editNamedEngram(changed,row,v)).toThrow();
});
test("unknown named rows remain untouched; unsafe or ambiguous values are refused",()=>{
  const unknown=head+'OverrideNamedEngramEntries=(EngramClassName="EngramEntry_Unknown_C",FutureField=1)\r\n';
  expect(inspectNamedEngrams(unknown).unknown).toHaveLength(1);
  expect(()=>editNamedEngram(unknown,null,v)).toThrow();
  expect(()=>formatNamedEngram({...v,className:"bad,EngramHidden=True"})).toThrow();
  expect(()=>formatNamedEngram({...v,cost:"-1"})).toThrow();
  const duplicate=head+formatNamedEngram(v)+"\r\n"+formatNamedEngram(v);
  expect(()=>editNamedEngram(duplicate,inspectNamedEngrams(duplicate).rows[0],{...v,cost:"5"})).toThrow();
});
