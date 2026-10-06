import { describe, expect, it } from 'vitest';
import { parseFinOps, parsePolicy, parseRateLimitsGlance, parseTenants } from './overviewCards';

describe('overviewCards', () => {
  it('parseFinOps_extractsMtdAndTopModels', () => {
    const view = parseFinOps({
      currency: 'USD',
      todayCost: 1.5,
      monthToDateCost: 10,
      topModelsMonthToDate: [{ key: 'gpt-4', cost: 8, requests: 100 }],
      budgets: [{ name: 'R&D', ratio: 0.5, spent: 5, limit: 10 }],
      unpricedModelIds: ['m1'],
    });
    expect(view?.mtdCost).toBe(10);
    expect(view?.topModels[0].key).toBe('gpt-4');
    expect(view?.unpricedCount).toBe(1);
  });

  it('parsePolicy_countsGrantDenials', () => {
    const view = parsePolicy({ grantDenials: [{ key: 'tenant-a', count: 3 }] });
    expect(view?.grantDenials[0].count).toBe(3);
  });

  it('parseTenants_summarizesCounts', () => {
    const view = parseTenants({ tenantCount: 2, keyCount: 5, revokedKeyCount: 1, topConsumersMonthToDate: [] });
    expect(view?.summary).toContain('2 tenants');
  });

  it('parseRateLimitsGlance_hidesWhenUnavailable', () => {
    expect(parseRateLimitsGlance({ available: false })?.hidden).toBe(true);
  });
});
