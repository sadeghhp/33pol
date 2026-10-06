import { createSignal } from 'solid-js';
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

export const DEFAULT_NEW_KEY: NewKeyDraft = {
  role: 'Inference',
  label: '',
  assignee: '',
  description: '',
  costCenter: '',
};

export const KEYS_RENDER_CAP = 50;
const sort: SortSpec = { key: 'createdAt', dir: -1 };

const [statusFilter, setStatusFilter] = createSignal<KeyStatusFilter>('active');
const [textFilter, setTextFilter] = createSignal('');
const [debouncedText, setDebouncedText] = createSignal('');
const applyDebounced = debounceMs((q: string) => setDebouncedText(q), 400);

const resource = createResource<{ items?: Record<string, unknown>[] }>({
  freshMs: RESOURCE_FRESH_MS.keys,
  fetch: async (signal) => {
    const data = await apiClient.apiJson<{ items?: Record<string, unknown>[] }>('/admin/api/keys', { signal });
    return data ?? { items: [] };
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
    load: (opts?: { force?: boolean }) => loadKeys(opts),
    revokeKey,
    archiveKey,
    unarchiveKey,
    deleteKey,
    createKey,
  };
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

function allRows(): Record<string, unknown>[] {
  return (resource.snapshot().data?.items ?? []).map(normalizeKey);
}

function filteredRows(): Record<string, unknown>[] {
  return filterKeys(allRows(), statusFilter(), debouncedText(), sort);
}

function totalMatches(): number {
  return filteredRows().length;
}

function capped(): Record<string, unknown>[] {
  return filteredRows().slice(0, KEYS_RENDER_CAP);
}

export async function revokeKey(id: string): Promise<void> {
  try {
    await apiClient.apiFetch(`/admin/api/keys/${id}/revoke`, { method: 'POST' });
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
    await resource.load(opts);
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
