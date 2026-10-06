import { describe, expect, it, vi } from 'vitest';
import { createResource, RESOURCE_FRESH_MS } from './resources';

describe('resources', () => {
  it('load_marksReadyWhenFresh', async () => {
    let now = 1000;
    const resource = createResource({
      freshMs: RESOURCE_FRESH_MS.summary,
      now: () => now,
      fetch: async () => ({ total: 1 }),
    });

    await resource.load();
    expect(resource.snapshot().phase).toBe('ready');

    now += RESOURCE_FRESH_MS.summary - 1;
    resource.tick(now);
    expect(resource.snapshot().phase).toBe('ready');
  });

  it('tick_marksStaleAfterFreshMs', async () => {
    let now = 0;
    const resource = createResource({
      freshMs: 2000,
      now: () => now,
      fetch: async () => 'ok',
    });

    await resource.load();
    now = 2500;
    expect(resource.tick(now)).toBe('stale');
  });

  it('discardIfStale_dropsLateResponses', async () => {
    let resolveFirst!: (value: string) => void;
    let resolveSecond!: (value: string) => void;
    let call = 0;
    const resource = createResource({
      freshMs: 1000,
      fetch: () => {
        call++;
        if (call === 1) {
          return new Promise<string>((resolve) => {
            resolveFirst = resolve;
          });
        }
        return new Promise<string>((resolve) => {
          resolveSecond = resolve;
        });
      },
    });

    const first = resource.load();
    const second = resource.load({ force: true });
    resolveFirst('late-first');
    await first;
    expect(resource.snapshot().data).toBeNull();

    resolveSecond('current');
    await second;
    expect(resource.snapshot().data).toBe('current');
  });

  it('dedupesConcurrentLoads', async () => {
    let resolve!: (value: number) => void;
    const fetch = vi.fn(
      () =>
        new Promise<number>((r) => {
          resolve = r;
        }),
    );
    const resource = createResource({ freshMs: 1000, fetch });
    const a = resource.load();
    const b = resource.load();
    expect(a).toBe(b);
    resolve(42);
    await a;
    expect(fetch).toHaveBeenCalledOnce();
  });
});
