import { describe, expect, it } from 'vitest';
import {
  computeDirtyView,
  diffRateLimits,
  formatEnforcingNow,
  sparkLine,
  validatePlanSlug,
  validateTierFields,
  windowPayload,
} from './rateLimitEdit';
import { buildRateLimitsPayload, type RateLimitConfig } from '../stores/rateLimits';

const baseConfig = (): RateLimitConfig => ({
  version: 1,
  enabled: true,
  adaptiveEnabled: false,
  default: { rpm: 60, burst: 10, maxConcurrentStreams: 0 },
  plans: { standard: { rpm: 1200, burst: 120, maxConcurrentStreams: 0 } },
  rules: [
    {
      scope: 'model',
      target: 'gpt-4',
      rpm: 600,
      burst: 60,
      maxConcurrentStreams: 0,
      enabled: true,
      schedule: [],
    },
  ],
});

describe('validatePlanSlug', () => {
  it('rejectsInvalidSlug', () => {
    expect(validatePlanSlug('9bad', {})).toMatch(/starting with a letter/);
  });

  it('rejectsDuplicate', () => {
    expect(validatePlanSlug('Pro', { pro: { rpm: 1, burst: 0, maxConcurrentStreams: 0 } })).toMatch(/already exists/);
  });
});

describe('validateTierFields', () => {
  it('requiresRpmForPlans', () => {
    expect(validateTierFields({ rpm: 0, burst: 0, maxConcurrentStreams: 0 }, { floorRpm: true })).toMatch(
      /RPM must be a whole number between 1/,
    );
  });

  it('acceptsValidRuleTier', () => {
    expect(validateTierFields({ rpm: 60, burst: 0, maxConcurrentStreams: 0 }, { scope: 'model' })).toBe('');
  });
});

describe('windowPayload', () => {
  it('clearsWeeklyFieldsForOnce', () => {
    const payload = windowPayload({
      name: 'night',
      kind: 'once',
      rpm: 30,
      burst: 0,
      maxConcurrentStreams: 0,
      suspend: false,
      priority: null,
      from: '2026-01-01T00:00:00.000Z',
      until: null,
      days: ['mon'],
      start: '19:00',
      end: '07:00',
      timeZone: 'UTC',
      validFrom: null,
      validUntil: null,
    });
    expect(payload.days).toEqual([]);
    expect(payload.from).toBe('2026-01-01T00:00:00.000Z');
  });
});

describe('diffRateLimits', () => {
  it('flagsEnforcementOffAsDestructive', () => {
    const saved = buildRateLimitsPayload(baseConfig());
    const draft = { ...saved, enabled: false };
    const items = diffRateLimits(saved, draft);
    expect(items.some((i) => i.id === 'enabled' && i.destructive)).toBe(true);
  });

  it('detectsPlanRemoval', () => {
    const saved = buildRateLimitsPayload(baseConfig());
    const draft = buildRateLimitsPayload({ ...baseConfig(), plans: {} });
    const items = diffRateLimits(saved, draft);
    expect(items.some((i) => i.kind === 'removed')).toBe(true);
  });
});

describe('computeDirtyView', () => {
  it('buildsSaveLabelWithDestructiveHint', () => {
    const saved = baseConfig();
    const draft = { ...baseConfig(), enabled: false };
    const view = computeDirtyView(saved, draft, true);
    expect(view.destructive).toBeGreaterThan(0);
    expect(view.saveLabel).toContain('stops enforcing');
  });
});

describe('formatEnforcingNow', () => {
  it('showsNotEnforcedWhenMasterSwitchOff', () => {
    expect(formatEnforcingNow({ effective: { rpm: 60, burst: 0, maxConcurrentStreams: 0 } }, false).text).toBe(
      'not enforced',
    );
  });
});

describe('sparkLine', () => {
  it('returnsEmptyForSinglePoint', () => {
    expect(sparkLine([5], 5)).toBe('');
  });

  it('returnsPointsForTwoValues', () => {
    expect(sparkLine([0, 10], 10)).toContain('0.00');
  });
});
