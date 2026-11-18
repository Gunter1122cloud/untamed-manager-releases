import {useEffect,useRef} from "react";

/** A new shared revision requests native read-only evidence, never a replay.
 * Busy panels coalesce notifications and keep their in-flight action receipts.
 */
export function useMapEvidenceRefresh(revision:number,blocked:boolean,refresh:()=>Promise<void>){
  const last=useRef(revision);
  const latest=useRef(refresh);
  latest.current=refresh;
  useEffect(()=>{
    if(blocked||last.current===revision)return;
    last.current=revision;
    void latest.current();
  },[revision,blocked]);
}
