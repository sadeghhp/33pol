import { describe, expect, it, vi } from 'vitest';
import { migrateLegacyHash } from './hashRedirect';

describe('migrateLegacyHash', () => {
  it('redirectsLegacyDashboardHash', () => {
    const replace = vi.fn();
    Object.defineProperty(window, 'location', {
      value: { hash: '#dashboard?window=5m&wall=1', replace },
      configurable: true,
    });
    migrateLegacyHash();
    expect(replace).toHaveBeenCalledWith('#/dashboard?window=5m&wall=1');
  });

  it('skipsModernHash', () => {
    const replace = vi.fn();
    Object.defineProperty(window, 'location', {
      value: { hash: '#/settings?sub=cors', replace },
      configurable: true,
    });
    migrateLegacyHash();
    expect(replace).not.toHaveBeenCalled();
  });
});
