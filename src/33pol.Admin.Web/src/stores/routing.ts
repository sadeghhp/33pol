import { createSignal } from 'solid-js';
import { filterBackends, filterModels, debounceMs } from '../domain/filters';
import type { SortSpec } from '../domain/sort';
import { createResource, RESOURCE_FRESH_MS } from '../realtime/resources';
import { apiClient, handleApiError, pushToast } from './auth';

const modelSort: SortSpec = { key: 'id', dir: 1 };
const backendSort: SortSpec = { key: 'modelId', dir: 1 };

const [subTab, setSubTab] = createSignal<'models' | 'backends'>('models');
const [modelsFilter, setModelsFilter] = createSignal('');
const [backendsFilter, setBackendsFilter] = createSignal('');
const [debouncedModels, setDebouncedModels] = createSignal('');
const [debouncedBackends, setDebouncedBackends] = createSignal('');
const [modelsVersion, setModelsVersion] = createSignal(0);
const [backendsVersion, setBackendsVersion] = createSignal(0);

const debounceModels = debounceMs((q: string) => setDebouncedModels(q), 400);
const debounceBackends = debounceMs((q: string) => setDebouncedBackends(q), 400);

const modelsResource = createResource<Record<string, unknown>[] | { items?: Record<string, unknown>[] }>({
  freshMs: RESOURCE_FRESH_MS.routing,
  fetch: async (signal) => {
    const data = await apiClient.apiJson('/admin/api/models', { signal });
    return data ?? [];
  },
});

const backendsResource = createResource<Record<string, unknown>[] | { items?: Record<string, unknown>[] }>({
  freshMs: RESOURCE_FRESH_MS.routing,
  fetch: async (signal) => {
    const data = await apiClient.apiJson('/admin/api/backends', { signal });
    return data ?? [];
  },
});

function normalizeList(data: unknown): Record<string, unknown>[] {
  if (Array.isArray(data)) return data;
  if (data && typeof data === 'object' && Array.isArray((data as { items?: unknown }).items)) {
    return (data as { items: Record<string, unknown>[] }).items;
  }
  return [];
}

export function useRoutingStore() {
  return {
    subTab,
    setSubTab,
    modelsFilter,
    setModelsFilter: (v: string) => {
      setModelsFilter(v);
      debounceModels(v);
    },
    backendsFilter,
    setBackendsFilter: (v: string) => {
      setBackendsFilter(v);
      debounceBackends(v);
    },
    filteredModels,
    filteredBackends,
    modelsPhase: () => modelsResource.snapshot().phase,
    backendsPhase: () => backendsResource.snapshot().phase,
    loadModels: (opts?: { force?: boolean }) => loadModels(opts),
    loadBackends: (opts?: { force?: boolean }) => loadBackends(opts),
    setModelState,
    isModelStopped,
  };
}

export function isModelStopped(m: Record<string, unknown>): boolean {
  return String(m.state ?? m.State ?? 'serving').toLowerCase() === 'stopped';
}

function filteredModels(): Record<string, unknown>[] {
  modelsVersion();
  return filterModels(normalizeList(modelsResource.snapshot().data), debouncedModels(), modelSort);
}

function filteredBackends(): Record<string, unknown>[] {
  backendsVersion();
  return filterBackends(normalizeList(backendsResource.snapshot().data), debouncedBackends(), backendSort);
}

async function loadModels(opts?: { force?: boolean; background?: boolean }): Promise<void> {
  try {
    await modelsResource.load(opts);
    setModelsVersion((v) => v + 1);
  } catch (e) {
    handleApiError(e, 'routing');
  }
}

async function loadBackends(opts?: { force?: boolean; background?: boolean }): Promise<void> {
  try {
    await backendsResource.load(opts);
    setBackendsVersion((v) => v + 1);
  } catch (e) {
    handleApiError(e, 'routing');
  }
}

export function activateRoutingPage(): void {
  const m = modelsResource.snapshot();
  const b = backendsResource.snapshot();
  if (m.phase === 'idle' || m.phase === 'stale') void loadModels({ background: m.phase === 'stale' });
  if (b.phase === 'idle' || b.phase === 'stale') void loadBackends({ background: b.phase === 'stale' });
}

export function disposeRoutingPage(): void {
  modelsResource.abort();
  backendsResource.abort();
}

export async function setModelState(id: string, action: 'stop' | 'start'): Promise<void> {
  const failed = action === 'stop' ? 'Could not stop model.' : 'Could not start model.';
  try {
    const body = await apiClient.apiJson<{ success?: boolean; message?: string }>(
      `/admin/api/models/${encodeURIComponent(id)}/${action}`,
      { method: 'POST' },
    );
    if (body?.success === false) {
      pushToast(body.message || failed, 'error');
      return;
    }
    pushToast(body?.message || (action === 'stop' ? 'Model stopped.' : 'Model started.'));
    await loadModels({ force: true });
    await loadBackends({ force: true });
  } catch (e) {
    handleApiError(e, 'routing');
    throw e;
  }
}
