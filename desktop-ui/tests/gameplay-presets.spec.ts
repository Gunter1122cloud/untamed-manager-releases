import {test,expect} from "@playwright/test";
import {presetItems,planPreset} from "../src/state/gameplayPresets";
import {inspectNamedEngrams} from "../src/state/namedEngrams";
test("explicit primitive and viking class lists are distinct and preserve mod settings",()=>{
  const primitive=presetItems("primitive"),viking=presetItems("viking");
  expect(primitive.some(x=>x.className==="EngramEntry_Crossbow_C")).toBe(true);
  expect(viking.some(x=>x.className==="EngramEntry_Crossbow_C")).toBe(false);
  const source='[/Script/ShooterGame.ShooterGameMode]\r\n;keep\r\nUnknownMod=3\r\nOverrideNamedEngramEntries=(EngramClassName="EngramEntry_Fabricator_C",EngramPointsCost=17,EngramHidden=False)\r\n';
  const result=planPreset(source,["EngramEntry_Fabricator_C","EngramEntry_TekGenerator_C"]);
  expect(result.next).toContain(";keep\r\nUnknownMod=3");
  const rows=inspectNamedEngrams(result.next).rows;
  expect(rows[0].value.cost).toBe("17");expect(rows[0].value.hidden).toBe("True");
  expect(rows[1].value.hidden).toBe("True");
  expect(planPreset(result.next,["EngramEntry_Fabricator_C"]).next).toBe(result.next);
});
test("preset never overwrites ambiguous class rows or global unlock rules",()=>{
  const raw="[/Script/ShooterGame.ShooterGameMode]\nbAutoUnlockAllEngrams=True\nbOnlyAllowSpecifiedEngrams=True\n";
  const x=planPreset(raw,["EngramEntry_Fabricator_C"]);
  expect(x.warnings).toHaveLength(2);expect(x.next).toContain("bAutoUnlockAllEngrams=True");
  expect(()=>planPreset(raw,["EngramEntry_Fabricator_C","EngramEntry_Fabricator_C"])).toThrow();
  expect(()=>planPreset(raw,["EngramEntry_Bad,C"])).toThrow();
});
