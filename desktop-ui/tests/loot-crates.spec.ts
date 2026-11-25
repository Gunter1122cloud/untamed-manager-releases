import {test,expect} from "@playwright/test";
import {newCrate,formatCrate,inspectCrates,editCrate} from "../src/state/lootCrates";
function fixture(){
  const c=newCrate();c.className="SupplyCrate_Test_C";c.sets[0].entries[0].item="PrimalItem_Test_C";return c;
}
test("structured loot round-trips and preserves other INI keys",()=>{
  const c=fixture(),source="[/Script/ShooterGame.ShooterGameMode]\r\n;keep\r\nOtherMod=1\r\n[Other]\r\nValue=2";
  const edited=editCrate(source,null,c);
  expect(edited).toContain("OtherMod=1\r\n"+formatCrate(c)+"\r\n[Other]");
  const row=inspectCrates(edited).rows[0];expect(row.crate).toEqual(c);
  expect(editCrate(edited,row,c)).toBe(edited);
  const changed=structuredClone(c);changed.sets[0].entries[0].maxQuantity=10;
  expect(inspectCrates(editCrate(edited,row,changed)).rows[0].crate).toEqual(changed);
  expect(()=>editCrate(edited,null,c)).toThrow();
});
test("unsupported rows and invalid parameters cannot silently replace loot",()=>{
  const c=fixture(),raw="[/Script/ShooterGame.ShooterGameMode]\nConfigOverrideSupplyCrateItems=(FutureField=1)\n";
  expect(inspectCrates(raw).unknown).toHaveLength(1);
  expect(()=>editCrate(raw,null,c)).toThrow();
  c.sets[0].entries[0].blueprintChance=2;expect(()=>formatCrate(c)).toThrow();
  c.sets[0].entries[0].blueprintChance=0;c.maxSets=2;expect(()=>formatCrate(c)).toThrow();
});
