import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./auth', () => ({
  apiClient: { apiJson: vi.fn() },
  handleApiError: vi.fn(),
  pushToast: vi.fn(),
}));

import { apiClient, pushToast } from './auth';
import {
  closeModelDrawer,
  openNewModelDrawer,
  removeModel,
  saveModel,
  testModel,
  useRoutingModelsStore,
} from './routingModels';

describe('routingModels store', () => {
  beforeEach(() => {
    vi.mocked(apiClient.apiJson).mockReset();
    vi.mocked(pushToast).mockReset();
    closeModelDrawer();
  });

  it('saveModel_postsNewModel', async () => {
    openNewModelDrawer();
    const store = useRoutingModelsStore();
    store.setDraft({
      ...store.draft(),
      id: 'test-model',
      url: 'http://host.docker.internal:8080',
    });
    vi.mocked(apiClient.apiJson).mockResolvedValue({ success: true, message: 'Added.' });

    const ok = await saveModel();
    expect(ok).toBe(true);
    expect(apiClient.apiJson).toHaveBeenCalledWith(
      '/admin/api/models',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('saveModel_patchesExistingModel', async () => {
    openNewModelDrawer();
    const store = useRoutingModelsStore();
    store.setDraft({
      ...store.draft(),
      id: 'renamed',
      url: 'http://host.docker.internal:8080',
      _existing: true,
      _originalId: 'old-id',
    });
    vi.mocked(apiClient.apiJson).mockResolvedValue({ success: true });

    await saveModel();
    expect(apiClient.apiJson).toHaveBeenCalledWith(
      '/admin/api/models/old-id',
      expect.objectContaining({ method: 'PATCH' }),
    );
  });

  it('saveModel_setsFieldErrorOnValidationFailure', async () => {
    openNewModelDrawer();
    const store = useRoutingModelsStore();
    store.setDraft({ ...store.draft(), id: '', url: '' });

    const ok = await saveModel();
    expect(ok).toBe(false);
    expect(store.fieldError()).toContain('required');
    expect(
      vi.mocked(apiClient.apiJson).mock.calls.some(
        ([path, opts]) => String(path).includes('/admin/api/models') && (opts as { method?: string })?.method,
      ),
    ).toBe(false);
  });

  it('testModel_recordsResult', async () => {
    vi.mocked(apiClient.apiJson).mockResolvedValue({ ok: true, modelId: 'm1' });
    await testModel('m1');
    const dialog = useRoutingModelsStore().testDialog();
    expect(dialog?.result?.ok).toBe(true);
    expect(pushToast).toHaveBeenCalledWith('Model test succeeded.');
  });

  it('removeModel_returnsFalseOnFailure', async () => {
    vi.mocked(apiClient.apiJson).mockResolvedValue({ success: false, message: 'blocked' });
    const ok = await removeModel('m1');
    expect(ok).toBe(false);
  });
});
