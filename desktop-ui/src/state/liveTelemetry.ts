export type Point={time:number;cpu:number|null;ram:number|null;players:number|null};
export type PlayerProof={count:number;utc:string};
export const HISTORY_MS=60*60*1000;
export const MAX_POINTS=241;

export function percent(value:number|null):number|null {
  return typeof value==="number"&&Number.isFinite(value)&&value>=0&&value<=100?value:null;
}
export function playerTotal(ids:readonly string[],proofs:Record<string,PlayerProof>,time:number):number|null {
  // Without running-map evidence we cannot claim an exact measured zero.
  if(!ids.length)return null;
  let total=0;
  for(const id of ids){
    const proof=proofs[id], observed=Date.parse(proof?.utc??"");
    if(!proof||!Number.isFinite(observed)||observed>time+5000||time-observed>60000||
      !Number.isSafeInteger(proof.count)||proof.count<0)return null;
    total+=proof.count;
    if(!Number.isSafeInteger(total))return null;
  }
  return total;
}
export function appendPoint(old:readonly Point[],point:Point):Point[]{
  if(!Number.isFinite(point.time)||old.length&&point.time<=old[old.length-1].time)return [...old];
  return [...old.filter(p=>point.time-p.time<=HISTORY_MS),point].slice(-MAX_POINTS);
}
export function plotSegments(points:readonly Point[],key:"cpu"|"ram"|"players",max:number):string[]{
  if(!points.length||!Number.isFinite(max)||max<=0)return [];
  const first=points[0].time,last=points[points.length-1].time,span=Math.max(1,last-first);
  const result:string[]=[];let path="",previous:number|null=null;
  for(const point of points){
    const value=point[key];
    if(value===null||!Number.isFinite(value)||value<0||value>max){
      if(path)result.push(path);path="";previous=null;continue;
    }
    if(previous!==null&&point.time-previous>60000){if(path)result.push(path);path="";}
    const x=10+280*(point.time-first)/span,y=75-65*value/max;
    path+=(path?" L":"M")+x.toFixed(2)+" "+y.toFixed(2);
    previous=point.time;
  }
  if(path)result.push(path);
  return result;
}
