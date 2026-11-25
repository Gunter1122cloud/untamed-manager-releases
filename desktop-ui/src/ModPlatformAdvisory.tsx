import {platformAdvisory,platformAdvisoryText} from "./state/modPlatformAdvisory";
export function ModPlatformAdvisory({versions,fileId}:{versions:readonly string[];fileId:number}){
  const advisory=platformAdvisory(versions);
  return <small className="cf-versions" style={{display:"block"}}
    data-platform-evidence={advisory.state} aria-label={"Mod platformjelzés, fájl "+fileId}>
    #{fileId} · {platformAdvisoryText(advisory.state)}
    {advisory.labels.length>0?" ("+advisory.labels.join(", ")+")":""}
  </small>;
}
