import { createSignal } from 'solid-js';
import { apiClient, handleApiError, pushToast } from './auth';

const [restricted, setRestricted] = createSignal(false);
const [selected, setSelected] = createSignal<string[]>([]);
const [registryModels, setRegistryModels] = createSignal<Array<{ id: string }>>([]);
const [loading, setLoading] = createSignal(false);
const [saving, setSaving] = createSignal(false);

export function useTenantGrantsStore() {
  return { restricted, selected, registryModels, loading, saving, toggleRestricted, toggleModel, saveGrants: saveTenantGrants };
}

async function loadRegistryModels(): Promise<void> {
  const data = await apiClient.apiJson<Array<{ model?: { id?: string }; id?: string }>>('/admin/api/models');
  const list = Array.isArray(data) ? data : [];
  setRegistryModels(
    list
      .map((item) => {
        const m = (item.model as { id?: string } | undefined) ?? item;
        return { id: String(m.id ?? '') };
      })
      .filter((m) => m.id),
  );
}

export async function loadTenantGrants(): Promise<void> {
  setLoading(true);
  try {
    if (!registryModels().length) await loadRegistryModels();
    const body = await apiClient.apiJson<{ modelIds?: string[]; usesDefaultAccess?: boolean }>(
      '/admin/api/tenant/model-grants',
    );
    const ids = body?.modelIds ?? [];
    setRestricted(!(body?.usesDefaultAccess ?? ids.length === 0));
    setSelected([...ids]);
  } catch (e) {
    handleApiError(e, 'settings');
  } finally {
    setLoading(false);
  }
}

export function toggleRestricted(on: boolean): void {
  setRestricted(on);
  if (!on) setSelected([]);
}

export function toggleModel(modelId: string): void {
  const set = new Set(selected());
  if (set.has(modelId)) set.delete(modelId);
  else set.add(modelId);
  setSelected([...set]);
}

export async function saveTenantGrants(): Promise<boolean> {
  setSaving(true);
  try {
    const payload = restricted()
      ? { modelIds: selected() }
      : { modelIds: [], allowAllModels: true };
    await apiClient.apiJson('/admin/api/tenant/model-grants', {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
    pushToast('Tenant model access updated.');
    return true;
  } catch (e) {
    handleApiError(e, 'settings');
    return false;
  } finally {
    setSaving(false);
  }
}
