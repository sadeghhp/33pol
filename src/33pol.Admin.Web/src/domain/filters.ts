import { sortedList, type SortSpec } from './sort';

export type KeyStatusFilter = 'active' | 'revoked' | 'archived' | 'all';

export interface ModelRow extends Record<string, unknown> {
  id?: string;
  url?: string;
  aliases?: string[];
}

export interface BackendRow extends Record<string, unknown> {
  modelId?: string;
  url?: string;
  alias?: string;
  isHealthy?: boolean;
}

export interface KeyRow extends Record<string, unknown> {
  keyPrefix?: string;
  label?: string;
  assignee?: string;
  costCenter?: string;
  isRevoked?: boolean;
  isArchived?: boolean;
  revokedAt?: string | null;
  archivedAt?: string | null;
}

function normalizeQuery(query: string): string {
  return query.trim().toLowerCase();
}

export function filterModels<T extends ModelRow>(
  models: readonly T[],
  query: string,
  sort: SortSpec,
): T[] {
  const q = normalizeQuery(query);
  let list = [...models];
  if (q) {
    list = list.filter(
      (m) =>
        (m.id || '').toLowerCase().includes(q) ||
        (m.url || '').toLowerCase().includes(q) ||
        ((m.aliases || []).join(' ')).toLowerCase().includes(q),
    );
  }
  return sortedList(list, sort);
}

export function filterBackends<T extends BackendRow>(
  backends: readonly T[],
  query: string,
  sort: SortSpec,
): T[] {
  const q = normalizeQuery(query);
  let list = [...backends];
  list.sort((a, b) => Number(a.isHealthy) - Number(b.isHealthy));
  if (q) {
    list = list.filter(
      (b) =>
        (b.modelId || '').toLowerCase().includes(q) ||
        (b.url || '').toLowerCase().includes(q) ||
        (b.alias || '').toLowerCase().includes(q),
    );
  }
  return sortedList(list, sort);
}

export function filterKeys<T extends KeyRow>(
  keys: readonly T[],
  statusFilter: KeyStatusFilter,
  textQuery: string,
  sort: SortSpec,
): T[] {
  let filtered = [...keys];
  if (statusFilter === 'active') {
    filtered = filtered.filter((k) => !k.isRevoked && !k.isArchived);
  } else if (statusFilter === 'revoked') {
    filtered = filtered.filter((k) => k.isRevoked && !k.isArchived);
  } else if (statusFilter === 'archived') {
    filtered = filtered.filter((k) => k.isArchived);
  } else if (statusFilter !== 'all') {
    filtered = filtered.filter((k) => !k.isArchived);
  }

  const q = normalizeQuery(textQuery);
  if (q) {
    filtered = filtered.filter(
      (k) =>
        (k.keyPrefix || '').toLowerCase().includes(q) ||
        (k.label || '').toLowerCase().includes(q) ||
        (k.assignee || '').toLowerCase().includes(q) ||
        (k.costCenter || '').toLowerCase().includes(q),
    );
  }
  return sortedList(filtered, sort);
}

/** Returns a debounced wrapper; the trailing call runs after `ms` of quiet time. */
export function debounceMs<T extends unknown[]>(fn: (...args: T) => void, ms: number): (...args: T) => void {
  let timer: ReturnType<typeof setTimeout> | null = null;
  return (...args: T) => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      timer = null;
      fn(...args);
    }, ms);
  };
}
