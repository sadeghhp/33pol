import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { subscribeHealthPolling, useHealthSignals } from './health';

describe('health store', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => ({
        ok: url.includes('live'),
      })),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('subscribeHealthPolling_updatesLiveAndReady', async () => {
    const unsub = subscribeHealthPolling();
    await new Promise((r) => setTimeout(r, 20));
    const { healthLive, healthReady } = useHealthSignals();
    expect(healthLive()).toBe(true);
    expect(healthReady()).toBe(false);
    unsub();
  });
});
