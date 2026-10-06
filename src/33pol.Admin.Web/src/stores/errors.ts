import { createSignal } from 'solid-js';
import { createStore } from 'solid-js/store';
import { debounceMs } from '../domain/filters';
import { createResource, RESOURCE_FRESH_MS } from '../realtime/resources';
import { resetVitalsErrorCounters } from './vitalsHistory';
import { apiClient, handleApiError, pushToast } from './auth';

export interface ErrorGroup extends Record<string, unknown> {
  fingerprint?: string;
  code?: string;
  level?: string;
  message?: string;
  count?: number;
  lastSeenUtc?: string;
  firstSeenUtc?: string;
  modelId?: string;
  statusCode?: number;
  errorCode?: string;
  endpointMethod?: string;
  endpointPath?: string;
}

export interface ErrorFacets {
  models?: { value: string; count: number }[];
  statusCodes?: { value: string; count: number }[];
  errorCodes?: { value: string; count: number }[];
}

const [search, setSearch] = createSignal('');
const [debouncedSearch, setDebouncedSearch] = createSignal('');
const [modelFilter, setModelFilter] = createSignal('');
const [statusFilter, setStatusFilter] = createSignal('');
const [codeFilter, setCodeFilter] = createSignal('');
const [levelFilter, setLevelFilter] = createSignal('all');
const [range, setRange] = createSignal('24h');
const [autoRefresh, setAutoRefresh] = createSignal(true);
const [byId, setById] = createStore<Record<string, ErrorGroup>>({});
const [order, setOrder] = createSignal<string[]>([]);
const [expandedId, setExpandedId] = createSignal<string | null>(null);
const [occurrences, setOccurrences] = createStore<Record<string, Record<string, unknown>[]>>({});
const [facets, setFacets] = createSignal<ErrorFacets | null>(null);
const [facetsError, setFacetsError] = createSignal(false);
const [active, setActive] = createSignal(false);
const [groupsTotal, setGroupsTotal] = createSignal(0);

const applyDebounced = debounceMs((q: string) => setDebouncedSearch(q), 400);

function rangeParams(extra?: Record<string, string>): URLSearchParams {
  const params = new URLSearchParams({ limit: '50', offset: '0' });
  const q = debouncedSearch().trim();
  if (q) params.set('search', q);
  if (modelFilter().trim()) params.set('modelId', modelFilter().trim());
  if (statusFilter().trim()) params.set('status', statusFilter().trim());
  if (codeFilter().trim()) params.set('code', codeFilter().trim());
  const level = levelFilter();
  if (level && level !== 'all') params.set('level', level);
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

function facetsQueryString(): string {
  const params = new URLSearchParams();
  const r = range();
  if (r !== 'all') {
    const hours: Record<string, number> = { '1h': 1, '24h': 24, '7d': 168, '30d': 720 };
    const h = hours[r];
    if (h) params.set('from', new Date(Date.now() - h * 3600000).toISOString());
  }
  const q = params.toString();
  return q ? `?${q}` : '';
}

const resource = createResource<{ groups?: ErrorGroup[]; total?: number }>({
  freshMs: RESOURCE_FRESH_MS.errors,
  fetch: async (signal) => {
    const data = await apiClient.apiJson<{ groups?: ErrorGroup[]; total?: number }>(
      `/admin/api/errors/groups?${rangeParams()}`,
      { signal },
    );
    return data ?? { groups: [] };
  },
});

export function facetOptions(values: { value: string; count: number }[] | undefined) {
  return (values ?? []).map((f) => ({
    value: f.value,
    label: `${f.value} (${Number(f.count).toLocaleString()})`,
  }));
}

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
    levelFilter,
    setLevelFilter,
    autoRefresh,
    setAutoRefresh,
    byId,
    order,
    expandedId,
    setExpandedId,
    occurrences,
    facets,
    facetsError,
    groupsTotal,
    phase: () => resource.snapshot().phase,
    load: (opts?: { force?: boolean; background?: boolean }) => loadErrors(opts),
    loadFacets,
    loadOccurrences,
    exportErrors,
    clearErrors,
    setActive,
    applyFilters,
  };
}

export async function loadFacets(): Promise<void> {
  try {
    const data = await apiClient.apiJson<ErrorFacets>(`/admin/api/errors/facets${facetsQueryString()}`);
    setFacets(data);
    setFacetsError(false);
  } catch {
    setFacets(null);
    setFacetsError(true);
  }
}

export function applyFilters(): void {
  void loadErrors({ background: true });
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
    setOccurrences({});
    setGroupsTotal(0);
    resetVitalsErrorCounters();
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
    const groups = data.groups ?? [];
    const map: Record<string, ErrorGroup> = {};
    const ids: string[] = [];
    for (const row of groups) {
      const id = String(row.fingerprint ?? row.code ?? row.message);
      map[id] = row;
      ids.push(id);
    }
    setById(map);
    setOrder(ids);
    setGroupsTotal(Number(data.total ?? groups.length));
  } catch (e) {
    handleApiError(e, 'errors');
  }
}

export async function loadOccurrences(fingerprint: string): Promise<void> {
  if (occurrences[fingerprint]) return;
  try {
    const params = rangeParams({ fingerprint, limit: '20', offset: '0' });
    const data = await apiClient.apiJson<{ occurrences?: Record<string, unknown>[] }>(
      `/admin/api/errors?${params}`,
    );
    setOccurrences(fingerprint, data?.occurrences ?? []);
  } catch (e) {
    handleApiError(e, 'errors');
    setOccurrences(fingerprint, []);
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
  void loadFacets();
  const snap = resource.snapshot();
  if (snap.phase === 'idle' || snap.phase === 'stale') void loadErrors({ background: snap.phase === 'stale' });
}
