// Only the native read-only transport diagnostic is presented here.
// Ownership failures and uncertain mutating commands retain their existing UI.
export function rconTransportHint(error:unknown):string|null {
  const prefix="UM-RCON-TRANSPORT: ";
  return error instanceof Error && error.message.startsWith(prefix)
    ?error.message.slice(prefix.length):null;
}
