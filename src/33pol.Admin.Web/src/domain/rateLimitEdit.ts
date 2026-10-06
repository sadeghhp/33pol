import { formatNum } from './format';
import {
  buildRateLimitsPayload,
  rateLimitRuleIdentity,
  type RateLimitConfig,
  type RateLimitRule,
  type RateLimitTier,
  type RateLimitsPayload,
  type RateLimitWindow,
} from '../stores/rateLimits';

export interface RateLimitScopeInfo {
  id: string;
  name: string;
  short: string;
  singleton?: boolean;
  rateOnly?: boolean;
}

export const RATE_LIMIT_SCOPE_INFO: Record<string, RateLimitScopeInfo> = {
  model: { id: 'model', name: 'Everyone on one model', short: 'Model' },
  tenant: { id: 'tenant', name: 'A tenant, all models', short: 'Tenant' },
  api_key: { id: 'api_key', name: 'An API key, all models', short: 'API key' },
  global: { id: 'global', name: 'Whole gateway', short: 'Gateway', singleton: true },
  tenant_model: { id: 'tenant_model', name: 'A tenant on one model', short: 'Tenant & model' },
  api_key_model: { id: 'api_key_model', name: 'An API key on one model', short: 'Key & model' },
  anonymous: { id: 'anonymous', name: 'Anonymous callers', short: 'Anonymous', singleton: true, rateOnly: true },
  auth_failure: { id: 'auth_failure', name: 'Failed sign-ins', short: 'Failed sign-ins', singleton: true, rateOnly: true },
};

export function scopeInfo(scope: string): RateLimitScopeInfo {
  const id = String(scope || '').toLowerCase();
  return RATE_LIMIT_SCOPE_INFO[id] ?? { id, name: id, short: id };
}

export const RATE_LIMIT_LIMITS = {
  maxRpm: 1_000_000,
  maxBurst: 1_000_000,
  maxStreams: 10_000,
  maxPlanSlugLength: 64,
  maxTargetLength: 256,
} as const;

const PLAN_SLUG_RE = /^[A-Za-z][A-Za-z0-9_-]*$/;

export function tierText(t: RateLimitTier | undefined, suspended = false): string {
  if (!t) return '—';
  if (suspended) return 'paused';
  const streams = Number(t.maxConcurrentStreams) > 0 ? formatNum(t.maxConcurrentStreams) : '∞';
  return `${formatNum(t.rpm)} rpm · burst ${formatNum(t.burst)} · streams ${streams}`;
}

export function tierChangeText(b: RateLimitTier, a: RateLimitTier): string {
  const streams = (v: number) => (v > 0 ? formatNum(v) : '∞');
  const parts: string[] = [];
  if ((b.rpm || 0) !== (a.rpm || 0)) parts.push(`${formatNum(b.rpm)} → ${formatNum(a.rpm)} rpm`);
  if ((b.burst || 0) !== (a.burst || 0)) parts.push(`burst ${formatNum(b.burst)} → ${formatNum(a.burst)}`);
  if ((b.maxConcurrentStreams || 0) !== (a.maxConcurrentStreams || 0)) {
    parts.push(`streams ${streams(b.maxConcurrentStreams)} → ${streams(a.maxConcurrentStreams)}`);
  }
  return parts.join(' · ');
}

export function validatePlanSlug(
  slug: string,
  plans: Record<string, RateLimitTier>,
  originalSlug = '',
): string {
  const key = String(slug || '').trim();
  if (!PLAN_SLUG_RE.test(key)) {
    return 'Plan slug: letters, digits, hyphen or underscore, starting with a letter.';
  }
  if (key.length > RATE_LIMIT_LIMITS.maxPlanSlugLength) {
    return `A plan slug can be at most ${RATE_LIMIT_LIMITS.maxPlanSlugLength} characters.`;
  }
  const clash = Object.keys(plans).find(
    (k) => k.toLowerCase() === key.toLowerCase() && k !== originalSlug,
  );
  if (clash) return `A plan called “${clash}” already exists.`;
  return '';
}

export function validateTierFields(
  tier: { rpm: number | null; burst: number | null; maxConcurrentStreams: number | null },
  opts?: { floorRpm?: boolean; scope?: string },
): string {
  const floorRpm = opts?.floorRpm === true;
  const scope = opts?.scope ?? '';
  const blank = (['rpm', 'burst', 'maxConcurrentStreams'] as const).find((f) => tier[f] === null);
  if (blank) {
    const label = blank === 'rpm' ? 'RPM' : blank === 'burst' ? 'Burst' : 'Streams';
    return `${label} is empty. Enter a number — 0 if you mean zero; an empty field is not read as one.`;
  }
  const rpm = tier.rpm as number;
  const burst = tier.burst as number;
  const streams = tier.maxConcurrentStreams as number;
  const info = scopeInfo(scope);
  if (!floorRpm && rpm === 0 && streams === 0) {
    return 'A rule must limit something: set rpm or streams above zero.';
  }
  const minRpm = floorRpm ? 1 : 0;
  const max = RATE_LIMIT_LIMITS;
  if (!Number.isInteger(rpm) || rpm < minRpm || rpm > max.maxRpm) {
    return `RPM must be a whole number between ${minRpm} and ${formatNum(max.maxRpm)}.`;
  }
  if (!Number.isInteger(burst) || burst < 0 || burst > max.maxBurst) {
    return `Burst must be a whole number between 0 and ${formatNum(max.maxBurst)}.`;
  }
  if (!Number.isInteger(streams) || streams < 0 || streams > max.maxStreams) {
    return `Streams must be a whole number between 0 and ${formatNum(max.maxStreams)}.`;
  }
  if (floorRpm && rpm < 1) {
    return 'A plan tier needs at least 1 rpm.';
  }
  if (!floorRpm && rpm === 0 && burst !== 0) {
    return scope === 'tenant'
      ? 'A tenant rule with rpm 0 keeps the plan rate; set burst to 0 as well.'
      : 'With rpm 0 this rule does not limit the rate, so a burst has no rate to refill it; set burst to 0 as well.';
  }
  if (info.rateOnly) {
    if (rpm === 0) return `${info.name} limits the request rate only; set rpm above zero.`;
    if (streams !== 0) return `${info.name} limits the request rate only; streams has no effect there and must be 0.`;
  }
  return '';
}

export function windowPayload(w: RateLimitWindow): RateLimitWindow {
  const isOnce = w.kind === 'once';
  return {
    name: String(w.name ?? '').trim(),
    kind: w.kind,
    rpm: w.suspend ? 0 : Number(w.rpm) || 0,
    burst: w.suspend ? 0 : Number(w.burst) || 0,
    maxConcurrentStreams: w.suspend ? 0 : Number(w.maxConcurrentStreams) || 0,
    suspend: !!w.suspend,
    priority: w.priority == null ? null : Number(w.priority),
    from: isOnce ? w.from : null,
    until: isOnce ? w.until : null,
    days: isOnce ? [] : [...(w.days || [])],
    start: isOnce ? null : w.start,
    end: isOnce ? null : w.end,
    timeZone: isOnce ? null : w.timeZone,
    validFrom: w.validFrom,
    validUntil: w.validUntil,
  };
}

export function blankWindow(fromRule?: Pick<RateLimitRule, 'rpm' | 'burst' | 'maxConcurrentStreams'>): RateLimitWindow {
  return {
    name: '',
    kind: 'weekly',
    rpm: fromRule?.rpm ?? 60,
    burst: fromRule?.burst ?? 0,
    maxConcurrentStreams: fromRule?.maxConcurrentStreams ?? 0,
    suspend: false,
    priority: null,
    from: null,
    until: null,
    days: ['mon', 'tue', 'wed', 'thu', 'fri'],
    start: '19:00',
    end: '07:00',
    timeZone: browserTimeZone(),
    validFrom: null,
    validUntil: null,
  };
}

export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

export interface RateLimitDiffItem {
  id: string;
  kind: string;
  subject: string;
  change: string;
  text: string;
  destructive: boolean;
  kindCls: string;
}

const DIFF_KIND_CLS: Record<string, string> = {
  new: 'tag accent',
  deleted: 'tag level-error',
  removed: 'tag level-error',
  'enforcement off': 'tag level-error',
  'switched off': 'tag warn',
};

function ruleSubject(scope: string, target: string): string {
  const info = scopeInfo(scope);
  if (info.singleton) return info.name;
  const display = target === '*' ? '' : target;
  return `${info.short} “${display}”`.trim();
}

export function diffRateLimits(before: RateLimitsPayload | null, after: RateLimitsPayload | null): RateLimitDiffItem[] {
  if (!before || !after) return [];
  const items: RateLimitDiffItem[] = [];
  const windowsText = (n: number) => `${n} window${n === 1 ? '' : 's'}`;
  const push = (
    id: string,
    kind: string,
    subject: string,
    change: string,
    text: string,
    destructive = false,
  ) => {
    items.push({
      id,
      kind,
      subject,
      change,
      text,
      destructive,
      kindCls: DIFF_KIND_CLS[kind] ?? 'tag',
    });
  };

  if (before.enabled !== after.enabled) {
    push(
      'enabled',
      after.enabled ? 'enforcement on' : 'enforcement off',
      'Whole gateway',
      after.enabled ? 'rate limits are enforced again' : 'every rule and window stops applying',
      after.enabled ? 'enforcement on' : 'enforcement off',
      !after.enabled,
    );
  }
  if (before.adaptiveEnabled !== after.adaptiveEnabled) {
    push(
      'adaptive',
      'changed',
      'Adaptive load shedding',
      after.adaptiveEnabled ? 'off → on' : 'on → off',
      after.adaptiveEnabled ? 'adaptive on' : 'adaptive off',
    );
  }
  if (JSON.stringify(before.default) !== JSON.stringify(after.default)) {
    push('default', 'changed', 'Default tier', tierChangeText(before.default, after.default), 'default tier');
  }

  const slugs = new Set([...Object.keys(before.plans), ...Object.keys(after.plans)]);
  for (const slug of slugs) {
    const b = before.plans[slug];
    const a = after.plans[slug];
    if (JSON.stringify(b) === JSON.stringify(a)) continue;
    if (!b) push('plan:' + slug, 'new', `Plan ${slug}`, tierText(a), `new plan ${slug}`);
    else if (!a) {
      push(
        'plan:' + slug,
        'removed',
        `Plan ${slug}`,
        `was ${tierText(b)} · its tenants fall back to the default tier`,
        `removed plan ${slug}`,
        true,
      );
    } else push('plan:' + slug, 'changed', `Plan ${slug}`, tierChangeText(b, a), `plan ${slug}`);
  }

  const byId = (list: RateLimitRule[]) =>
    new Map(list.map((r) => [rateLimitRuleIdentity(r.scope, r.target), r]));
  const bRules = byId(before.rules);
  const aRules = byId(after.rules);
  for (const id of new Set([...bRules.keys(), ...aRules.keys()])) {
    const was = bRules.get(id);
    const now = aRules.get(id);
    if (was && now && JSON.stringify(was) === JSON.stringify(now)) continue;
    const r = now || was!;
    const info = scopeInfo(r.scope);
    const label = `${info.short} ${r.target === '*' ? '' : r.target}`.trim();
    const subject = ruleSubject(r.scope, r.target);
    if (!was) {
      const n = (now!.schedule || []).length;
      push(
        'rule:' + id,
        'new',
        subject,
        tierText(now!) + (n ? ` · ${windowsText(n)}` : '') + (now!.enabled ? '' : ' · switched off'),
        `new rule ${label}`,
      );
    } else if (!now) {
      const n = (was.schedule || []).length;
      push(
        'rule:' + id,
        'deleted',
        subject,
        `was ${tierText(was)}${n ? ` · ${windowsText(n)} go with it` : ''}`,
        `deleted rule ${label}`,
        true,
      );
    } else {
      const parts: string[] = [];
      const tier = tierChangeText(was, now);
      if (tier) parts.push(tier);
      const wb = (was.schedule || []).length;
      const wa = (now.schedule || []).length;
      if (JSON.stringify(was.schedule || []) !== JSON.stringify(now.schedule || [])) {
        parts.push(wb === wa ? `${windowsText(wa)} edited` : `${wb} → ${windowsText(wa)}`);
      }
      const toggled = was.enabled !== now.enabled;
      const kind = toggled ? (now.enabled ? 'switched on' : 'switched off') : 'changed';
      if (toggled && !now.enabled) parts.push('tier and windows kept');
      push(
        'rule:' + id,
        kind,
        subject,
        parts.join(' · '),
        (toggled ? (now.enabled ? 'switched on rule ' : 'switched off rule ') : '') + label,
      );
    }
  }

  return items
    .map((item, i) => ({ item, i }))
    .sort((x, y) => Number(y.item.destructive) - Number(x.item.destructive) || x.i - y.i)
    .map(({ item }) => item);
}

export interface RateLimitDirtyView {
  show: boolean;
  count: number;
  destructive: number;
  countText: string;
  detail: string;
  items: RateLimitDiffItem[];
  saveLabel: string;
}

type DirtyConfigSource = Pick<RateLimitConfig, 'enabled' | 'adaptiveEnabled' | 'default' | 'plans' | 'rules'>;

export function computeDirtyView(
  saved: DirtyConfigSource | null,
  draft: DirtyConfigSource | null,
  dirty: boolean,
): RateLimitDirtyView {
  const before = saved ? buildRateLimitsPayload(saved) : null;
  const after = draft ? buildRateLimitsPayload(draft) : null;
  if (!before || !after) {
    return { show: false, count: 0, destructive: 0, countText: '', detail: '', items: [], saveLabel: 'Save' };
  }
  const mine = diffRateLimits(before, after);
  const count = mine.length;
  const destructive = mine.filter((i) => i.destructive).length;
  const deletions = mine.filter((i) => i.kind === 'deleted' || i.kind === 'removed').length;
  const stopping = mine.some((i) => i.kind === 'enforcement off');
  const warn = [
    deletions ? `${deletions} deletion${deletions === 1 ? '' : 's'}` : '',
    stopping ? 'stops enforcing' : '',
  ]
    .filter(Boolean)
    .join(', ');
  return {
    show: mine.length > 0 || dirty,
    count,
    destructive,
    countText: count > 0 ? `${count} unsaved change${count === 1 ? '' : 's'}` : 'Unsaved changes',
    detail: mine.slice(0, 4).map((i) => i.text).join(' · ') + (count > 4 ? ' · …' : ''),
    items: mine,
    saveLabel:
      count > 0
        ? `Save ${count} change${count === 1 ? '' : 's'}${warn ? ` (${warn})` : ''}`
        : 'Save',
  };
}

export interface ScheduleTierDto {
  rpm?: number;
  Rpm?: number;
  burst?: number;
  Burst?: number;
  maxConcurrentStreams?: number;
  MaxConcurrentStreams?: number;
  suspended?: boolean;
  Suspended?: boolean;
}

export interface ScheduleRuleStatusDto {
  scope?: string;
  Scope?: string;
  target?: string;
  Target?: string;
  effective?: ScheduleTierDto;
  Effective?: ScheduleTierDto;
  activeWindow?: string | null;
  ActiveWindow?: string | null;
  activeUntil?: string | null;
  ActiveUntil?: string | null;
}

export function scheduleTierFromRaw(t: ScheduleTierDto | undefined): { rpm: number; burst: number; maxConcurrentStreams: number; suspended: boolean } {
  const raw = t ?? {};
  return {
    rpm: Number(raw.rpm ?? raw.Rpm ?? 0),
    burst: Number(raw.burst ?? raw.Burst ?? 0),
    maxConcurrentStreams: Number(raw.maxConcurrentStreams ?? raw.MaxConcurrentStreams ?? 0),
    suspended: !!(raw.suspended ?? raw.Suspended),
  };
}

export function formatEnforcingNow(status: ScheduleRuleStatusDto | null | undefined, masterEnabled: boolean): { text: string; tags: string[] } {
  if (!masterEnabled) return { text: 'not enforced', tags: ['off'] };
  if (!status) return { text: '—', tags: [] };
  const eff = scheduleTierFromRaw(status.effective ?? status.Effective);
  if (eff.suspended) return { text: 'paused', tags: ['window'] };
  const parts = [`${formatNum(eff.rpm)} rpm`, `burst ${formatNum(eff.burst)}`];
  if (eff.maxConcurrentStreams > 0) parts.push(`streams ${formatNum(eff.maxConcurrentStreams)}`);
  const tags: string[] = [];
  const active = status.activeWindow ?? status.ActiveWindow;
  if (active) tags.push(String(active));
  return { text: parts.join(' · '), tags };
}

export function usageTake(ruleCount: number): number {
  return Math.max(200, Math.min(1000, ruleCount));
}

export function seriesBucketMinutes(windowMinutes: number): number {
  if (windowMinutes <= 60) return 1;
  if (windowMinutes <= 120) return 2;
  return 3;
}

function sparkXY(values: number[], max: number): [number, number][] {
  const n = values.length;
  const cap = Math.max(max, 1e-9);
  return values.map((val, i) => [(i / (n - 1)) * 100, 94 - Math.min(88, (val / cap) * 88)]);
}

export function sparkLine(values: number[], max: number): string {
  if (values.length < 2) return '';
  return sparkXY(values, max).map(([x, y]) => `${x.toFixed(2)},${y.toFixed(2)}`).join(' ');
}

export function sparkFill(values: number[], max: number): string {
  if (values.length < 2) return '';
  return `M0,100${sparkXY(values, max).map(([x, y]) => ` L${x.toFixed(2)},${y.toFixed(2)}`).join('')} L100,100 Z`;
}
