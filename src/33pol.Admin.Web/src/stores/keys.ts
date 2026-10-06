import { createSignal } from 'solid-js';
import { createStore } from 'solid-js/store';
import { filterKeys, type KeyStatusFilter, debounceMs } from '../domain/filters';
import type { SortSpec } from '../domain/sort';
import { createResource, RESOURCE_FRESH_MS } from '../realtime/resources';
import { apiClient, handleApiError, pushToast } from './auth';

export interface NewKeyDraft {
  role: string;
  label: string;
  assignee: string;
  description: string;
  costCenter: string;
}

export interface KeyEditDraft {
  id: string;
  keyPrefix: string;
  label: string;
  assignee: string;
  description: string;
  costCenter: string;
}

export const DEFAULT_KEY_EDIT: KeyEditDraft = {
  id: '',
  keyPrefix: '',
  label: '',
  assignee: '',
  description: '',
  costCenter: '',
};

export const DEFAULT_NEW_KEY: NewKeyDraft = {
  role: 'Inference',
  label: '',
  assignee: '',
  description: '',
  costCenter: '',
};

export const KEYS_RENDER_CAP = 50;
const [sortSpec, setSortSpec] = createSignal<SortSpec>({ key: 'createdAt', dir: -1 });
const [selectedIds, setSelectedIds] = createSignal<Set<string>>(new Set());

const [statusFilter, setStatusFilter] = createSignal<KeyStatusFilter>('active');
const [textFilter, setTextFilter] = createSignal('');
const [debouncedText, setDebouncedText] = createSignal('');
const applyDebounced = debounceMs((q: string) => setDebouncedText(q), 400);
const [byId, setById] = createStore<Record<string, Record<string, unknown>>>({});
const [order, setOrder] = createSignal<string[]>([]);

export function normalizeKeysListResponse(data: unknown): Record<string, unknown>[] {
  if (Array.isArray(data)) return data;
  if (data && typeof data === 'object' && Array.isArray((data as { items?: unknown }).items)) {
    return (data as { items: Record<string, unknown>[] }).items;
  }
  return [];
}

const resource = createResource<{ items?: Record<string, unknown>[] }>({
  freshMs: RESOURCE_FRESH_MS.keys,
  fetch: async (signal) => {
    const data = await apiClient.apiJson<unknown>('/admin/api/keys', { signal });
    return { items: normalizeKeysListResponse(data) };
  },
});

export function useKeysStore() {
  return {
    statusFilter,
    setStatusFilter,
    textFilter,
    setTextFilter: (v: string) => {
      setTextFilter(v);
      applyDebounced(v);
    },
    filteredRows,
    totalMatches,
    capped,
    phase: () => resource.snapshot().phase,
    sortSpec,
    setSortSpec,
    toggleSort,
    selectedIds,
    toggleSelected,
    clearSelected,
    load: (opts?: { force?: boolean }) => loadKeys(opts),
    revokeKey,
    bulkRevoke,
    archiveKey,
    unarchiveKey,
    deleteKey,
    createKey,
    updateKey,
  };
}

export function toggleSort(key: string): void {
  setSortSpec((prev) => {
    if (prev.key === key) return { key, dir: (prev.dir === 1 ? -1 : 1) as SortSpec['dir'] };
    return { key, dir: -1 };
  });
}

export function toggleSelected(id: string): void {
  setSelectedIds((prev) => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    return next;
  });
}

export function clearSelected(): void {
  setSelectedIds(new Set<string>());
}

function normalizeKey(row: Record<string, unknown>): Record<string, unknown> {
  const isRevoked = !!(row.isRevoked ?? row.revokedAt ?? String(row.status ?? '').toLowerCase() === 'revoked');
  const isArchived = !!(row.isArchived ?? row.archivedAt ?? String(row.status ?? '').toLowerCase() === 'archived');
  return {
    ...row,
    isRevoked,
    isArchived,
    canArchive: !!(row.canArchive ?? (isRevoked && !isArchived)),
    canDelete: !!row.canDelete,
    canUnarchive: isArchived,
    active: !isRevoked && !isArchived,
  };
}

function syncKeysFromItems(items: Record<string, unknown>[]): void {
  const map: Record<string, Record<string, unknown>> = {};
  const ids: string[] = [];
  for (const row of items.map(normalizeKey)) {
    const id = String(row.id ?? '');
    if (!id) continue;
    map[id] = row;
    ids.push(id);
  }
  setById(map);
  setOrder(ids);
}

function allRows(): Record<string, unknown>[] {
  return order()
    .map((id) => byId[id])
    .filter(Boolean);
}

function filteredRows(): Record<string, unknown>[] {
  return filterKeys(allRows(), statusFilter(), debouncedText(), sortSpec());
}

function totalMatches(): number {
  return filteredRows().length;
}

function capped(): Record<string, unknown>[] {
  return filteredRows().slice(0, KEYS_RENDER_CAP);
}

export async function revokeKey(id: string): Promise<void> {
  try {
    await apiClient.apiFetch(`/admin/api/keys/${encodeURIComponent(id)}/revoke`, { method: 'POST' });
    await loadKeys({ force: true });
  } catch (e) {
    handleApiError(e, 'keys');
    throw e;
  }
}

export async function bulkRevoke(ids: string[]): Promise<void> {
  if (!ids.length) return;
  try {
    await apiClient.apiJson('/admin/api/keys/revoke', {
      method: 'POST',
      body: JSON.stringify({ keyIds: ids }),
    });
    pushToast(`${ids.length} key(s) revoked.`);
    clearSelected();
    await loadKeys({ force: true });
  } catch (e) {
    handleApiError(e, 'keys');
    throw e;
  }
}

export async function updateKey(draft: KeyEditDraft): Promise<void> {
  if (!draft.id) return;
  try {
    await apiClient.apiJson(`/admin/api/keys/${encodeURIComponent(draft.id)}`, {
      method: 'PATCH',
      body: JSON.stringify({
        label: draft.label || null,
        assignee: draft.assignee || null,
        description: draft.description || null,
        costCenter: draft.costCenter || null,
      }),
    });
    pushToast('API key updated.');
    await loadKeys({ force: true });
  } catch (e) {
    handleApiError(e, 'keys');
    throw e;
  }
}

export async function archiveKey(id: string): Promise<void> {
  try {
    await apiClient.apiFetch(`/admin/api/keys/${id}/archive`, { method: 'POST' });
    await loadKeys({ force: true });
  } catch (e) {
    handleApiError(e, 'keys');
    throw e;
  }
}

export async function unarchiveKey(id: string): Promise<void> {
  try {
    await apiClient.apiFetch(`/admin/api/keys/${id}/unarchive`, { method: 'POST' });
    await loadKeys({ force: true });
  } catch (e) {
    handleApiError(e, 'keys');
    throw e;
  }
}

export async function deleteKey(id: string, confirmKeyPrefix: string): Promise<void> {
  try {
    await apiClient.apiFetch(`/admin/api/keys/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      body: JSON.stringify({ confirmKeyPrefix }),
    });
    pushToast('API key deleted permanently. Its history is kept.');
    await loadKeys({ force: true });
  } catch (e) {
    handleApiError(e, 'keys');
    throw e;
  }
}

export async function createKey(draft: NewKeyDraft): Promise<string> {
  try {
    const body = await apiClient.apiJson<{ secret?: string }>('/admin/api/keys', {
      method: 'POST',
      body: JSON.stringify({
        role: draft.role,
        scopes: [],
        label: draft.label || null,
        assignee: draft.assignee || null,
        description: draft.description || null,
        costCenter: draft.costCenter || null,
      }),
    });
    pushToast('API key created — copy the secret now.');
    await loadKeys({ force: true });
    return body?.secret ?? '';
  } catch (e) {
    handleApiError(e, 'keys');
    throw e;
  }
}

export async function loadKeys(opts?: { force?: boolean; background?: boolean }): Promise<void> {
  try {
    const data = await resource.load(opts);
    if (!data) return;
    syncKeysFromItems(data.items ?? []);
  } catch (e) {
    handleApiError(e, 'keys');
  }
}

export function activateKeysPage(): void {
  const snap = resource.snapshot();
  if (snap.phase === 'idle' || snap.phase === 'stale') void loadKeys({ background: snap.phase === 'stale' });
}

export function disposeKeysPage(): void {
  resource.abort();
}
