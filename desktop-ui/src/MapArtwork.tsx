/** Original offline 10-map visual atlas; no telemetry is inferred from imagery. */
const mapThemes:Record<string,string>={
  "the island":"island","the center":"center","scorched earth":"desert",
  "aberration":"aberration","extinction":"extinction",
  "genesis part 1":"genesis","valguero":"valguero",
  "ragnarok":"ragnarok","astraeos":"astraeos","lost colony":"colony"
};

export function MapCover({name}:{name:string}){
  const theme=mapThemes[name.trim().toLocaleLowerCase("en-US")]??"unknown";
  return <span className={`premium-map-cover premium-map-${theme}`} aria-hidden="true">
    <span className="premium-cover-ridge"/><span className="premium-cover-light"/>
  </span>;
}

