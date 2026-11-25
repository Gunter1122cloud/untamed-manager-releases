const KEY="OverridePlayerLevelEngramPoints";
const SECTION="/Script/ShooterGame.ShooterGameMode";
const MAX_ROWS=2000;
type Line={text:string;eol:string};
export function splitLines(text:string):Line[]{
  const pieces=text.split(/(\r\n|\n|\r)/);
  const result:Line[]=[];
  for(let i=0;i<pieces.length;i+=2){
    if(i===pieces.length-1&&pieces[i]===""&&i>0)break;
    if(pieces[i]!==""||pieces[i+1])result.push({text:pieces[i],eol:pieces[i+1]??""});
  }
  return result;
}
export function gameSection(name:string):boolean{
  return [SECTION.toLowerCase(),"shootergame.shootergamemode"].includes(name.trim().toLowerCase());
}
function inspect(text:string){
  const lines=splitLines(text),indexes:number[]=[],values:number[]=[];
  let start=-1,end=lines.length,inSection=false;
  for(let i=0;i<lines.length;i++){
    const raw=lines[i].text;
    const header=/^\s*\[([^\]]+)\]\s*$/.exec(raw);
    if(header){
      if(inSection)end=i;
      inSection=gameSection(header[1]);
      if(inSection){
        if(start!==-1)throw Error("Többszörös ShooterGameMode szakasz: kézi ellenőrzés szükséges.");
        start=i;
      }
      continue;
    }
    if(!inSection)continue;
    const match=/^(\s*OverridePlayerLevelEngramPoints\s*=\s*)(.*)$/i.exec(raw);
    if(!match){
      if(/^\s*OverridePlayerLevelEngramPoints\b/i.test(raw))throw Error("Nem értelmezhető engram-pontsor.");
      continue;
    }
    if(!/^(?:0|[1-9]\d{0,6})\s*$/.test(match[2]))throw Error("Nem egyértelmű engram-pontsor; kézi szerkesztés szükséges.");
    indexes.push(i);values.push(Number(match[2].trim()));
    if(values.length>MAX_ROWS)throw Error("Túl sok engram-pontsor a szerkesztőhöz.");
  }
  return {lines,indexes,values,start,end};
}
export function readEngramPoints(text:string):number[]{return inspect(text).values;}
export function writeEngramPoints(text:string,values:readonly number[]):string{
  if(values.length>MAX_ROWS||values.some(v=>!Number.isInteger(v)||v<0||v>9999999))
    throw Error("0–9 999 999 közötti egész pontérték szükséges; legfeljebb 2000 sor.");
  const {lines,indexes,start,end}=inspect(text);
  const eol=lines.find(l=>l.eol)?.eol??"\r\n";
  if(start<0&&values.length===0)return text;
  const indexMap=new Map(indexes.map((n,i)=>[n,i]));
  const added=values.slice(indexes.length);
  const insertAt=indexes.length?indexes[indexes.length-1]+1:end;
  const out:Line[]=[];
  const append=(line:Line)=>{
    if(out.length&&out[out.length-1].eol==="")out[out.length-1]={...out[out.length-1],eol};
    out.push(line);
  };
  const insert=()=>{for(const v of added)append({text:KEY+"="+v,eol});};
  if(start<0){
    for(const line of lines)append(line);
    append({text:"["+SECTION+"]",eol});
    insert();
  }else{
    for(let i=0;i<=lines.length;i++){
      if(i===insertAt)insert();
      if(i===lines.length)break;
      const line=lines[i],row=indexMap.get(i);
      if(row===undefined)append(line);
      else if(row<values.length){
        // Keep original key spelling, whitespace and line endings.
        append({...line,text:line.text.replace(/(=\s*)(\d+)(\s*)$/,(_m,prefix:string,_digits:string,suffix:string)=>prefix+values[row]+suffix)});
      }
    }
  }
  // Preserve absence of final newline if the original had no final newline.
  if(out.length&&text&&!/(?:\r\n|\r|\n)$/.test(text))out[out.length-1]={...out[out.length-1],eol:""};
  return out.map(l=>l.text+l.eol).join("");
}
