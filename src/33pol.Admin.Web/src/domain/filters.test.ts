import { describe, expect, it, vi } from 'vitest';
import { debounceMs, filterBackends, filterKeys, filterModels } from './filters';

describe('filters', () => {
  it('filterModels_matchesIdOrAlias', () => {
    const models = [
      { id: 'gpt-4', url: 'http://a', aliases: ['smart'] },
      { id: 'llama', url: 'http://b', aliases: [] },
    ];
    const result = filterModels(models, 'smart', { key: 'id', dir: 1 });
    expect(result).toHaveLength(1);
    expect(result[0].id).toBe('gpt-4');
  });

  it('filterBackends_unhealthyFirst', () => {
    const backends = [
      { modelId: 'a', isHealthy: true },
      { modelId: 'b', isHealthy: false },
    ];
    const result = filterBackends(backends, '', { key: 'isHealthy', dir: 1 });
    expect(result[0].modelId).toBe('b');
  });

  it('filterKeys_respectsStatusAndText', () => {
    const keys = [
      { keyPrefix: 'abcd', label: 'Prod', isRevoked: false, isArchived: false },
      { keyPrefix: 'efgh', label: 'Old', isRevoked: true, isArchived: false },
    ];
    const active = filterKeys(keys, 'active', 'prod', { key: 'label', dir: 1 });
    expect(active).toHaveLength(1);
    expect(active[0].keyPrefix).toBe('abcd');
  });

  it('debounceMs_runsTrailingCall', async () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const debounced = debounceMs(fn, 400);
    debounced('a');
    debounced('b');
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(400);
    expect(fn).toHaveBeenCalledOnce();
    expect(fn).toHaveBeenCalledWith('b');
    vi.useRealTimers();
  });
});
