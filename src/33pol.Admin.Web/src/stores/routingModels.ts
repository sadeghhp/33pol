import { createSignal } from 'solid-js';
import {
  DEFAULT_MODEL_DRAFT,
  buildModelWriteBody,
  draftFromModel,
  normalizeModelItem,
  validateModelDraft,
  type ModelEditDraft,
  type ModelRow,
  type ModelTestResult,
  type ModelTypeDescriptor,
} from '../domain/routingModels';
import { apiClient, handleApiError, pushToast } from './auth';

const [drawerOpen, setDrawerOpen] = createSignal(false);
const [draft, setDraft] = createSignal<ModelEditDraft>({ ...DEFAULT_MODEL_DRAFT });
const [fieldError, setFieldError] = createSignal('');
const [saving, setSaving] = createSignal(false);
const [modelTypes, setModelTypes] = createSignal<ModelTypeDescriptor[]>([]);
const [testDialog, setTestDialog] = createSignal<{
  modelId: string;
  loading: boolean;
  result: ModelTestResult | null;
  error: string;
} | null>(null);

let saveInFlight = false;

export function useRoutingModelsStore() {
  return {
    drawerOpen,
    draft,
    setDraft,
    fieldError,
    saving,
    modelTypes,
    testDialog,
    openNewModelDrawer,
    openEditModelDrawer,
    closeModelDrawer,
    saveModel,
    removeModel,
    testModel,
    rerunModelTest,
    closeTestDialog,
    loadModelTypes,
  };
}

export async function loadModelTypes(): Promise<void> {
  try {
    const data = await apiClient.apiJson<ModelTypeDescriptor[]>('/admin/api/model-types');
    setModelTypes(Array.isArray(data) ? data : []);
  } catch (e) {
    handleApiError(e, 'routing');
  }
}

export function openNewModelDrawer(): void {
  setDraft({ ...DEFAULT_MODEL_DRAFT });
  setFieldError('');
  setDrawerOpen(true);
  void loadModelTypes();
}

export function openEditModelDrawer(model: Record<string, unknown>): void {
  const row = normalizeModelItem(model);
  setDraft(draftFromModel(row, modelTypes()));
  setFieldError('');
  setDrawerOpen(true);
  void loadModelTypes();
}

export function closeModelDrawer(): void {
  setDrawerOpen(false);
  setFieldError('');
}

export async function saveModel(onSaved?: () => void): Promise<boolean> {
  if (saveInFlight) return false;
  const current = draft();
  const err = validateModelDraft(current);
  if (err) {
    setFieldError(err);
    return false;
  }
  saveInFlight = true;
  setSaving(true);
  setFieldError('');
  try {
    const body = buildModelWriteBody(current);
    const isEdit = current._existing;
    const urlId = encodeURIComponent(isEdit ? current._originalId || current.id : body.model.id);
    const result = await apiClient.apiJson<{ success?: boolean; message?: string }>(
      isEdit ? `/admin/api/models/${urlId}` : '/admin/api/models',
      { method: isEdit ? 'PATCH' : 'POST', body: JSON.stringify(body) },
    );
    if (result?.success === false) {
      setFieldError(result.message || 'Could not save model.');
      return false;
    }
    pushToast(result?.message || (isEdit ? 'Model updated.' : 'Model added.'));
    closeModelDrawer();
    onSaved?.();
    return true;
  } catch (e) {
    handleApiError(e, 'routing');
    return false;
  } finally {
    saveInFlight = false;
    setSaving(false);
  }
}

export async function removeModel(id: string, onRemoved?: () => void): Promise<boolean> {
  try {
    const result = await apiClient.apiJson<{ success?: boolean; message?: string }>(
      `/admin/api/models/${encodeURIComponent(id)}`,
      { method: 'DELETE' },
    );
    if (result?.success === false) {
      pushToast(result.message || 'Could not remove model.', 'error');
      return false;
    }
    pushToast(result?.message || 'Model removed.');
    onRemoved?.();
    return true;
  } catch (e) {
    handleApiError(e, 'routing');
    return false;
  }
}

export async function testModel(modelId: string): Promise<void> {
  if (!modelId) return;
  setTestDialog({ modelId, loading: true, result: null, error: '' });
  try {
    const result = await apiClient.apiJson<ModelTestResult>(
      `/admin/api/models/${encodeURIComponent(modelId)}/test`,
      { method: 'POST', body: JSON.stringify({}) },
    );
    setTestDialog({ modelId, loading: false, result, error: '' });
    if (result?.ok) pushToast('Model test succeeded.');
    else if (result?.supported === false) pushToast('No health check for this model type.', 'error');
    else if (result) pushToast(result.detail || 'Model test failed.', 'error');
  } catch (e) {
    const err = e as { message?: string };
    setTestDialog({
      modelId,
      loading: false,
      result: null,
      error: err.message || String(e),
    });
  }
}

export function rerunModelTest(): void {
  const dialog = testDialog();
  if (dialog?.modelId) void testModel(dialog.modelId);
}

export function closeTestDialog(): void {
  setTestDialog(null);
}

export function asModelRow(m: Record<string, unknown>): ModelRow {
  return normalizeModelItem(m);
}
