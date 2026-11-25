import {gameSection,splitLines} from "./engramPoints";
const KEY="OverrideNamedEngramEntries";
const SECTION="/Script/ShooterGame.ShooterGameMode";
export type NamedEngram={className:string;hidden:""|"True"|"False";cost:string;level:string;prereq:""|"True"|"False"};
export type NamedRow={index:number;raw:string;value:NamedEngram};
export function emptyEngram():NamedEngram{return {className:"",hidden:"",cost:"",level:"",prereq:""};}
const safeClass=(s:string)=>/^[A-Za-z][A-Za-z0-9_]{0,179}$/.test(s);
const safeNumber=(s:string)=>s===""||/^(?:0|[1-9]\d{0,6})$/.test(s);
function bool(s:string):NamedEngram["hidden"]|null{
  return /^true$/i.test(s)?"True":/^false$/i.test(s)?"False":null;
}
export function formatNamedEngram(v:NamedEngram):string{
  if(!safeClass(v.className)||!safeNumber(v.cost)||!safeNumber(v.level)||
    !["","True","False"].includes(v.hidden)||!["","True","False"].includes(v.prereq))
    throw Error("Érvénytelen engram class vagy mezőérték.");
  const fields=['EngramClassName="'+v.className+'"'];
  if(v.hidden!=="")fields.push("EngramHidden="+v.hidden);
  if(v.cost!=="")fields.push("EngramPointsCost="+v.cost);
  if(v.level!=="")fields.push("EngramLevelRequirement="+v.level);
  if(v.prereq!=="")fields.push("RemoveEngramPreReq="+v.prereq);
  if(fields.length===1)throw Error("Adj meg legalább egy módosítandó mezőt.");
  return KEY+"=("+fields.join(",")+")";
}
function parse(raw:string):NamedEngram|null{
  const match=/^\s*OverrideNamedEngramEntries\s*=\s*\(([^()]*)\)\s*$/i.exec(raw);
  if(!match)return null;
  const v=emptyEngram(),seen=new Set<string>();
  for(const field of match[1].split(",")){
    const item=/^\s*([A-Za-z]+)\s*=\s*(.*?)\s*$/.exec(field);
    if(!item)return null;
    const name=item[1].toLowerCase(),value=item[2];
    if(seen.has(name))return null;seen.add(name);
    switch(name){
      case "engramclassname": {
        const cls=/^"([A-Za-z][A-Za-z0-9_]{0,179})"$/.exec(value);
        if(!cls)return null;v.className=cls[1];break;
      }
      case "engramhidden": {const b=bool(value);if(b===null)return null;v.hidden=b;break;}
      case "engrampointscost":if(value===""||!safeNumber(value))return null;v.cost=value;break;
      case "engramlevelrequirement":if(value===""||!safeNumber(value))return null;v.level=value;break;
      case "removeengramprereq":{const b=bool(value);if(b===null)return null;v.prereq=b;break;}
      default:return null;
    }
  }
  return v.className?v:null;
}
export function inspectNamedEngrams(text:string){
  const lines=splitLines(text),rows:NamedRow[]=[],unknown:number[]=[];
  let start=-1,end=lines.length,inSection=false;
  for(let i=0;i<lines.length;i++){
    const raw=lines[i].text,header=/^\s*\[([^\]]+)\]\s*$/.exec(raw);
    if(header){
      if(inSection)end=i;
      inSection=gameSection(header[1]);
      if(inSection){if(start!==-1)throw Error("Többszörös ShooterGameMode szakasz.");start=i;}
      continue;
    }
    if(!inSection||!/^\s*OverrideNamedEngramEntries\b/i.test(raw))continue;
    const value=parse(raw);
    if(value)rows.push({index:i,raw,value});else unknown.push(i);
    if(rows.length+unknown.length>2000)throw Error("Túl sok név szerinti engrambejegyzés.");
  }
  return {lines,rows,unknown,start,end};
}
export function editNamedEngram(text:string,expected:NamedRow|null,value:NamedEngram|null):string{
  const {lines,rows,unknown,start,end}=inspectNamedEngrams(text);
  if(expected){
    const found=rows.find(r=>r.index===expected.index&&r.raw===expected.raw);
    if(!found)throw Error("Az engram sor megváltozott. Töltsd be újra.");
    if(value===null){
      return lines.filter((_,i)=>i!==expected.index).map(l=>l.text+l.eol).join("");
    }
    if(value.className.toLowerCase()!==found.value.className.toLowerCase())
      throw Error("A meglévő class azonosítója nem nevezhető át; külön új sor szükséges.");
    if(rows.filter(r=>r.value.className.toLowerCase()===value.className.toLowerCase()).length!==1)
      throw Error("Többszörös class bejegyzés; előbb rendezd a duplikációt.");
    // Unsupported lines stay byte-identical. Do not edit an ambiguous
    // class while unknown rows might define the same override.
    if(unknown.length)throw Error("Nem értelmezett engram sor található; ellenőrizd a nyers INI-t.");
    if(JSON.stringify(value)===JSON.stringify(found.value))return text;
    lines[found.index]={...lines[found.index],text:formatNamedEngram(value)};
    return lines.map(l=>l.text+l.eol).join("");
  }
  if(!value)throw Error("Új engramhoz érték szükséges.");
  if(unknown.length)throw Error("Nem értelmezett engram sor található; új class nem ellenőrizhető.");
  if(rows.some(r=>r.value.className.toLowerCase()===value.className.toLowerCase()))
    throw Error("Ez a class már szerepel az INI-ben.");
  const newline=lines.find(l=>l.eol)?.eol??"\r\n";
  const insertAt=start<0?lines.length:end;
  const inserted=start<0?[{text:"["+SECTION+"]",eol:newline},{text:formatNamedEngram(value),eol:newline}]
    :[{text:formatNamedEngram(value),eol:newline}];
  if(insertAt>0&&lines[insertAt-1].eol==="")lines[insertAt-1]={...lines[insertAt-1],eol:newline};
  lines.splice(insertAt,0,...inserted);
  if(text&&!/(?:\r\n|\r|\n)$/.test(text))lines[lines.length-1]={...lines[lines.length-1],eol:""};
  return lines.map(l=>l.text+l.eol).join("");
}
