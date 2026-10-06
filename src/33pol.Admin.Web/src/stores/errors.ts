import { createSignal } from 'solid-js';
import { createStore } from 'solid-js/store';
import { debounceMs } from '../domain/filters';
import { createResource, RESOURCE_FRESH_MS } from '../realtime/resources';
import { apiClient, handleApiError, pushToast } from './auth';

export interface ErrorGroup extends Record<string, unknown> {
  fingerprint?: string;
  code?: string;
  message?: string;
  count?: number;
  lastSeenUtc?: string;
}

const [search, setSearch] = createSignal('');
const [debouncedSearch, setDebouncedSearch] = createSignal('');
const [modelFilter, setModelFilter] = createSignal('');
const [statusFilter, setStatusFilter] = createSignal('');
const [codeFilter, setCodeFilter] = createSignal('');
const [range, setRange] = createSignal('24h');
const [autoRefresh, setAutoRefresh] = createSignal(true);
const [byId, setById] = createStore<Record<string, ErrorGroup>>({});
const [order, setOrder] = createSignal<string[]>([]);
const [expandedId, setExpandedId] = createSignal<string | null>(null);
const [occurrences, setOccurrences] = createStore<Record<string, Record<string, unknown>[]>>({});
const [active, setActive] = createSignal(false);

const applyDebounced = debounceMs((q: string) => setDebouncedSearch(q), 400);

function rangeParams(extra?: Record<string, string>): URLSearchParams {
  const params = new URLSearchParams({ limit: '50', offset: '0' });
  const q = debouncedSearch().trim();
  if (q) params.set('search', q);
  if (modelFilter().trim()) params.set('modelId', modelFilter().trim());
  if (statusFilter().trim()) params.set('status', statusFilter().trim());
  if (codeFilter().trim()) params.set('code', codeFilter().trim());
  const r = range();
  if (r !== 'all') {
    const hours: Record<string, number> = { '1h': 1, '24h': 24, '7d': 168, '30d': 720 };
    const h = hours[r];
    if (h) {
      const from = new Date(Date.now() - h * 3600000).toISOString();
      params.set('from', from);
    }
  }
  if (extra) {
    for (const [k, v] of Object.entries(extra)) params.set(k, v);
  }
  return params;
}

export function errorsQueryString(extra?: Record<string, string>): string {
  return `?${rangeParams(extra).toString()}`;
}

const resource = createResource<{ items?: ErrorGroup[] }>({
  freshMs: RESOURCE_FRESH_MS.errors,
  fetch: async (signal) => {
    const data = await apiClient.apiJson<{ items?: ErrorGroup[] }>(
      `/admin/api/errors/groups?${rangeParams()}`,
      { signal },
    );
    return data ?? { items: [] };
  },
});

export function useErrorsStore() {
  return {
    search,
    setSearch: (v: string) => {
      setSearch(v);
      applyDebounced(v);
    },
    range,
    setRange,
    modelFilter,
    setModelFilter,
    statusFilter,
    setStatusFilter,
    codeFilter,
    setCodeFilter,
    autoRefresh,
    setAutoRefresh,
    byId,
    order,
    expandedId,
    setExpandedId,
    occurrences,
    phase: () => resource.snapshot().phase,
    load: (opts?: { force?: boolean }) => loadErrors(opts),
    loadOccurrences,
    exportErrors,
    clearErrors,
    setActive,
  };
}

export async function exportErrors(format: 'json' | 'csv'): Promise<void> {
  const ext = format === 'csv' ? 'csv' : 'json';
  try {
    await apiClient.downloadBlob(
      `/admin/api/errors/export${errorsQueryString({ format, limit: '5000', offset: '0' })}`,
      `errors-export.${ext}`,
    );
    pushToast('Export downloaded.');
  } catch (e) {
    handleApiError(e, 'errors');
    throw e;
  }
}

export async function clearErrors(): Promise<void> {
  try {
    await apiClient.apiJson('/admin/api/errors?confirm=true', { method: 'DELETE' });
    setById({});
    setOrder([]);
    pushToast('All recorded errors cleared.');
  } catch (e) {
    handleApiError(e, 'errors');
    throw e;
  }
}

export async function loadErrors(opts?: { force?: boolean; background?: boolean }): Promise<void> {
  try {
    const data = await resource.load(opts);
    if (!data) return;
    const items = data.items ?? [];
    const map: Record<string, ErrorGroup> = {};
    const ids: string[] = [];
    for (const row of items) {
      const id = String(row.fingerprint ?? row.code ?? row.message);
      map[id] = row;
      ids.push(id);
    }
    setById(map);
    setOrder(ids);
  } catch (e) {
    handleApiError(e, 'errors');
  }
}

export async function loadOccurrences(fingerprint: string): Promise<void> {
  if (occurrences[fingerprint]) return;
  try {
    const data = await apiClient.apiJson<{ items?: Record<string, unknown>[] }>(
      `/admin/api/errors/groups/${encodeURIComponent(fingerprint)}/occurrences?limit=20`,
    );
    setOccurrences(fingerprint, data?.items ?? []);
  } catch (e) {
    handleApiError(e, 'errors');
  }
}

export function refreshErrorsIfActive(): void {
  if (!active() || !autoRefresh()) return;
  void loadErrors({ background: true });
}

export function disposeErrorsPage(): void {
  setActive(false);
  resource.abort();
}

export function activateErrorsPage(): void {
  setActive(true);
  const snap = resource.snapshot();
  if (snap.phase === 'idle' || snap.phase === 'stale') void loadErrors({ background: snap.phase === 'stale' });
}
