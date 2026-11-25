import {test,expect} from "@playwright/test";
import {newCrate,newEntry} from "../src/state/lootCrates";
import {simulateLoot} from "../src/state/lootSimulator";
function fixture(){
  const c=newCrate();c.className="SupplyCrate_Test_C";c.sets[0].entries[0]={...newEntry(),item:"PrimalItem_Test_C",minQuantity:2,maxQuantity:2,forceBlueprint:true};return c;
}
test("fixed loot grants exact quantities and forced blueprints",()=>{
  const c=fixture();const result=simulateLoot(c,42,100);
  expect(result.results).toEqual([{item:"PrimalItem_Test_C",openings:100,quantity:200,blueprints:200}]);
  expect(result.shortfalls).toBe(0);
  expect(simulateLoot(c,42,100)).toEqual(result);
});
test("weighted picks are seeded, appearances count openings, not duplicate grants",()=>{
  const c=fixture();c.sets[0].entries.push({...newEntry(),item:"PrimalItem_Other_C",weight:3});
  const r=simulateLoot(c,42,500);
  expect(r).toEqual(simulateLoot(c,42,500));
  expect(r.results.reduce((n,x)=>n+x.openings,0)).toBe(500);
  expect(r.results[0].item).toBe("PrimalItem_Other_C");
  c.sets[0].unique=false;c.sets[0].minItems=2;c.sets[0].maxItems=2;
  expect(simulateLoot(c,42,500).results.every(x=>x.openings<=500)).toBe(true);
});
test("repeated set selection reports exhausted unique pool; appended vanilla loot is incomplete",()=>{
  const c=fixture();c.unique=false;c.minSets=2;c.maxSets=2;c.append=true;
  const r=simulateLoot(c,42,50);
  expect(r.shortfalls).toBe(50);expect(r.incomplete).toBe(true);
  expect(()=>simulateLoot(c,42,501)).toThrow();
  expect(()=>simulateLoot(c,NaN,10)).toThrow();
});
