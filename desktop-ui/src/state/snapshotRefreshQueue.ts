/**
 * Serialize dashboard evidence reads. A foreground request made during a
 * read must observe a new snapshot, not join an older background response.
 * Multiple foreground waiters coalesce into one queued pass. No mutations.
 */
export function createSnapshotRefreshQueue(read: (background: boolean) => Promise<void>) {
  let active: Promise<void> | undefined;
  let queued: Promise<void> | undefined;
  function run(background: boolean): Promise<void> {
    const task = Promise.resolve().then(() => read(background));
    const settled = task.finally(() => {
      if (active === settled) active = undefined;
    });
    active = settled;
    return settled;
  }
  return {
    request(background = false): Promise<void> {
      if (queued) return background && active ? active : queued;
      if (!active) return run(background);
      if (background) return active;
      queued = active.catch(() => undefined).then(() => {
        queued = undefined;
        return run(false);
      });
      return queued;
    },
  };
}
