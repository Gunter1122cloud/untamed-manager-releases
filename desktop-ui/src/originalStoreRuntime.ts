// Opaque value used only for backward-compatible identification.
// Do not rename the persisted cluster ID or the shared transfer directory.
const originalClusterId = "\u006Bovacluster";

export const isOriginalStoreClusterId = (clusterId: string): boolean =>
  clusterId.toLowerCase() === originalClusterId;
