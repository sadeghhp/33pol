export interface ReconcileResult<T> {
  value: T;
  changedPaths: string[];
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value != null && typeof value === 'object' && !Array.isArray(value);
}

function valuesEqual(a: unknown, b: unknown): boolean {
  if (Object.is(a, b)) return true;
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return false;
    return a.every((item, index) => valuesEqual(item, b[index]));
  }
  if (isPlainObject(a) && isPlainObject(b)) {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    for (const key of keys) {
      if (!valuesEqual(a[key], b[key])) return false;
    }
    return true;
  }
  return false;
}

function collectChangedPaths(
  prev: unknown,
  next: unknown,
  prefix = '',
  changed: string[] = [],
): string[] {
  if (valuesEqual(prev, next)) return changed;

  if (!isPlainObject(prev) || !isPlainObject(next)) {
    changed.push(prefix || '.');
    return changed;
  }

  const keys = new Set([...Object.keys(prev), ...Object.keys(next)]);
  for (const key of keys) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (!valuesEqual(prev[key], next[key])) {
      if (isPlainObject(prev[key]) && isPlainObject(next[key])) {
        collectChangedPaths(prev[key], next[key], path, changed);
      } else {
        changed.push(path);
      }
    }
  }
  return changed;
}

/** Path-level diff for summary objects so downstream stores can notify only changed leaves. */
export function reconcileSummary<T extends Record<string, unknown>>(
  previous: T | null | undefined,
  incoming: T | null | undefined,
): ReconcileResult<T | null> {
  if (incoming == null) {
    return { value: null, changedPaths: previous == null ? [] : ['.'] };
  }
  if (previous == null) {
    return { value: incoming, changedPaths: ['.'] };
  }
  const changedPaths = collectChangedPaths(previous, incoming);
  if (changedPaths.length === 0) {
    return { value: previous, changedPaths: [] };
  }
  return { value: { ...previous, ...incoming }, changedPaths };
}

export interface ReconcileByIdResult<T> {
  byId: Record<string, T>;
  order: string[];
  changedIds: string[];
  removedIds: string[];
}

/** Merge an incoming array into a keyed map, preserving unchanged row object references. */
export function reconcileById<T extends Record<string, unknown>>(
  previousById: Record<string, T>,
  incoming: readonly T[],
  idKey: keyof T & string,
): ReconcileByIdResult<T> {
  const byId: Record<string, T> = { ...previousById };
  const order: string[] = [];
  const changedIds: string[] = [];
  const incomingIds = new Set<string>();

  for (const row of incoming) {
    const id = String(row[idKey] ?? '');
    if (!id) continue;
    incomingIds.add(id);
    order.push(id);
    const prev = byId[id];
    if (!prev) {
      byId[id] = row as T;
      changedIds.push(id);
    } else if (!valuesEqual(prev, row)) {
      byId[id] = isPlainObject(prev) && isPlainObject(row) ? ({ ...prev, ...row } as T) : ({ ...row } as T);
      changedIds.push(id);
    }
  }

  const removedIds: string[] = [];
  for (const id of Object.keys(byId)) {
    if (!incomingIds.has(id)) {
      delete byId[id];
      removedIds.push(id);
    }
  }

  return { byId, order, changedIds, removedIds };
}

/** Convenience wrapper for request feed rows keyed by requestId. */
export function reconcileRequests<T extends Record<string, unknown> & { requestId?: string }>(
  previousById: Record<string, T>,
  incoming: readonly T[],
): ReconcileByIdResult<T> {
  return reconcileById(previousById, incoming, 'requestId');
}
