import {useState} from "react";
import type {LootCrate} from "./state/lootCrates";
import {simulateLoot,type Simulation} from "./state/lootSimulator";
export function LootSimulator({crate}:{crate:LootCrate}){
  const [seed,setSeed]=useState("42");
  const [rolls,setRolls]=useState("200");
  const [result,setResult]=useState<{key:string;data:Simulation}|null>(null);
  const [error,setError]=useState("");
  const key=JSON.stringify([crate,seed,rolls]);
  const data=result?.key===key?result.data:null;
  const run=()=>{
    setError("");setResult(null);
    try{
      if(!/^\d+$/.test(seed)||!/^\d+$/.test(rolls))throw Error("Egész seed és nyitásszám szükséges.");
      setResult({key,data:simulateLoot(crate,Number(seed),Number(rolls))});
    }catch(e){setError(e instanceof Error?e.message:"Érvénytelen loot modell.");}
  };
  return <section className="ini-preview" style={{display:"block",margin:8}} aria-label="Loot Simulator">
    <h4>Loot Simulator</h4>
    <p className="muted small">Seedelt, súlyozott modell, nem ASA motoros teszt. Az egyenletes set/item számot, mennyiséget és blueprint esélyt szimulálja.
      A minőségi statokat, játékon belüli RNG-t és alap loot táblákat nem reprodukálja.</p>
    <label>Seed <input aria-label="Loot seed" inputMode="numeric" maxLength={10} value={seed} onChange={e=>setSeed(e.target.value)}/></label>
    <label>Nyitások (1–500) <input aria-label="Loot nyitások" inputMode="numeric" maxLength={3} value={rolls} onChange={e=>setRolls(e.target.value)}/></label>
    <button type="button" className="secondary" onClick={run}>Szimuláció futtatása</button>
    {error&&<p role="alert" className="warning-inline">{error}</p>}
    {result&&!data&&<p className="muted">A modell megváltozott; futtasd újra.</p>}
    {data&&<>
      <p>{data.rolls} nyitás · seed {data.seed} · {data.shortfalls} részleges nyitás</p>
      {data.incomplete&&<p className="warning-inline">Hozzáfűzés: az alapjáték lootja nincs a modellben, az eredmény csak az egyedi setekre vonatkozik.</p>}
      <div style={{overflowX:"auto"}}>
        <table><thead><tr><th>Item class</th><th>Megjelenés</th><th>Átlag / nyitás</th><th>BP darab</th></tr></thead>
        <tbody>{data.results.map(x=><tr key={x.item}><td>{x.item}</td><td>{x.openings} ({(100*x.openings/data.rolls).toFixed(1)}%)</td><td>{(x.quantity/data.rolls).toFixed(2)}</td><td>{x.blueprints}</td></tr>)}</tbody></table>
      </div>
    </>}
  </section>;
}
