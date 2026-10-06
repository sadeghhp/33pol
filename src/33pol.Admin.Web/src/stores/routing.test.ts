import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./auth', () => ({
  apiClient: {
    apiJson: vi.fn(),
  },
  handleApiError: vi.fn(),
  pushToast: vi.fn(),
}));

import { apiClient } from './auth';
import { activateRoutingPage, disposeRoutingPage, useRoutingStore } from './routing';

describe('routing store', () => {
  beforeEach(() => {
    disposeRoutingPage();
    vi.mocked(apiClient.apiJson).mockReset();
  });

  it('loadModels_UpdatesFilteredRowsForSolidReactivity', async () => {
    vi.mocked(apiClient.apiJson).mockResolvedValue([
      { id: 'local-mock', state: 'serving', url: 'http://127.0.0.1/mock' },
    ]);

    activateRoutingPage();
    const store = useRoutingStore();
    await store.loadModels({ force: true });

    expect(store.filteredModels().map((row) => String(row.id ?? (row.model as { id?: string })?.id))).toContain(
      'local-mock',
    );
  });
});
