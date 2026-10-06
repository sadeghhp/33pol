import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchRateLimitHelp, resetRateLimitHelpCache } from './rateLimitHelp';

describe('fetchRateLimitHelp', () => {
  afterEach(() => {
    resetRateLimitHelpCache();
    vi.restoreAllMocks();
  });

  it('loadsJsonWithoutEval', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({
          langs: [{ id: 'en', label: 'EN', name: 'English', dir: 'ltr' }],
          en: { ui: { guide: 'Rate limits, explained' }, sections: [] },
        }),
      }),
    );
    const help = await fetchRateLimitHelp('en');
    expect(help?.ui?.guide).toBe('Rate limits, explained');
    expect(fetch).toHaveBeenCalledWith('/admin/admin-rate-limit-help.json');
  });
});
