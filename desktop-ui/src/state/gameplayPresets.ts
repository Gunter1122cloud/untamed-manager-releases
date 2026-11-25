import {emptyEngram,inspectNamedEngrams,editNamedEngram} from "./namedEngrams";
export type PresetId="primitive"|"viking";
export type PresetItem={label:string;className:string};
// Small independently selected baseline. Class identifiers cross-checked
// against the pinned YARK ASA engram catalog (original source: ark.wiki.gg).
// This is a deny-list of explicit classes, not a complete conversion mod.
const modern=[
  ["Fabricator","Fabricator"],["Electrical Generator","PowerGenerator"],
  ["Air Conditioner","AirConditioner"],["Refrigerator","IceBox"],
  ["Chemistry Bench","ChemBench"],["Industrial Forge","IndustrialForge"],
  ["Industrial Grill","Grill"],["Assault Rifle","MachinedRifle"],
  ["Pump-Action Shotgun","MachinedShotgun"],["Longneck Rifle","SimpleRifle"],
  ["Shotgun","SimpleShotgun"],["Simple Pistol","Pistol"],
  ["Rocket Launcher","RocketLauncher"],["C4 Charge","C4Ammo"],
  ["C4 Remote Detonator","WeaponC4"],["Auto Turret","Turret"],
  ["Heavy Auto Turret","HeavyTurret"],["Tek Replicator","TekReplicator"],
  ["Tek Generator","TekGenerator"],["Tek Rifle","TekRifle"],["Tek Turret","TurretTek"]
];
export function presetItems(id:PresetId):PresetItem[]{
  const extra=id==="primitive"?[["Compound Bow","CompoundBow"],["Crossbow","Crossbow"]]:
    [["Compound Bow","CompoundBow"]];
  return [...modern,...extra].map(([label,suffix])=>({label,className:"EngramEntry_"+suffix+"_C"}));
}
export function planPreset(text:string,classes:readonly string[]){
  if(classes.length===0||classes.length>100)throw Error("Válassz 1–100 engramot.");
  if(new Set(classes.map(x=>x.toLowerCase())).size!==classes.length)throw Error("Duplikált class.");
  const first=inspectNamedEngrams(text);
  if(first.unknown.length)throw Error("Nem értelmezett név szerinti engram sor; ellenőrizd az eredeti INI-t.");
  // Never automatically change global unlock rules or index overrides.
  const warnings:string[]=[];
  if(/^\s*bAutoUnlockAllEngrams\s*=\s*true\s*$/im.test(text))warnings.push("Az automatikus engramfeloldás engedélyezett.");
  if(/^\s*bOnlyAllowSpecifiedEngrams\s*=\s*true\s*$/im.test(text))warnings.push("Engram allow-list aktív; a preset nem teljes allow-list.");
  if(/^\s*OverrideEngramEntries\s*=/im.test(text))warnings.push("Index alapú engram felülírás is található.");
  let next=text;
  const changes:{className:string;action:"add"|"hide"|"unchanged"}[]=[];
  for(const className of classes){
    if(!/^EngramEntry_[A-Za-z0-9_]+_C$/.test(className)||className.length>180)throw Error("Érvénytelen class.");
    const matches=inspectNamedEngrams(next).rows.filter(r=>r.value.className.toLowerCase()===className.toLowerCase());
    if(matches.length>1)throw Error("Duplikált meglévő engram: "+className);
    const row=matches[0];
    if(row?.value.hidden==="True"){changes.push({className,action:"unchanged"});continue;}
    next=editNamedEngram(next,row??null,row?{...row.value,hidden:"True"}:{...emptyEngram(),className,hidden:"True"});
    changes.push({className,action:row?"hide":"add"});
  }
  return {next,changes,warnings};
}
