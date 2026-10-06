/** Keeps pinned request rows visible after SSE eviction (legacy PINNED_REQUESTS). */
export function mergePinnedIntoFeed<T extends Record<string, unknown>>(
  prev: Record<string, T>,
  nextById: Record<string, T>,
  pinned: ReadonlySet<string>,
  snapshots: Map<string, T>,
): Record<string, T> {
  const result = { ...nextById };
  for (const id of pinned) {
    const live = result[id];
    if (live) {
      snapshots.set(id, live);
    } else if (prev[id]) {
      result[id] = prev[id];
      snapshots.set(id, prev[id]);
    } else if (snapshots.has(id)) {
      result[id] = snapshots.get(id)!;
    }
  }
  return result;
}
