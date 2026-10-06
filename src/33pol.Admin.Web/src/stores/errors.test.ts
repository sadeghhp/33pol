import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./auth', () => ({
  apiClient: {
    apiJson: vi.fn(),
    downloadBlob: vi.fn(),
  },
  handleApiError: vi.fn(),
  pushToast: vi.fn(),
}));

import { apiClient } from './auth';
import { errorsQueryString, facetOptions, loadOccurrences } from './errors';

describe('errors store', () => {
  beforeEach(() => {
    vi.mocked(apiClient.apiJson).mockReset();
  });

  it('errorsQueryString_includesLevelFilter', async () => {
    const { useErrorsStore } = await import('./errors');
    const store = useErrorsStore();
    store.setLevelFilter('error');
    expect(errorsQueryString()).toContain('level=error');
  });

  it('facetOptions_formatsCounts', () => {
    const opts = facetOptions([{ value: 'gpt-4', count: 12 }]);
    expect(opts[0].label).toContain('gpt-4');
    expect(opts[0].label).toContain('12');
  });

  it('loadOccurrences_usesFingerprintQuery', async () => {
    vi.mocked(apiClient.apiJson).mockResolvedValue({ occurrences: [{ id: '1' }] });
    await loadOccurrences('fp-abc');
    expect(apiClient.apiJson).toHaveBeenCalledWith(
      expect.stringMatching(/^\/admin\/api\/errors\?.*fingerprint=fp-abc/),
    );
  });
});
