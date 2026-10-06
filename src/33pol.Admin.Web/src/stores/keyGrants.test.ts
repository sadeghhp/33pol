import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./auth', () => ({
  apiClient: { apiJson: vi.fn() },
  handleApiError: vi.fn(),
  pushToast: vi.fn(),
}));

import { apiClient } from './auth';
import { closeDrawer, openKeyAccessDrawer, saveGrants, toggleModel, useKeyGrantsStore } from './keyGrants';

describe('keyGrants store', () => {
  beforeEach(() => {
    vi.mocked(apiClient.apiJson).mockReset();
    closeDrawer();
  });

  it('openKeyAccessDrawer_loadsRegistryAndGrants', async () => {
    vi.mocked(apiClient.apiJson)
      .mockResolvedValueOnce([{ id: 'gpt-4' }])
      .mockResolvedValueOnce({ modelIds: ['gpt-4'] });

    await openKeyAccessDrawer({ id: 'key-1', label: 'Bot', keyPrefix: 'sk-x' });

    const store = useKeyGrantsStore();
    expect(store.keyId()).toBe('key-1');
    expect(store.selected()).toEqual(['gpt-4']);
    expect(store.registryModels().map((m) => m.id)).toContain('gpt-4');
  });

  it('saveGrants_putsModelIds', async () => {
    vi.mocked(apiClient.apiJson)
      .mockResolvedValueOnce([{ id: 'm1' }])
      .mockResolvedValueOnce({ modelIds: [] })
      .mockResolvedValueOnce({});

    await openKeyAccessDrawer({ id: 'key-2', keyPrefix: 'sk-y' });
    toggleModel('m1');
    const ok = await saveGrants();

    expect(ok).toBe(true);
    expect(apiClient.apiJson).toHaveBeenCalledWith(
      '/admin/api/keys/key-2/model-grants',
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ modelIds: ['m1'] }),
      }),
    );
  });
});
