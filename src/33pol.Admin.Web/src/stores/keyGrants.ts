import { createSignal } from 'solid-js';
import { buildReplaceGrantsPayload, type ModelGrantsResponse } from '../domain/keyGrants';
import { apiClient, handleApiError, pushToast } from './auth';

const [drawerOpen, setDrawerOpen] = createSignal(false);
const [keyId, setKeyId] = createSignal('');
const [keyLabel, setKeyLabel] = createSignal('');
const [selected, setSelected] = createSignal<string[]>([]);
const [registryModels, setRegistryModels] = createSignal<Array<{ id: string; label: string }>>([]);
const [loading, setLoading] = createSignal(false);
const [saving, setSaving] = createSignal(false);

export function useKeyGrantsStore() {
  return {
    drawerOpen,
    keyId,
    keyLabel,
    selected,
    setSelected,
    registryModels,
    loading,
    saving,
    closeDrawer,
    toggleModel,
    saveGrants,
  };
}

async function loadRegistryModels(): Promise<void> {
  try {
    const data = await apiClient.apiJson<Array<{ model?: { id?: string }; id?: string }>>('/admin/api/models');
    const list = Array.isArray(data) ? data : [];
    setRegistryModels(
      list.map((item) => {
        const m = (item.model as { id?: string } | undefined) ?? item;
        const id = String(m.id ?? '');
        return { id, label: id };
      }).filter((m) => m.id),
    );
  } catch (e) {
    handleApiError(e, 'keys');
  }
}

export async function openKeyAccessDrawer(key: Record<string, unknown>): Promise<void> {
  const id = String(key.id ?? '');
  if (!id) return;
  setKeyId(id);
  setKeyLabel(String(key.label ?? key.keyPrefix ?? id));
  setSelected([]);
  setDrawerOpen(true);
  setLoading(true);
  try {
    if (!registryModels().length) await loadRegistryModels();
    const body = await apiClient.apiJson<ModelGrantsResponse>(
      `/admin/api/keys/${encodeURIComponent(id)}/model-grants`,
    );
    setSelected([...(body?.modelIds ?? [])]);
  } catch (e) {
    handleApiError(e, 'keys');
  } finally {
    setLoading(false);
  }
}

export function closeDrawer(): void {
  setDrawerOpen(false);
  setKeyId('');
  setKeyLabel('');
  setSelected([]);
}

export function toggleModel(modelId: string): void {
  const set = new Set(selected());
  if (set.has(modelId)) set.delete(modelId);
  else set.add(modelId);
  setSelected([...set]);
}

export async function saveGrants(): Promise<boolean> {
  const id = keyId();
  if (!id) return false;
  setSaving(true);
  try {
    await apiClient.apiJson(`/admin/api/keys/${encodeURIComponent(id)}/model-grants`, {
      method: 'PUT',
      body: JSON.stringify(buildReplaceGrantsPayload(selected())),
    });
    pushToast('Model access updated.');
    closeDrawer();
    return true;
  } catch (e) {
    handleApiError(e, 'keys');
    return false;
  } finally {
    setSaving(false);
  }
}
