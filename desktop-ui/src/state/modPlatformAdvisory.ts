// Treat file gameVersions values only as provider-supplied labels.
// Neither a Windows server build nor the absence of a console label proves
// PC-only. No install authorization or runtime/crossplay guarantee follows.
export type PlatformAdvisory={state:"crossplay-label"|"pc-only-label"|"conflict"|"unknown";labels:string[]};
export function platformAdvisory(versions:readonly string[]):PlatformAdvisory{
  const cross=new Set(["crossplay","cross-play","cross play","crossplatform","cross-platform","cross platform"]);
  const pc=new Set(["pc only","pc-only","pc_only"]);
  const labels=Array.from(new Set(versions.filter(v=>typeof v==="string"&&v.length<=80)
    .map(v=>v.trim()).filter(Boolean)));
  const explicit=labels.filter(v=>cross.has(v.toLowerCase())||pc.has(v.toLowerCase()));
  const hasCross=explicit.some(v=>cross.has(v.toLowerCase()));
  const hasPc=explicit.some(v=>pc.has(v.toLowerCase()));
  return {state:hasCross&&hasPc?"conflict":hasCross?"crossplay-label":hasPc?"pc-only-label":"unknown",
    labels:explicit};
}
export function platformAdvisoryText(state:PlatformAdvisory["state"]):string{
  switch(state){
    case "crossplay-label":return "Crossplay-jelölés a katalógusban; tényleges konzolos támogatás nem igazolt.";
    case "pc-only-label":return "PC-only jelölés a katalógusban; konzolos játékosoknál ellenőrzés szükséges.";
    case "conflict":return "Ellentmondó platformjelölések; crossplay nem igazolt.";
    default:return "Crossplay ismeretlen; a fájlmetaadat nem tartalmaz egyértelmű jelölést.";
  }
}
