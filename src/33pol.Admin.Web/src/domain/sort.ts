export type SortDirection = 1 | -1;

export interface SortSpec {
  key: string;
  dir: SortDirection;
}

const DATE_KEYS = new Set(['createdAt', 'timestampUtc', 'lastUsedAt']);

export interface DecoratedItem<T> {
  item: T;
  sortKey: string | number;
}

/** Precompute the comparator key once per row instead of parsing dates on every comparison. */
export function computeSortKey(item: Record<string, unknown>, key: string): string | number {
  const raw = item[key];
  if (DATE_KEYS.has(key)) {
    return raw ? new Date(String(raw)).getTime() : 0;
  }
  if (typeof raw === 'number') {
    return raw;
  }
  const asNumber = Number(raw);
  if (raw !== '' && raw != null && Number.isFinite(asNumber) && String(raw).trim() !== '') {
    return asNumber;
  }
  return (raw ?? '').toString().toLowerCase();
}

export function decorateForSort<T extends Record<string, unknown>>(
  list: readonly T[],
  spec: SortSpec,
): DecoratedItem<T>[] {
  return list.map((item) => ({ item, sortKey: computeSortKey(item, spec.key) }));
}

export function sortDecorated<T>(decorated: DecoratedItem<T>[], dir: SortDirection): DecoratedItem<T>[] {
  return [...decorated].sort((a, b) => {
    const av = a.sortKey;
    const bv = b.sortKey;
    if (av < bv) return -dir;
    if (av > bv) return dir;
    return 0;
  });
}

export function undecorate<T>(decorated: readonly DecoratedItem<T>[]): T[] {
  return decorated.map((d) => d.item);
}

/** Decorate → sort → undecorate, matching the Alpine sortedList semantics without per-compare parsing. */
export function sortedList<T extends Record<string, unknown>>(
  list: readonly T[] | null | undefined,
  spec: SortSpec,
): T[] {
  const decorated = decorateForSort(list ?? [], spec);
  return undecorate(sortDecorated(decorated, spec.dir));
}
