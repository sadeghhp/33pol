import { describe, expect, it } from 'vitest';
import {
  buildRateLimitsPayload,
  buildWindowPreviewBody,
  canonicalRateLimits,
  cloneRateLimitsConfig,
  findDraftRule,
  ifMatchHeaders,
  isRateLimitsDirty,
  normalizeRateLimitsPayload,
  normalizeUsageReport,
  parseEtagVersion,
  rateLimitRuleIdentity,
  resolveMatchVersion,
  ruleFormSnapshot,
  undoRateLimitChange,
  type RateLimitConfig,
} from './rateLimits';
import { usageTake } from '../domain/rateLimitEdit';

const baseConfig = (): RateLimitConfig => ({
  version: 7,
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

describe('rateLimitRuleIdentity', () => {
  it('lowercasesScopeAndTarget', () => {
    expect(rateLimitRuleIdentity('Model', 'GPT-4')).toBe('model:gpt-4');
  });
});

describe('parseEtagVersion', () => {
  it('parsesWeakEtag', () => {
    expect(parseEtagVersion('W/"42"')).toBe(42);
  });

  it('returnsNullForMissing', () => {
    expect(parseEtagVersion(null)).toBeNull();
  });
});

describe('resolveMatchVersion', () => {
  it('prefersEtagOverBodyVersion', () => {
    expect(resolveMatchVersion('W/"9"', 7)).toBe(9);
  });

  it('fallsBackToBodyVersion', () => {
    expect(resolveMatchVersion(null, 7)).toBe(7);
  });
});

describe('ifMatchHeaders', () => {
  it('buildsWeakEtagHeader', () => {
    expect(ifMatchHeaders(12)).toEqual({ 'If-Match': 'W/"12"' });
  });

  it('omitsHeaderWhenVersionMissing', () => {
    expect(ifMatchHeaders(null)).toEqual({});
  });
});

describe('normalizeRateLimitsPayload', () => {
  it('acceptsPascalCaseFields', () => {
    const normalized = normalizeRateLimitsPayload({
      Version: 3,
      Enabled: false,
      AdaptiveEnabled: true,
      Default: { Rpm: 30, Burst: 5, MaxConcurrentStreams: 2 },
      Plans: { pro: { Rpm: 900, Burst: 90, MaxConcurrentStreams: 0 } },
      Rules: [
        {
          Scope: 'tenant',
          Target: 'acme',
          Rpm: 100,
          Burst: 10,
          MaxConcurrentStreams: 0,
          Enabled: false,
          Schedule: [],
        },
      ],
      Writable: false,
      ReadOnlyReason: 'store_unavailable',
    } as unknown as import('./rateLimits').AdminRateLimitsDto);
    expect(normalized?.version).toBe(3);
    expect(normalized?.enabled).toBe(false);
    expect(normalized?.adaptiveEnabled).toBe(true);
    expect(normalized?.default.rpm).toBe(30);
    expect(normalized?.plans.pro.rpm).toBe(900);
    expect(normalized?.rules[0].scope).toBe('tenant');
    expect(normalized?.rules[0].enabled).toBe(false);
    expect(normalized?.writable).toBe(false);
    expect(normalized?.readOnlyReason).toBe('store_unavailable');
  });
});

describe('buildRateLimitsPayload', () => {
  it('stripsVersionAndOmitsEmptyTargets', () => {
    const cfg = baseConfig();
    cfg.rules.push({
      scope: 'model',
      target: '  ',
      rpm: 1,
      burst: 0,
      maxConcurrentStreams: 0,
      enabled: true,
      schedule: [],
    });
    const payload = buildRateLimitsPayload(cfg);
    expect(payload).not.toHaveProperty('version');
    expect(payload.rules).toHaveLength(1);
    expect(payload.rules[0].target).toBe('gpt-4');
    expect(payload.enabled).toBe(true);
    expect(payload.adaptiveEnabled).toBe(false);
    expect(payload.default).toEqual({ rpm: 60, burst: 10, maxConcurrentStreams: 0 });
    expect(payload.plans.standard).toEqual({ rpm: 1200, burst: 120, maxConcurrentStreams: 0 });
  });

  it('preservesExplicitFalseEnabled', () => {
    const cfg = baseConfig();
    cfg.enabled = false;
    cfg.rules[0].enabled = false;
    const payload = buildRateLimitsPayload(cfg);
    expect(payload.enabled).toBe(false);
    expect(payload.rules[0].enabled).toBe(false);
  });
});

describe('isRateLimitsDirty', () => {
  it('isFalseForMatchingDraft', () => {
    const saved = baseConfig();
    const draft = cloneRateLimitsConfig(saved);
    expect(isRateLimitsDirty(saved, draft)).toBe(false);
  });

  it('isTrueWhenDraftDiffers', () => {
    const saved = baseConfig();
    const draft = cloneRateLimitsConfig(saved);
    draft.default.rpm = 61;
    expect(isRateLimitsDirty(saved, draft)).toBe(true);
  });

  it('ignoresRuleOrder', () => {
    const saved = baseConfig();
    saved.rules.push({
      scope: 'tenant',
      target: 'acme',
      rpm: 100,
      burst: 0,
      maxConcurrentStreams: 0,
      enabled: true,
      schedule: [],
    });
    const draft = cloneRateLimitsConfig(saved);
    draft.rules.reverse();
    expect(isRateLimitsDirty(saved, draft)).toBe(false);
  });
});

describe('findDraftRule', () => {
  it('findsByIdentity', () => {
    const cfg = baseConfig();
    const hit = findDraftRule(cfg.rules, 'model:gpt-4');
    expect(hit?.target).toBe('gpt-4');
  });
});

describe('ruleFormSnapshot', () => {
  it('changesWhenScheduleDiffers', () => {
    const rule = baseConfig().rules[0];
    const a = ruleFormSnapshot(rule);
    const b = ruleFormSnapshot({ ...rule, schedule: [{ ...blankScheduleWindow() }] });
    expect(a).not.toBe(b);
  });
});

function blankScheduleWindow() {
  return {
    name: 'w',
    kind: 'weekly',
    rpm: 1,
    burst: 0,
    maxConcurrentStreams: 0,
    suspend: false,
    priority: null,
    from: null,
    until: null,
    days: ['mon'],
    start: '09:00',
    end: '17:00',
    timeZone: 'UTC',
    validFrom: null,
    validUntil: null,
  };
}

describe('queueAddRuleIntent', () => {
  it('storesScopeAndTargetForRateLimitsPage', async () => {
    const { queueAddRuleIntent, useAddRuleIntent } = await import('./rateLimits');
    queueAddRuleIntent('api_key', 'key-abc');
    expect(useAddRuleIntent().addRuleIntent()).toEqual({ scope: 'api_key', target: 'key-abc' });
  });
});

describe('undoRateLimitChange', () => {
  it('restoresRemovedPlan', () => {
    const saved = baseConfig();
    const draft = cloneRateLimitsConfig(saved);
    delete draft.plans.standard;
    const next = undoRateLimitChange(draft, saved, 'plan:standard');
    expect(next.plans.standard.rpm).toBe(1200);
  });
});

describe('buildWindowPreviewBody', () => {
  it('includesCandidateWindow', () => {
    const rule = baseConfig().rules[0];
    const candidate = blankScheduleWindow();
    const body = buildWindowPreviewBody(rule, [], candidate, -1);
    expect(body.candidate).toBe('w');
    expect(body.windows).toHaveLength(1);
  });
});

describe('normalizeUsageReport', () => {
  it('acceptsPascalCaseFields', () => {
    const report = normalizeUsageReport({
      WindowMinutes: 60,
      Totals: { Requests: 10, Admitted: 8, Rejected: 2, RejectionRate: 0.2 },
      Limits: [{ LimitId: 'model:gpt-4', Scope: 'model', Target: 'gpt-4', Charged: 5, RefusedByRate: 1 }],
    });
    expect(report?.windowMinutes).toBe(60);
    expect(report?.totals.requests).toBe(10);
    expect(report?.limits[0].limitId).toBe('model:gpt-4');
  });
});

describe('usageTake', () => {
  it('scalesWithRuleCount', () => {
    expect(usageTake(5)).toBe(200);
    expect(usageTake(500)).toBe(500);
    expect(usageTake(2000)).toBe(1000);
  });
});

describe('canonicalRateLimits', () => {
  it('sortsPlansAndRulesForStableComparison', () => {
    const a = baseConfig();
    a.plans = { z: { rpm: 1, burst: 0, maxConcurrentStreams: 0 }, a: { rpm: 2, burst: 0, maxConcurrentStreams: 0 } };
    a.rules = [
      {
        scope: 'tenant',
        target: 'b',
        rpm: 1,
        burst: 0,
        maxConcurrentStreams: 0,
        enabled: true,
        schedule: [],
      },
      {
        scope: 'model',
        target: 'a',
        rpm: 2,
        burst: 0,
        maxConcurrentStreams: 0,
        enabled: true,
        schedule: [],
      },
    ];
    const b = cloneRateLimitsConfig(a);
    b.plans = { a: { rpm: 2, burst: 0, maxConcurrentStreams: 0 }, z: { rpm: 1, burst: 0, maxConcurrentStreams: 0 } };
    b.rules.reverse();
    expect(canonicalRateLimits(a)).toBe(canonicalRateLimits(b));
  });
});
