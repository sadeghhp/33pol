import { beforeEach, describe, expect, it } from 'vitest';
import { applyHashUsageParams, usagePresetRange, usageRangeError, usageSnapshot } from './usage';

describe('usage store', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('usagePresetRange_lastSevenDays', () => {
    const { from, to } = usagePresetRange(7);
    expect(from <= to).toBe(true);
    const days = (Date.parse(to) - Date.parse(from)) / 86400000 + 1;
    expect(days).toBe(7);
  });

  it('usageRangeError_rejectsInvertedRange', () => {
    expect(usageRangeError('2026-01-10', '2026-01-01')).toContain('From');
  });

  it('applyHashUsageParams_setsKeyFilter', async () => {
    const { useUsageStore } = await import('./usage');
    applyHashUsageParams({ apiKeyId: 'key-123', costCenter: 'eng' });
    const store = useUsageStore();
    expect(store.apiKeyId()).toBe('key-123');
    expect(store.costCenter()).toBe('eng');
    expect(usageSnapshot().apiKeyId).toBe('key-123');
  });
});
