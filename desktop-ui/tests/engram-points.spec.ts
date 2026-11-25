import {test,expect} from "@playwright/test";
import {readEngramPoints,writeEngramPoints} from "../src/state/engramPoints";
const key="OverridePlayerLevelEngramPoints";
test("ordered level point editor preserves unrelated keys, comments and sections",()=>{
  const text="[/Script/ShooterGame.ShooterGameMode]\r\n;keep\r\n"+key+"=0\r\nCustomModValue=untouched\r\n"+key+"=8\r\n[Other]\r\n"+key+"=777";
  expect(readEngramPoints(text)).toEqual([0,8]);
  const edited=writeEngramPoints(text,[0,10,20]);
  expect(edited).toBe("[/Script/ShooterGame.ShooterGameMode]\r\n;keep\r\n"+key+"=0\r\nCustomModValue=untouched\r\n"+key+"=10\r\n"+key+"=20\r\n[Other]\r\n"+key+"=777");
  expect(readEngramPoints(edited)).toEqual([0,10,20]);
  expect(writeEngramPoints(text,[0,8])).toBe(text);
  expect(writeEngramPoints(edited,[0])).toContain("CustomModValue=untouched");
});
test("empty and custom INI can create a game mode section without touching another section",()=>{
  expect(readEngramPoints(writeEngramPoints("",[0,5]))).toEqual([0,5]);
  expect(writeEngramPoints("[Other]\nValue=1",[3])).toBe("[Other]\nValue=1\n[/Script/ShooterGame.ShooterGameMode]\n"+key+"=3");
  expect(writeEngramPoints("[Other]\nValue=1",[])).toBe("[Other]\nValue=1");
});
test("ambiguous or invalid values fail rather than deleting unsupported syntax",()=>{
  expect(()=>writeEngramPoints("[ShooterGame.ShooterGameMode]\n"+key+"=oops",[1])).toThrow();
  expect(()=>writeEngramPoints("[ShooterGame.ShooterGameMode]\n[/Script/ShooterGame.ShooterGameMode]\n",[1])).toThrow();
  expect(()=>writeEngramPoints("",[-1])).toThrow();
  expect(()=>writeEngramPoints("",[1.5])).toThrow();
});
