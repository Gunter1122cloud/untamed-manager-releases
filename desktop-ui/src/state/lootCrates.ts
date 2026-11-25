import {gameSection,splitLines} from "./engramPoints";
const KEY="ConfigOverrideSupplyCrateItems";
const SECTION="/Script/ShooterGame.ShooterGameMode";
export type LootEntry={item:string;weight:number;minQuantity:number;maxQuantity:number;minQuality:number;maxQuality:number;forceBlueprint:boolean;blueprintChance:number};
export type LootSet={weight:number;minItems:number;maxItems:number;unique:boolean;entries:LootEntry[]};
export type LootCrate={className:string;minSets:number;maxSets:number;unique:boolean;append:boolean;sets:LootSet[]};
export type LootRow={index:number;raw:string;crate:LootCrate};
export const newEntry=():LootEntry=>({item:"",weight:1,minQuantity:1,maxQuantity:1,minQuality:1,maxQuality:1,forceBlueprint:false,blueprintChance:0});
export const newSet=():LootSet=>({weight:1,minItems:1,maxItems:1,unique:true,entries:[newEntry()]});
export const newCrate=():LootCrate=>({className:"",minSets:1,maxSets:1,unique:true,append:false,sets:[newSet()]});
const isClass=(s:string)=>typeof s==="string"&&/^[A-Za-z][A-Za-z0-9_]{0,179}$/.test(s);
const finite=(n:number,min:number,max:number)=>Number.isFinite(n)&&n>=min&&n<=max;
function pair(min:number,max:number,bound:number,integer:boolean){
  if(!finite(min,0,bound)||!finite(max,min,bound)||(integer&&(!Number.isInteger(min)||!Number.isInteger(max))))throw Error("Érvénytelen minimum / maximum.");
}
export function validateCrate(c:LootCrate){
  if(!isClass(c.className)||!c.sets.length||c.sets.length>12)throw Error("Valódi láda class és 1–12 item set szükséges.");
  pair(c.minSets,c.maxSets,12,true);
  if(c.minSets<1||c.unique&&c.maxSets>c.sets.length)throw Error("A kiválasztható setek száma nem megfelelő.");
  for(const s of c.sets){
    if(!finite(s.weight,0.000001,1000000)||!s.entries.length||s.entries.length>32)throw Error("Pozitív set súly és 1–32 item entry szükséges.");
    pair(s.minItems,s.maxItems,32,true);
    if(s.minItems<1||s.unique&&s.maxItems>s.entries.length)throw Error("A kiválasztható itemek száma nem megfelelő.");
    for(const e of s.entries){
      if(!isClass(e.item)||!finite(e.weight,0.000001,1000000)||!finite(e.blueprintChance,0,1))
        throw Error("Valódi item class, pozitív súly és 0–1 blueprint esély szükséges.");
      pair(e.minQuantity,e.maxQuantity,100000,true);
      pair(e.minQuality,e.maxQuality,1000,false);
      if(e.minQuantity<1)throw Error("Legalább egy tárgy szükséges.");
    }
  }
}
const b=(v:boolean)=>v?"True":"False";
export function formatCrate(c:LootCrate):string{
  validateCrate(c);
  const sets=c.sets.map(s=>"(MinNumItems="+s.minItems+",MaxNumItems="+s.maxItems+
    ",NumItemsPower=1,SetWeight="+s.weight+",bItemsRandomWithoutReplacement="+b(s.unique)+
    ",ItemEntries=("+s.entries.map(e=>'(EntryWeight='+e.weight+',ItemClassStrings=("'+e.item+
    '"),ItemsWeights=(1),MinQuantity='+e.minQuantity+",MaxQuantity="+e.maxQuantity+
    ",MinQuality="+e.minQuality+",MaxQuality="+e.maxQuality+",bForceBlueprint="+b(e.forceBlueprint)+
    ",ChanceToBeBlueprintOverride="+e.blueprintChance+")").join(",")+"))");
  return KEY+'=(SupplyCrateClassString="'+c.className+'",MinItemSets='+c.minSets+",MaxItemSets="+c.maxSets+
    ",NumItemSetsPower=1,bSetsRandomWithoutReplacement="+b(c.unique)+",bAppendItemSets="+b(c.append)+
    ",ItemSets=("+sets.join(",")+"))";
}
// Strict bounded tuple reader for our supported subset; do not reinterpret
// unknown fields, powers, multiple class choices, escaped strings or comments.
type Value=string|Field[];
type Field={key:string|null;value:Value};
function tuple(source:string):Field[]{
  if(source.length>250000)throw Error("Loot sor túl nagy.");
  let i=0,fields=0;
  const ws=()=>{while(i<source.length&&/\s/.test(source[i]))i++;};
  const read=(depth:number):Value=>{
    ws();if(depth>8)throw Error("Túl mély tuple.");
    if(source[i]==="("){
      i++;const out:Field[]=[];ws();
      if(source[i]===")") {i++;return out;}
      while(true){
        if(++fields>12000)throw Error("Túl sok loot mező.");
        ws();const mark=i;
        const named=/^[A-Za-z][A-Za-z0-9_]*\s*=/.exec(source.slice(i));
        let key:string|null=null;
        if(named){key=named[0].slice(0,-1).trim().toLowerCase();i+=named[0].length;}else i=mark;
        out.push({key,value:read(depth+1)});ws();
        if(source[i]===")"){i++;return out;}
        if(source[i]!==",")throw Error("Érvénytelen tuple.");i++;
      }
    }
    const token=/^(?:"[A-Za-z][A-Za-z0-9_]{0,179}"|[A-Za-z]+|(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?)/.exec(source.slice(i));
    if(!token)throw Error("Nem támogatott loot érték.");
    i+=token[0].length;return token[0];
  };
  const result=read(0);ws();
  if(i!==source.length||typeof result==="string")throw Error("Érvénytelen loot tuple.");
  return result;
}
function record(v:Value,keys:string[]):Record<string,Value>{
  if(typeof v==="string"||v.length!==keys.length)throw Error("Nem támogatott mezőkészlet.");
  const result:Record<string,Value>=Object.create(null);
  for(const f of v){if(f.key===null||!keys.includes(f.key)||Object.hasOwn(result,f.key))throw Error("Ismeretlen/ismételt loot mező.");result[f.key]=f.value;}
  return result;
}
const list=(v:Value):Value[]=>{if(typeof v==="string"||v.some(f=>f.key!==null))throw Error("Lista szükséges.");return v.map(f=>f.value);};
const scalar=(v:Value):string=>{if(typeof v!=="string")throw Error("Skalár szükséges.");return v;};
const number=(v:Value):number=>{const s=scalar(v);if(!/^(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(s))throw Error("Szám szükséges.");return Number(s);};
const cls=(v:Value)=>{const s=scalar(v);if(!/^"[A-Za-z][A-Za-z0-9_]{0,179}"$/.test(s))throw Error("Class szükséges.");return s.slice(1,-1);};
const boolean=(v:Value)=>{const s=scalar(v).toLowerCase();if(s!=="true"&&s!=="false")throw Error("Logikai érték szükséges.");return s==="true";};
function parse(raw:string):LootCrate|null{
  try{
    const match=/^\s*ConfigOverrideSupplyCrateItems\s*=\s*(.*)$/i.exec(raw);if(!match)return null;
    const a=record(tuple(match[1]),["supplycrateclassstring","minitemsets","maxitemsets","numitemsetspower","bsetsrandomwithoutreplacement","bappenditemsets","itemsets"]);
    if(number(a.numitemsetspower)!==1)return null;
    const crate:LootCrate={className:cls(a.supplycrateclassstring),minSets:number(a.minitemsets),maxSets:number(a.maxitemsets),
      unique:boolean(a.bsetsrandomwithoutreplacement),append:boolean(a.bappenditemsets),sets:list(a.itemsets).map(value=>{
      const s=record(value,["minnumitems","maxnumitems","numitemspower","setweight","bitemsrandomwithoutreplacement","itementries"]);
      if(number(s.numitemspower)!==1)throw Error("Nem egyenletes power.");
      return {weight:number(s.setweight),minItems:number(s.minnumitems),maxItems:number(s.maxnumitems),unique:boolean(s.bitemsrandomwithoutreplacement),
        entries:list(s.itementries).map(value=>{
          const e=record(value,["entryweight","itemclassstrings","itemsweights","minquantity","maxquantity","minquality","maxquality","bforceblueprint","chancetobeblueprintoverride"]);
          const items=list(e.itemclassstrings),weights=list(e.itemsweights);
          if(items.length!==1||weights.length!==1||number(weights[0])!==1)throw Error("Több class / súly nem támogatott.");
          return {item:cls(items[0]),weight:number(e.entryweight),minQuantity:number(e.minquantity),maxQuantity:number(e.maxquantity),
            minQuality:number(e.minquality),maxQuality:number(e.maxquality),forceBlueprint:boolean(e.bforceblueprint),blueprintChance:number(e.chancetobeblueprintoverride)};
        })};
    })};
    validateCrate(crate);return crate;
  }catch{return null;}
}
export function inspectCrates(text:string){
  const lines=splitLines(text),rows:LootRow[]=[],unknown:number[]=[];
  let start=-1,end=lines.length,inside=false;
  for(let i=0;i<lines.length;i++){
    const raw=lines[i].text,h=/^\s*\[([^\]]+)\]\s*$/.exec(raw);
    if(h){if(inside)end=i;inside=gameSection(h[1]);if(inside){if(start!==-1)throw Error("Többszörös ShooterGameMode szakasz.");start=i;}continue;}
    if(!inside||!/^\s*ConfigOverrideSupplyCrateItems\b/i.test(raw))continue;
    const crate=parse(raw);if(crate)rows.push({index:i,raw,crate});else unknown.push(i);
    if(rows.length+unknown.length>1000)throw Error("Túl sok loot sor.");
  }
  return {lines,rows,unknown,start,end};
}
export function editCrate(text:string,expected:LootRow|null,crate:LootCrate):string{
  const next=formatCrate(crate);
  const {lines,rows,unknown,start,end}=inspectCrates(text);
  if(unknown.length)throw Error("Nem támogatott meglévő loot sor; a nyers INI változatlan.");
  if(rows.some(row=>row.index!==expected?.index&&row.crate.className.toLowerCase()===crate.className.toLowerCase()))throw Error("A láda class már létezik.");
  if(expected){
    const found=rows.find(row=>row.index===expected.index&&row.raw===expected.raw);
    if(!found||found.crate.className.toLowerCase()!==crate.className.toLowerCase())throw Error("A láda azonossága megváltozott.");
    if(JSON.stringify(found.crate)===JSON.stringify(crate))return text;
    lines[found.index]={...lines[found.index],text:next};
  }else{
    const eol=lines.find(l=>l.eol)?.eol??"\r\n",at=start<0?lines.length:end;
    if(at>0&&lines[at-1].eol==="")lines[at-1]={...lines[at-1],eol};
    lines.splice(at,0,...(start<0?[{text:"["+SECTION+"]",eol}]:[]),{text:next,eol});
    if(text&&!/(?:\r\n|\r|\n)$/.test(text))lines[lines.length-1]={...lines[lines.length-1],eol:""};
  }
  return lines.map(l=>l.text+l.eol).join("");
}
