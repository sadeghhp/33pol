import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./auth', () => ({
  apiClient: { apiJson: vi.fn() },
  handleApiError: vi.fn(),
  pushToast: vi.fn(),
}));

import { apiClient } from './auth';
import {
  loadTenantGrants,
  saveTenantGrants,
  toggleModel,
  toggleRestricted,
  useTenantGrantsStore,
} from './tenantGrants';

describe('tenantGrants store', () => {
  beforeEach(() => {
    vi.mocked(apiClient.apiJson).mockReset();
    vi.mocked(apiClient.apiJson).mockImplementation(async (path: string) => {
      if (path.includes('/admin/api/models')) return [{ id: 'm1' }];
      if (path.includes('/admin/api/tenant/model-grants')) {
        return { modelIds: [], usesDefaultAccess: true };
      }
      return {};
    });
  });

  it('loadTenantGrants_unrestrictedWhenUsesDefaultAccess', async () => {
    await loadTenantGrants();
    expect(useTenantGrantsStore().restricted()).toBe(false);
  });

  it('loadTenantGrants_restrictedWhenExplicitModelIds', async () => {
    vi.mocked(apiClient.apiJson).mockImplementation(async (path: string) => {
      if (path.includes('/admin/api/models')) return [{ id: 'm1' }];
      return { modelIds: ['m1'], usesDefaultAccess: false };
    });

    await loadTenantGrants();
    const store = useTenantGrantsStore();
    expect(store.restricted()).toBe(true);
    expect(store.selected()).toEqual(['m1']);
  });

  it('saveTenantGrants_allowAllWhenUnrestricted', async () => {
    vi.mocked(apiClient.apiJson).mockResolvedValue({});
    toggleRestricted(false);

    await saveTenantGrants();
    expect(apiClient.apiJson).toHaveBeenCalledWith(
      '/admin/api/tenant/model-grants',
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ modelIds: [], allowAllModels: true }),
      }),
    );
  });

  it('saveTenantGrants_sendsSelectedWhenRestricted', async () => {
    vi.mocked(apiClient.apiJson).mockResolvedValue({});
    toggleRestricted(true);
    toggleModel('m2');

    await saveTenantGrants();
    expect(apiClient.apiJson).toHaveBeenCalledWith(
      '/admin/api/tenant/model-grants',
      expect.objectContaining({
        body: JSON.stringify({ modelIds: ['m2'] }),
      }),
    );
  });
});
