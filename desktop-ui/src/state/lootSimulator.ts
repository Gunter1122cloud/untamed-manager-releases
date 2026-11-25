import {validateCrate,type LootCrate} from "./lootCrates";
export type LootResult={item:string;openings:number;quantity:number;blueprints:number};
export type Simulation={rolls:number;seed:number;incomplete:boolean;shortfalls:number;results:LootResult[]};
function random(seed:number){
  let state=seed>>>0;
  return ()=>{state=(Math.imul(state,1664525)+1013904223)>>>0;return state/4294967296;};
}
function weighted<T extends {weight:number}>(pool:T[],rng:()=>number):number{
  if(!pool.length)return -1;
  let threshold=rng()*pool.reduce((sum,x)=>sum+x.weight,0);
  for(let i=0;i<pool.length;i++){threshold-=pool[i].weight;if(threshold<0)return i;}
  return pool.length-1;
}
export function simulateLoot(crate:LootCrate,seed:number,rolls:number):Simulation{
  validateCrate(crate);
  if(!Number.isSafeInteger(seed)||seed<0||seed>4294967295||!Number.isInteger(rolls)||rolls<1||rolls>500)
    throw Error("0–4294967295 közötti seed és 1–500 nyitás szükséges.");
  const rng=random(seed),totals=new Map<string,LootResult>();
  const count=(min:number,max:number)=>min+Math.floor(rng()*(max-min+1));
  let shortfalls=0;
  for(let roll=0;roll<rolls;roll++){
    const selected=new Set<string>();
    const sets=crate.sets.map((set,index)=>({...set,index}));
    const entryPools=new Map<number,typeof crate.sets[number]["entries"]>();
    const n=count(crate.minSets,crate.maxSets);
    let incomplete=false;
    for(let i=0;i<n;i++){
      const si=weighted(sets,rng);if(si<0){incomplete=true;break;}
      const s=sets[si];if(crate.unique)sets.splice(si,1);
      const entries=entryPools.get(s.index)??[...s.entries];
      if(s.unique)entryPools.set(s.index,entries);
      const m=count(s.minItems,s.maxItems);
      for(let j=0;j<m;j++){
        const ei=weighted(entries,rng);if(ei<0){incomplete=true;break;}
        const e=entries[ei];if(s.unique)entries.splice(ei,1);
        const quantity=count(e.minQuantity,e.maxQuantity);
        const blueprint=e.forceBlueprint||rng()<e.blueprintChance;
        const key=e.item.toLowerCase();
        const result=totals.get(key)??{item:e.item,openings:0,quantity:0,blueprints:0};
        if(!selected.has(key)){result.openings++;selected.add(key);}
        result.quantity+=quantity;
        if(blueprint)result.blueprints+=quantity;
        totals.set(key,result);
      }
    }
    if(incomplete)shortfalls++;
  }
  return {rolls,seed,incomplete:crate.append,shortfalls,results:[...totals.values()].sort((a,b)=>b.openings-a.openings||a.item.localeCompare(b.item))};
}
