// UI suggestions only: the native AsaSteamMapCatalog validates every launch
// level, and unknown DLC/custom levels remain explicitly editable.
export const officialMapSuggestions = [
  {name:"The Island",level:"TheIsland_WP"},
  {name:"The Center",level:"TheCenter_WP"},
  {name:"Scorched Earth",level:"ScorchedEarth_WP"},
  {name:"Aberration",level:"Aberration_WP"},
  {name:"Extinction",level:"Extinction_WP"},
  {name:"Genesis Part 1",level:"Genesis_WP"},
  {name:"Valguero",level:"Valguero_WP"},
  {name:"Ragnarok",level:"Ragnarok_WP"},
  {name:"Astraeos",level:"Astraeos_WP"},
  {name:"Lost Colony",level:"LostColony_WP"},
] as const;
export const asaMapNames: readonly string[] = officialMapSuggestions.map(m=>m.name);
