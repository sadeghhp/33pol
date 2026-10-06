import { createSignal } from 'solid-js';
import { createStore } from 'solid-js/store';
import { mergePinnedIntoFeed } from '../domain/pinnedMerge';
import { reconcileRequests } from '../domain/reconcile';
import type { ConnectionSource, LiveRequestRow } from '../realtime/types';
import { snapshot as connectionSnapshot } from './connectionWriter';

type RequestRow = LiveRequestRow & Record<string, unknown>;

const [byId, setById] = createStore<Record<string, RequestRow>>({});
const [order, setOrder] = createSignal<string[]>([]);
const [pinnedIds, setPinnedIds] = createSignal<string[]>([]);
const [paused, setPaused] = createSignal(false);
const [parkedFrame, setParkedFrame] = createSignal<RequestRow[] | null>(null);
const [expandedId, setExpandedId] = createSignal<string | null>(null);
const [modelFilter, setModelFilter] = createSignal('');
const [tenantFilter, setTenantFilter] = createSignal('');
const [statusFilter, setStatusFilter] = createSignal('');
const [slowOnly, setSlowOnly] = createSignal(false);

const seenIds = new Map<string, number>();
/** Snapshot of pinned rows evicted from the live feed window (legacy PINNED_REQUESTS). */
const pinnedSnapshots = new Map<string, RequestRow>();
/** Ids present in the last server frame (before pinned merge). */
let lastLiveIds = new Set<string>();

export function useRequestsStore() {
  return {
    byId,
    order,
    pinnedIds,
    paused,
    parkedFrame,
    expandedId,
    modelFilter,
    tenantFilter,
    statusFilter,
    slowOnly,
    setPaused,
    setParkedFrame,
    setExpandedId,
    setModelFilter: (v: string) => {
      setModelFilter(v);
      setOrder((o) => filterOrder(o, byId));
    },
    setTenantFilter: (v: string) => {
      setTenantFilter(v);
      setOrder((o) => filterOrder(o, byId));
    },
    setStatusFilter: (v: string) => {
      setStatusFilter(v);
      setOrder((o) => filterOrder(o, byId));
    },
    setSlowOnly: (v: boolean) => {
      setSlowOnly(v);
      setOrder((o) => filterOrder(o, byId));
    },
    togglePin,
    visibleOrder,
    isNewRow,
  };
}

export function applyRequestsFromSource(incoming: readonly RequestRow[], source: ConnectionSource): void {
  const snap = connectionSnapshot();
  if (snap.source && snap.source !== source) return;
  if (paused()) {
    setParkedFrame([...incoming]);
    return;
  }
  mergeRequests(incoming);
}

function mergeRequests(incoming: readonly RequestRow[]): void {
  const prev = { ...byId };
  const pinned = new Set(pinnedIds());
  const { byId: nextById, order: nextOrder, changedIds } = reconcileRequests(prev, incoming);

  Object.assign(nextById, mergePinnedIntoFeed(prev, nextById, pinned, pinnedSnapshots));

  const now = Date.now();
  for (const id of changedIds) {
    if (!seenIds.has(id)) seenIds.set(id, now);
  }
  lastLiveIds = new Set(nextOrder);
  setById(nextById);
  setOrder(filterOrder(nextOrder, nextById));
}

function filterOrder(ids: string[], map: Record<string, RequestRow>): string[] {
  const qModel = modelFilter().trim().toLowerCase();
  const qTenant = tenantFilter().trim().toLowerCase();
  const qStatus = statusFilter().trim().toLowerCase();
  const slow = slowOnly();
  const pinned = new Set(pinnedIds());

  let list = ids.filter((id) => {
    const row = map[id];
    if (!row) return false;
    if (qModel && !String(row.modelId ?? row.model ?? '').toLowerCase().includes(qModel)) return false;
    if (qTenant && !String(row.tenantId ?? row.tenant ?? '').toLowerCase().includes(qTenant)) return false;
    if (qStatus && !String(row.status ?? row.statusCode ?? '').toLowerCase().includes(qStatus)) return false;
    if (slow && Number(row.durationMs ?? 0) < 5000) return false;
    return true;
  });

  const pinnedFirst = [...pinned].filter((id) => map[id]);
  const rest = list.filter((id) => !pinned.has(id));
  list = [...pinnedFirst, ...rest];
  return list.slice(0, 25);
}

export function visibleOrder(): string[] {
  return filterOrder(order(), byId);
}

export function togglePin(id: string): void {
  const wasPinned = pinnedIds().includes(id);
  if (wasPinned) {
    pinnedSnapshots.delete(id);
    setPinnedIds((prev) => prev.filter((x) => x !== id));
    if (!lastLiveIds.has(id)) {
      setById((current) => {
        const next = { ...current };
        delete next[id];
        return next;
      });
    }
  } else {
    const row = byId[id];
    if (row) pinnedSnapshots.set(id, { ...row });
    setPinnedIds((prev) => [...prev, id]);
  }
  setOrder((o) => filterOrder(o, byId));
}

export function isNewRow(id: string, maxAgeMs = 3000): boolean {
  const seen = seenIds.get(id);
  if (!seen) return false;
  return Date.now() - seen < maxAgeMs;
}

export function resumeFromPause(): void {
  const frame = parkedFrame();
  setPaused(false);
  setParkedFrame(null);
  if (frame) mergeRequests(frame);
}

export function clearRequests(): void {
  setById({});
  setOrder([]);
  seenIds.clear();
}
