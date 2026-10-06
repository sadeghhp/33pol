import { createSignal } from 'solid-js';
import {
  blankWindow,
  computeDirtyView,
  diffRateLimits,
  type RateLimitDirtyView,
  type RateLimitDiffItem,
  seriesBucketMinutes,
  usageTake,
  validatePlanSlug,
  validateTierFields,
  windowPayload,
} from '../domain/rateLimitEdit';
import { apiClient, handleApiError, pushToast } from './auth';

export interface RateLimitTier {
  rpm: number;
  burst: number;
  maxConcurrentStreams: number;
}

export interface RateLimitWindow {
  name: string;
  kind: string;
  rpm: number;
  burst: number;
  maxConcurrentStreams: number;
  suspend: boolean;
  priority: number | null;
  from: string | null;
  until: string | null;
  days: string[];
  start: string | null;
  end: string | null;
  timeZone: string | null;
  validFrom: string | null;
  validUntil: string | null;
}

export interface RateLimitRule {
  scope: string;
  target: string;
  rpm: number;
  burst: number;
  maxConcurrentStreams: number;
  enabled: boolean;
  schedule: RateLimitWindow[];
}

/** Normalized configuration held in saved/draft state. */
export interface RateLimitConfig {
  version: number | null;
  enabled: boolean;
  adaptiveEnabled: boolean;
  default: RateLimitTier;
  plans: Record<string, RateLimitTier>;
  rules: RateLimitRule[];
  writable?: boolean;
  readOnlyReason?: string | null;
}

/** PUT body / comparable payload (version and writable are omitted). */
export interface RateLimitsPayload {
  enabled: boolean;
  adaptiveEnabled: boolean;
  default: RateLimitTier;
  plans: Record<string, RateLimitTier>;
  rules: Array<Omit<RateLimitRule, 'schedule'> & { schedule: RateLimitWindow[] }>;
}

export type AdminRateLimitsDto = RateLimitsPayload & {
  version?: number | null;
  Version?: number | null;
  writable?: boolean | null;
  Writable?: boolean | null;
  readOnlyReason?: string | null;
  ReadOnlyReason?: string | null;
  default?: RateLimitTier;
  Default?: RateLimitTier;
  plans?: Record<string, RateLimitTier>;
  Plans?: Record<string, RateLimitTier>;
  rules?: RateLimitRule[];
  Rules?: RateLimitRule[];
  enabled?: boolean;
  Enabled?: boolean;
  adaptiveEnabled?: boolean;
  AdaptiveEnabled?: boolean;
};

export const RATE_LIMIT_SCOPES = [
  { value: 'model', label: 'Everyone on one model' },
  { value: 'tenant', label: 'A tenant, all models' },
  { value: 'api_key', label: 'An API key, all models' },
  { value: 'global', label: 'Whole gateway' },
  { value: 'tenant_model', label: 'A tenant on one model' },
  { value: 'api_key_model', label: 'An API key on one model' },
  { value: 'anonymous', label: 'Anonymous callers' },
  { value: 'auth_failure', label: 'Failed sign-ins' },
] as const;

export function cloneRateLimitsConfig<T>(value: T): T {
  return value == null ? value : (JSON.parse(JSON.stringify(value)) as T);
}

function tierFromRaw(t: Record<string, unknown> | RateLimitTier | undefined): RateLimitTier {
  const raw = (t ?? {}) as Record<string, unknown>;
  return {
    rpm: Number(raw.rpm ?? raw.Rpm ?? 60),
    burst: Number(raw.burst ?? raw.Burst ?? 0),
    maxConcurrentStreams: Number(raw.maxConcurrentStreams ?? raw.MaxConcurrentStreams ?? 0),
  };
}

function windowFromRaw(w: Record<string, unknown>): RateLimitWindow {
  const iso = (v: unknown) => (v ? new Date(String(v)).toISOString() : null);
  const rawDays = w.days ?? w.Days;
  const days = Array.isArray(rawDays) ? rawDays.map((d: unknown) => String(d).toLowerCase()) : [];
  return {
    name: String(w.name ?? w.Name ?? ''),
    kind: String(w.kind ?? w.Kind ?? 'weekly').toLowerCase(),
    rpm: Number(w.rpm ?? w.Rpm ?? 0),
    burst: Number(w.burst ?? w.Burst ?? 0),
    maxConcurrentStreams: Number(w.maxConcurrentStreams ?? w.MaxConcurrentStreams ?? 0),
    suspend: !!(w.suspend ?? w.Suspend),
    priority: (w.priority ?? w.Priority) == null ? null : Number(w.priority ?? w.Priority),
    from: iso(w.from ?? w.From),
    until: iso(w.until ?? w.Until),
    days,
    start: (w.start ?? w.Start ?? null) as string | null,
    end: (w.end ?? w.End ?? null) as string | null,
    timeZone: (w.timeZone ?? w.TimeZone ?? null) as string | null,
    validFrom: iso(w.validFrom ?? w.ValidFrom),
    validUntil: iso(w.validUntil ?? w.ValidUntil),
  };
}

export function normalizeRateLimitsPayload(data: AdminRateLimitsDto | null | undefined): RateLimitConfig | null {
  if (!data) return null;
  const d = data.default ?? data.Default;
  const plans = data.plans ?? data.Plans ?? {};
  const rules = data.rules ?? data.Rules ?? [];
  const versionRaw = data.version ?? data.Version;
  return {
    version: versionRaw == null ? null : Number(versionRaw),
    enabled: data.enabled ?? data.Enabled ?? true,
    adaptiveEnabled: data.adaptiveEnabled ?? data.AdaptiveEnabled ?? false,
    default: tierFromRaw(d),
    plans: Object.fromEntries(
      Object.entries(plans).map(([slug, t]) => [slug, tierFromRaw(t as unknown as Record<string, unknown>)]),
    ),
    rules: (Array.isArray(rules) ? rules : []).map((r) => {
      const row = r as Record<string, unknown>;
      return {
        scope: String(row.scope ?? row.Scope ?? 'model'),
        target: String(row.target ?? row.Target ?? ''),
        ...tierFromRaw(row),
        enabled: (row.enabled ?? row.Enabled) !== false,
        schedule: (Array.isArray(row.schedule ?? row.Schedule) ? (row.schedule ?? row.Schedule) as unknown[] : []).map(
          (w: unknown) => windowFromRaw(w as Record<string, unknown>),
        ),
      };
    }),
    writable: data.writable ?? data.Writable ?? undefined,
    readOnlyReason: data.readOnlyReason ?? data.ReadOnlyReason ?? null,
  };
}

export function tierPayload(t: RateLimitTier | undefined): RateLimitTier {
  return {
    rpm: Number(t?.rpm) || 0,
    burst: Number(t?.burst) || 0,
    maxConcurrentStreams: Number(t?.maxConcurrentStreams) || 0,
  };
}

export function rulePayload(row: RateLimitRule): RateLimitRule {
  return {
    scope: String(row.scope ?? '').trim(),
    target: String(row.target ?? '').trim(),
    ...tierPayload(row),
    enabled: row.enabled !== false,
    schedule: (row.schedule || []).map((w) => ({ ...w })),
  };
}

export type RateLimitsPayloadSource = Pick<
  RateLimitConfig,
  'enabled' | 'adaptiveEnabled' | 'default' | 'plans' | 'rules'
>;

export function buildRateLimitsPayload(source: RateLimitsPayloadSource | null | undefined): RateLimitsPayload {
  const cfg = source ?? {
    enabled: true,
    adaptiveEnabled: false,
    default: { rpm: 60, burst: 0, maxConcurrentStreams: 0 },
    plans: {},
    rules: [],
  };
  const plans: Record<string, RateLimitTier> = {};
  for (const [slug, t] of Object.entries(cfg.plans || {})) {
    const key = String(slug || '').trim();
    if (!key) continue;
    plans[key] = tierPayload(t as RateLimitTier);
  }
  const rules = (cfg.rules || [])
    .filter((row) => String(row.target ?? '').trim() !== '')
    .map((row) => rulePayload(row));
  return {
    enabled: cfg.enabled !== false,
    adaptiveEnabled: cfg.adaptiveEnabled === true,
    default: tierPayload(cfg.default),
    plans,
    rules,
  };
}

export function rateLimitRuleIdentity(scope: string, target: string): string {
  return `${String(scope || '').toLowerCase()}:${String(target || '').toLowerCase()}`;
}

export function canonicalRateLimits(source: RateLimitConfig | null | undefined): string {
  if (!source) return '';
  const payload = buildRateLimitsPayload(source);
  const byKey = (a: readonly [string, unknown], b: readonly [string, unknown]) =>
    a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0;
  return JSON.stringify({
    ...payload,
    plans: Object.entries(payload.plans).sort(byKey),
    rules: payload.rules
      .map((r) => [rateLimitRuleIdentity(r.scope, r.target), r] as [string, typeof r])
      .sort(byKey),
  });
}

export function isRateLimitsDirty(saved: RateLimitConfig | null, draft: RateLimitConfig | null): boolean {
  if (!saved || !draft) return false;
  return canonicalRateLimits(draft) !== canonicalRateLimits(saved);
}

export function parseEtagVersion(etag: string | null | undefined): number | null {
  if (!etag) return null;
  const match = /W?"(\d+)"?/.exec(etag.trim());
  return match ? Number(match[1]) : null;
}

export function resolveMatchVersion(etag: string | null | undefined, bodyVersion: number | null | undefined): number | null {
  const fromEtag = parseEtagVersion(etag);
  if (fromEtag != null && Number.isFinite(fromEtag)) return fromEtag;
  if (bodyVersion == null) return null;
  const n = Number(bodyVersion);
  return Number.isFinite(n) ? n : null;
}

export function ifMatchHeaders(version: number | null | undefined): Record<string, string> {
  if (version === null || version === undefined) return {};
  return { 'If-Match': `W/"${version}"` };
}

export function readOnlyText(code: string | null | undefined): string {
  return code === 'store_unavailable'
    ? 'This gateway has no database, so rate limits are read-only here.'
    : 'This gateway cannot save rate-limit changes right now, so they are read-only here.';
}

const [saved, setSaved] = createSignal<RateLimitConfig | null>(null);
const [draft, setDraft] = createSignal<RateLimitConfig | null>(null);
const [matchVersion, setMatchVersion] = createSignal<number | null>(null);
const [loading, setLoading] = createSignal(false);
const [saving, setSaving] = createSignal(false);
const [loadError, setLoadError] = createSignal('');
const [readOnlyReason, setReadOnlyReason] = createSignal('');

let fetchSeq = 0;
let fetchInFlight: Promise<void> | null = null;
let aborted = false;

function applyLoaded(data: AdminRateLimitsDto, etag: string | null, keepDraft = false): void {
  fetchSeq += 1;
  const normalized = normalizeRateLimitsPayload(data);
  if (!normalized) {
    setSaved(null);
    setDraft(null);
    setMatchVersion(null);
    return;
  }
  setSaved(normalized);
  if (!keepDraft || !draft()) {
    setDraft(cloneRateLimitsConfig(normalized));
  }
  setMatchVersion(resolveMatchVersion(etag, normalized.version));
  setLoadError('');
  const writable = data.writable ?? data.Writable;
  if (writable === false) {
    setReadOnlyReason(readOnlyText(data.readOnlyReason ?? data.ReadOnlyReason));
  } else if (writable === true) {
    setReadOnlyReason('');
  }
}

function patchDraft(updater: (next: RateLimitConfig) => void): void {
  const current = draft();
  if (!current) return;
  const next = cloneRateLimitsConfig(current);
  updater(next);
  setDraft(next);
}

export function useRateLimitsStore() {
  return {
    saved,
    draft,
    loading,
    saving,
    loadError,
    readOnlyReason,
    dirty: () => isRateLimitsDirty(saved(), draft()),
    locked: () => !!readOnlyReason() || saving(),
    editable: () => !readOnlyReason() && !saving(),
    workInProgress: rateLimitsWorkInProgress,
    load: (opts?: { force?: boolean }) => loadRateLimits(opts),
    save: requestSaveRateLimits,
    discard: () => {
      discardRateLimitChanges();
      setReviewOpen(false);
      closeTierDrawer();
      closeRuleDrawer();
      queueScheduleRefresh();
    },
    setEnabled: (value: boolean) => patchDraft((d) => { d.enabled = value; }),
    setAdaptiveEnabled: (value: boolean) => patchDraft((d) => { d.adaptiveEnabled = value; }),
    setDefaultTier: (field: keyof RateLimitTier, value: number) =>
      patchDraft((d) => { d.default = { ...d.default, [field]: value }; }),
    setRuleEnabled: (identity: string, enabled: boolean) =>
      patchDraft((d) => {
        const rule = findDraftRule(d.rules, identity);
        if (rule) rule.enabled = enabled;
      }),
    setRuleField: (index: number, field: keyof RateLimitRule, value: string | number | boolean) =>
      patchDraft((d) => {
        if (index < 0 || index >= d.rules.length) return;
        d.rules[index] = { ...d.rules[index], [field]: value };
      }),
    deleteRule: (index: number) =>
      patchDraft((d) => { d.rules = d.rules.filter((_, i) => i !== index); }),
    addRule: (rule: Pick<RateLimitRule, 'scope' | 'target' | 'rpm' | 'burst'>) =>
      patchDraft((d) => {
        d.rules = [
          ...d.rules,
          {
            scope: rule.scope,
            target: rule.target.trim(),
            rpm: rule.rpm,
            burst: rule.burst,
            maxConcurrentStreams: 0,
            enabled: true,
            schedule: [],
          },
        ];
      }),
    removePlan: (slug: string) =>
      patchDraft((d) => {
        delete d.plans[slug];
      }),
    scheduleStatusFor: (scope: string, target: string) => {
      const report = scheduleSaved();
      return report?.rules.find((r) => r.identity === rateLimitRuleIdentity(scope, target)) ?? null;
    },
  };
}

export async function loadRateLimits(opts?: { force?: boolean }): Promise<void> {
  if (aborted) return;
  const force = opts?.force === true;
  if (!force && fetchInFlight) return fetchInFlight;
  if (!force && draft() && isRateLimitsDirty(saved(), draft())) {
    return;
  }

  const seq = ++fetchSeq;
  const draftAtStart = canonicalRateLimits(draft());
  setLoading(true);
  setLoadError('');

  const request = (async () => {
    try {
      const { data, etag } = await apiClient.apiJsonWithMeta<AdminRateLimitsDto>('/admin/api/rate-limits');
      if (aborted || seq !== fetchSeq) return;
      if (canonicalRateLimits(draft()) !== draftAtStart && !force) return;
      if (!data) {
        setSaved(null);
        setDraft(null);
        return;
      }
      applyLoaded(data, etag, false);
      void loadRateLimitSchedule();
    } catch (e) {
      if (aborted) return;
      const err = e as { status?: number; message?: string };
      if (!draft()) {
        setSaved(null);
        setDraft(null);
      }
      if (err.status === 404) {
        setLoadError('Rate limit API is not available on this gateway (rebuild/restart the server with the latest image).');
      } else if (err.status === 401 || err.status === 403) {
        setLoadError('Connect with an Admin API key to load rate limits.');
      } else {
        setLoadError(err.message || 'Could not load rate limits.');
      }
      handleApiError(e, 'ratelimits');
    } finally {
      if (!aborted) setLoading(false);
    }
  })();

  const tracked = request.finally(() => {
    if (fetchInFlight === tracked) fetchInFlight = null;
  });
  fetchInFlight = tracked;
  return tracked;
}

export async function saveRateLimits(): Promise<void> {
  const currentDraft = draft();
  const baseline = saved();
  if (!currentDraft || !baseline || saving() || readOnlyReason()) return;
  if (!isRateLimitsDirty(baseline, currentDraft)) return;

  setSaving(true);
  try {
    const payload = buildRateLimitsPayload(currentDraft);
    const headers = ifMatchHeaders(matchVersion());
    const { data, etag } = await apiClient.apiJsonWithMeta<AdminRateLimitsDto>('/admin/api/rate-limits', {
      method: 'PUT',
      headers,
      body: JSON.stringify(payload),
    });
    if (data) {
      applyLoaded(data, etag, false);
      pushToast('Rate limits saved.');
    }
  } catch (e) {
    handleApiError(e, 'ratelimits');
    throw e;
  } finally {
    setSaving(false);
  }
}

export function discardRateLimitChanges(): void {
  const baseline = saved();
  if (!baseline) return;
  setDraft(cloneRateLimitsConfig(baseline));
  pushToast('Changes discarded.');
  queueScheduleRefresh();
}

export function activateRateLimitsPage(): void {
  aborted = false;
  if (!draft() && !loading()) void loadRateLimits();
  if (!usageReport() && !usageLoading()) void loadRateLimitUsage();
}

export function disposeRateLimitsPage(): void {
  aborted = true;
  fetchSeq += 1;
  fetchInFlight = null;
}

export interface AddRuleIntent {
  scope: string;
  target: string;
}

const [addRuleIntent, setAddRuleIntent] = createSignal<AddRuleIntent | null>(null);

export function useAddRuleIntent() {
  return { addRuleIntent, setAddRuleIntent };
}

export function queueAddRuleIntent(scope: string, target: string): void {
  setAddRuleIntent({ scope, target });
}

// ---- usage & schedule API types ----

export interface RateLimitUsageTotals {
  requests?: number;
  Requests?: number;
  admitted?: number;
  Admitted?: number;
  rejected?: number;
  Rejected?: number;
  rateRejected?: number;
  RateRejected?: number;
  concurrencyRejected?: number;
  ConcurrencyRejected?: number;
  rejectionRate?: number;
  RejectionRate?: number;
}

export interface RateLimitUsageRowDto {
  key?: string;
  Key?: string;
  tenantId?: string | null;
  TenantId?: string | null;
  apiKeyId?: string | null;
  ApiKeyId?: string | null;
  modelId?: string | null;
  ModelId?: string | null;
  requests?: number;
  Requests?: number;
  admitted?: number;
  Admitted?: number;
  rejected?: number;
  Rejected?: number;
  requestsPerMinute?: number;
  RequestsPerMinute?: number;
  configuredRpm?: number;
  ConfiguredRpm?: number;
  effectiveRpm?: number;
  EffectiveRpm?: number;
  utilization?: number | null;
  Utilization?: number | null;
}

export interface RateLimitViolationRowDto {
  scope?: string;
  Scope?: string;
  key?: string;
  Key?: string;
  control?: string;
  Control?: string;
  hits?: number;
  Hits?: number;
}

export interface RateLimitLimitUsageRowDto {
  limitId?: string;
  LimitId?: string;
  scope?: string;
  Scope?: string;
  target?: string;
  Target?: string;
  evaluations?: number;
  Evaluations?: number;
  charged?: number;
  Charged?: number;
  refusedByRate?: number;
  RefusedByRate?: number;
  refusedByStreams?: number;
  RefusedByStreams?: number;
  peakUtilization?: number | null;
  PeakUtilization?: number | null;
  peakChargedInOneMinute?: number;
  PeakChargedInOneMinute?: number;
}

export interface RateLimitUsageReportDto {
  windowMinutes?: number;
  WindowMinutes?: number;
  generatedUtc?: string;
  GeneratedUtc?: string;
  totals?: RateLimitUsageTotals;
  Totals?: RateLimitUsageTotals;
  byTenantModel?: RateLimitUsageRowDto[];
  ByTenantModel?: RateLimitUsageRowDto[];
  byTenant?: RateLimitUsageRowDto[];
  ByTenant?: RateLimitUsageRowDto[];
  byModel?: RateLimitUsageRowDto[];
  ByModel?: RateLimitUsageRowDto[];
  byApiKey?: RateLimitUsageRowDto[];
  ByApiKey?: RateLimitUsageRowDto[];
  violations?: RateLimitViolationRowDto[];
  Violations?: RateLimitViolationRowDto[];
  limits?: RateLimitLimitUsageRowDto[];
  Limits?: RateLimitLimitUsageRowDto[];
  adaptive?: { enabled?: boolean; Enabled?: boolean; models?: unknown[]; Models?: unknown[] };
  Adaptive?: { enabled?: boolean; Enabled?: boolean; models?: unknown[]; Models?: unknown[] };
  store?: { requestPartitions?: number; RequestPartitions?: number; streamPartitions?: number; StreamPartitions?: number; maxPartitions?: number; MaxPartitions?: number };
  Store?: { requestPartitions?: number; RequestPartitions?: number; streamPartitions?: number; StreamPartitions?: number; maxPartitions?: number; MaxPartitions?: number };
  tracker?: { isSaturated?: boolean; IsSaturated?: boolean; trackingSinceUtc?: string | null; TrackingSinceUtc?: string | null };
  Tracker?: { isSaturated?: boolean; IsSaturated?: boolean; trackingSinceUtc?: string | null; TrackingSinceUtc?: string | null };
}

export interface RateLimitUsagePointDto {
  startUtc?: string;
  StartUtc?: string;
  covered?: boolean;
  Covered?: boolean;
  decisions?: number;
  Decisions?: number;
  admitted?: number;
  Admitted?: number;
  refusedByRate?: number;
  RefusedByRate?: number;
  refusedByStreams?: number;
  RefusedByStreams?: number;
}

export interface RateLimitUsageSeriesDto {
  bucketMinutes?: number;
  BucketMinutes?: number;
  trackingSinceUtc?: string | null;
  TrackingSinceUtc?: string | null;
  points?: RateLimitUsagePointDto[];
  Points?: RateLimitUsagePointDto[];
}

export interface ScheduleRuleStatusDto {
  scope?: string;
  Scope?: string;
  target?: string;
  Target?: string;
  effective?: { rpm?: number; Rpm?: number; burst?: number; Burst?: number; maxConcurrentStreams?: number; MaxConcurrentStreams?: number; suspended?: boolean; Suspended?: boolean };
  Effective?: { rpm?: number; Rpm?: number; burst?: number; Burst?: number; maxConcurrentStreams?: number; MaxConcurrentStreams?: number; suspended?: boolean; Suspended?: boolean };
  activeWindow?: string | null;
  ActiveWindow?: string | null;
  activeUntil?: string | null;
  ActiveUntil?: string | null;
}

export interface RateLimitScheduleReportDto {
  at?: string;
  At?: string;
  rules?: ScheduleRuleStatusDto[];
  Rules?: ScheduleRuleStatusDto[];
}

export interface RateLimitWindowPreviewDto {
  valid?: boolean;
  Valid?: boolean;
  error?: string | null;
  Error?: string | null;
  activeNow?: boolean;
  ActiveNow?: boolean;
  nextStartAt?: string | null;
  NextStartAt?: string | null;
  nextEndAt?: string | null;
  NextEndAt?: string | null;
  overlaps?: string[];
  Overlaps?: string[];
}

function usageRowFromRaw(r: RateLimitUsageRowDto) {
  return {
    key: String(r.key ?? r.Key ?? ''),
    tenantId: r.tenantId ?? r.TenantId ?? null,
    apiKeyId: r.apiKeyId ?? r.ApiKeyId ?? null,
    modelId: r.modelId ?? r.ModelId ?? null,
    requests: Number(r.requests ?? r.Requests ?? 0),
    admitted: Number(r.admitted ?? r.Admitted ?? 0),
    rejected: Number(r.rejected ?? r.Rejected ?? 0),
    requestsPerMinute: Number(r.requestsPerMinute ?? r.RequestsPerMinute ?? 0),
    configuredRpm: Number(r.configuredRpm ?? r.ConfiguredRpm ?? 0),
    effectiveRpm: Number(r.effectiveRpm ?? r.EffectiveRpm ?? 0),
    utilization: r.utilization ?? r.Utilization ?? null,
  };
}

export function normalizeUsageReport(data: RateLimitUsageReportDto | null | undefined) {
  if (!data) return null;
  const totalsRaw = data.totals ?? data.Totals ?? {};
  const rows = (key: keyof RateLimitUsageReportDto, alt: keyof RateLimitUsageReportDto) =>
    (Array.isArray(data[key]) ? data[key] : Array.isArray(data[alt]) ? data[alt] : []) as RateLimitUsageRowDto[];
  return {
    windowMinutes: Number(data.windowMinutes ?? data.WindowMinutes ?? 60),
    generatedUtc: String(data.generatedUtc ?? data.GeneratedUtc ?? ''),
    totals: {
      requests: Number(totalsRaw.requests ?? totalsRaw.Requests ?? 0),
      admitted: Number(totalsRaw.admitted ?? totalsRaw.Admitted ?? 0),
      rejected: Number(totalsRaw.rejected ?? totalsRaw.Rejected ?? 0),
      rejectionRate: Number(totalsRaw.rejectionRate ?? totalsRaw.RejectionRate ?? 0),
    },
    byTenantModel: rows('byTenantModel', 'ByTenantModel').map(usageRowFromRaw),
    byTenant: rows('byTenant', 'ByTenant').map(usageRowFromRaw),
    byModel: rows('byModel', 'ByModel').map(usageRowFromRaw),
    byApiKey: rows('byApiKey', 'ByApiKey').map(usageRowFromRaw),
    violations: ((Array.isArray(data.violations ?? data.Violations) ? (data.violations ?? data.Violations) : []) as RateLimitViolationRowDto[]).map((v) => {
      const row = v as RateLimitViolationRowDto;
      return {
        scope: String(row.scope ?? row.Scope ?? ''),
        key: String(row.key ?? row.Key ?? ''),
        control: String(row.control ?? row.Control ?? ''),
        hits: Number(row.hits ?? row.Hits ?? 0),
      };
    }),
    limits: ((Array.isArray(data.limits ?? data.Limits) ? (data.limits ?? data.Limits) : []) as RateLimitLimitUsageRowDto[]).map((row) => {
      return {
        limitId: String(row.limitId ?? row.LimitId ?? '').toLowerCase(),
        scope: String(row.scope ?? row.Scope ?? ''),
        target: String(row.target ?? row.Target ?? ''),
        evaluations: Number(row.evaluations ?? row.Evaluations ?? 0),
        charged: Number(row.charged ?? row.Charged ?? 0),
        refusedByRate: Number(row.refusedByRate ?? row.RefusedByRate ?? 0),
        refusedByStreams: Number(row.refusedByStreams ?? row.RefusedByStreams ?? 0),
        peakUtilization: row.peakUtilization ?? row.PeakUtilization ?? null,
        peakChargedInOneMinute: Number(row.peakChargedInOneMinute ?? row.PeakChargedInOneMinute ?? 0),
      };
    }),
    adaptiveEnabled: !!(data.adaptive?.enabled ?? data.Adaptive?.enabled ?? data.adaptive?.Enabled ?? data.Adaptive?.Enabled),
    trackerSaturated: !!(data.tracker?.isSaturated ?? data.Tracker?.isSaturated ?? data.tracker?.IsSaturated ?? data.Tracker?.IsSaturated),
    store: {
      requestPartitions: Number(data.store?.requestPartitions ?? data.Store?.requestPartitions ?? data.store?.RequestPartitions ?? 0),
      streamPartitions: Number(data.store?.streamPartitions ?? data.Store?.streamPartitions ?? data.store?.StreamPartitions ?? 0),
      maxPartitions: Number(data.store?.maxPartitions ?? data.Store?.maxPartitions ?? data.store?.MaxPartitions ?? 0),
    },
  };
}

export function normalizeUsageSeries(data: RateLimitUsageSeriesDto | null | undefined) {
  if (!data) return null;
  const points = (Array.isArray(data.points ?? data.Points) ? (data.points ?? data.Points) : []) as RateLimitUsagePointDto[];
  return {
    bucketMinutes: Number(data.bucketMinutes ?? data.BucketMinutes ?? 1),
    trackingSinceUtc: data.trackingSinceUtc ?? data.TrackingSinceUtc ?? null,
    points: points.map((p) => ({
      startUtc: String(p.startUtc ?? p.StartUtc ?? ''),
      covered: (p.covered ?? p.Covered) !== false,
      decisions: Number(p.decisions ?? p.Decisions ?? 0),
      admitted: Number(p.admitted ?? p.Admitted ?? 0),
      refusedByRate: Number(p.refusedByRate ?? p.RefusedByRate ?? 0),
      refusedByStreams: Number(p.refusedByStreams ?? p.RefusedByStreams ?? 0),
    })),
  };
}

export function normalizeScheduleReport(data: RateLimitScheduleReportDto | null | undefined) {
  if (!data) return null;
  const rules = (Array.isArray(data.rules ?? data.Rules) ? (data.rules ?? data.Rules) : []) as ScheduleRuleStatusDto[];
  return {
    at: String(data.at ?? data.At ?? ''),
    rules: rules.map((r) => ({
      scope: String(r.scope ?? r.Scope ?? ''),
      target: String(r.target ?? r.Target ?? ''),
      identity: rateLimitRuleIdentity(String(r.scope ?? r.Scope ?? ''), String(r.target ?? r.Target ?? '')),
      effective: r.effective ?? r.Effective ?? {},
      activeWindow: r.activeWindow ?? r.ActiveWindow ?? null,
      activeUntil: r.activeUntil ?? r.ActiveUntil ?? null,
    })),
  };
}

export function findDraftRule(rules: RateLimitRule[], identity: string): RateLimitRule | undefined {
  return rules.find((r) => rateLimitRuleIdentity(r.scope, r.target) === identity);
}

export function ruleFormSnapshot(rule: Pick<RateLimitRule, 'rpm' | 'burst' | 'maxConcurrentStreams' | 'enabled' | 'schedule'>): string {
  return JSON.stringify([
    tierPayload(rule),
    rule.enabled !== false,
    rule.schedule || [],
  ]);
}

export function undoRateLimitChange(
  draft: RateLimitConfig,
  saved: RateLimitConfig,
  id: string,
): RateLimitConfig {
  const next = cloneRateLimitsConfig(draft);
  if (id === 'enabled') next.enabled = saved.enabled;
  else if (id === 'adaptive') next.adaptiveEnabled = saved.adaptiveEnabled;
  else if (id === 'default') next.default = cloneRateLimitsConfig(saved.default);
  else if (id.startsWith('plan:')) {
    const slug = id.slice(5);
    const plans = { ...(next.plans || {}) };
    for (const k of Object.keys(plans)) {
      if (k.toLowerCase() === slug.toLowerCase()) delete plans[k];
    }
    const stored = Object.keys(saved.plans || {}).find((k) => k.toLowerCase() === slug.toLowerCase());
    if (stored) plans[stored] = cloneRateLimitsConfig(saved.plans[stored]);
    next.plans = plans;
  } else if (id.startsWith('rule:')) {
    const identity = id.slice(5);
    const stored = findDraftRule(saved.rules, identity);
    const rules = next.rules.filter((r) => rateLimitRuleIdentity(r.scope, r.target) !== identity);
    const at = next.rules.findIndex((r) => rateLimitRuleIdentity(r.scope, r.target) === identity);
    if (stored) rules.splice(at >= 0 ? at : rules.length, 0, cloneRateLimitsConfig(stored));
    next.rules = rules;
  }
  return next;
}

export function buildWindowPreviewBody(
  rule: Pick<RateLimitRule, 'scope' | 'target' | 'rpm' | 'burst' | 'maxConcurrentStreams'>,
  schedule: RateLimitWindow[],
  candidate: RateLimitWindow,
  editIndex: number,
): { scope: string; target: string; rpm: number; burst: number; maxConcurrentStreams: number; windows: RateLimitWindow[]; candidate: string } {
  const others = schedule.filter((_, i) => i !== editIndex);
  return {
    scope: rule.scope,
    target: rule.target,
    ...tierPayload(rule),
    windows: [...others, candidate].map((w) => windowPayload(w)),
    candidate: candidate.name,
  };
}

// ---- extended store state ----

export type RateLimitUsageTab = 'tenantModel' | 'tenant' | 'model' | 'key';

export interface TierDrawerState {
  kind: 'default' | 'plan';
  slug: string;
  originalSlug: string;
  isNew: boolean;
  rpm: number;
  burst: number;
  maxConcurrentStreams: number;
}

export interface RuleDrawerState {
  identity: string;
  scope: string;
  target: string;
  rpm: number;
  burst: number;
  maxConcurrentStreams: number;
  enabled: boolean;
  schedule: RateLimitWindow[];
}

const [usageReport, setUsageReport] = createSignal<ReturnType<typeof normalizeUsageReport>>(null);
const [usageSeries, setUsageSeries] = createSignal<ReturnType<typeof normalizeUsageSeries>>(null);
const [usageMinutes, setUsageMinutes] = createSignal(60);
const [usageTab, setUsageTab] = createSignal<RateLimitUsageTab>('tenantModel');
const [usageLoading, setUsageLoading] = createSignal(false);
const [usageError, setUsageError] = createSignal('');
const [usageStale, setUsageStale] = createSignal(false);
const [usageUnavailable, setUsageUnavailable] = createSignal(false);
const [usageLoadedAt, setUsageLoadedAt] = createSignal(0);

const [scheduleSaved, setScheduleSaved] = createSignal<ReturnType<typeof normalizeScheduleReport>>(null);
const [scheduleError, setScheduleError] = createSignal('');

const [reviewOpen, setReviewOpen] = createSignal(false);
const [tierDrawer, setTierDrawer] = createSignal<TierDrawerState | null>(null);
const [tierDrawerOpen, setTierDrawerOpen] = createSignal(false);
const [tierDrawerError, setTierDrawerError] = createSignal('');
const [ruleDrawer, setRuleDrawer] = createSignal<RuleDrawerState | null>(null);
const [ruleDrawerOpen, setRuleDrawerOpen] = createSignal(false);
const [ruleDrawerError, setRuleDrawerError] = createSignal('');
const [windowEditIndex, setWindowEditIndex] = createSignal(-1);
const [windowForm, setWindowForm] = createSignal<RateLimitWindow | null>(null);
const [windowOpen, setWindowOpen] = createSignal(false);
const [windowError, setWindowError] = createSignal('');
const [windowPreview, setWindowPreview] = createSignal<RateLimitWindowPreviewDto | null>(null);

let usageSeq = 0;
let seriesSeq = 0;
let scheduleSeq = 0;
let previewTimer: ReturnType<typeof setTimeout> | null = null;
let previewSeq = 0;
let scheduleRefreshTimer: ReturnType<typeof setTimeout> | null = null;

function queueScheduleRefresh(): void {
  if (scheduleRefreshTimer) clearTimeout(scheduleRefreshTimer);
  scheduleRefreshTimer = setTimeout(() => {
    scheduleRefreshTimer = null;
    void loadRateLimitSchedule();
  }, 300);
}

function editable(): boolean {
  return !readOnlyReason() && !saving();
}

export async function loadRateLimitUsage(): Promise<void> {
  const seq = ++usageSeq;
  setUsageLoading(true);
  try {
    const minutes = Number(usageMinutes()) || 60;
    const take = usageTake((saved()?.rules || draft()?.rules || []).length);
    const report = await apiClient.apiJson<RateLimitUsageReportDto>(
      `/admin/api/rate-limits/usage?minutes=${minutes}&take=${take}`,
    );
    if (seq !== usageSeq) return;
    setUsageReport(normalizeUsageReport(report));
    setUsageError('');
    setUsageStale(false);
    setUsageUnavailable(false);
    setUsageLoadedAt(Date.now());
    void loadRateLimitUsageSeries();
  } catch (e) {
    if (seq !== usageSeq) return;
    const err = e as { status?: number; message?: string };
    setUsageUnavailable(err.status === 503);
    setUsageStale(!!usageReport());
    setUsageError(err.message || 'Could not load rate-limit activity.');
  } finally {
    if (seq === usageSeq) setUsageLoading(false);
  }
}

export async function loadRateLimitUsageSeries(): Promise<void> {
  const seq = ++seriesSeq;
  try {
    const minutes = Number(usageMinutes()) || 60;
    const bucket = seriesBucketMinutes(minutes);
    const series = await apiClient.apiJson<RateLimitUsageSeriesDto>(
      `/admin/api/rate-limits/usage/timeseries?minutes=${minutes}&bucketMinutes=${bucket}`,
    );
    if (seq !== seriesSeq) return;
    setUsageSeries(normalizeUsageSeries(series));
  } catch {
    if (seq !== seriesSeq) return;
    setUsageSeries(null);
  }
}

export async function loadRateLimitSchedule(): Promise<void> {
  const seq = ++scheduleSeq;
  setScheduleError('');
  const from = new Date();
  const to = new Date(from.getTime() + 7 * 86400000);
  const query = `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&take=200`;
  const dirty = isRateLimitsDirty(saved(), draft());
  try {
    const savedReq = apiClient.apiJson<RateLimitScheduleReportDto>(`/admin/api/rate-limits/schedule?${query}`);
    const draftReq = dirty
      ? apiClient.apiJson<RateLimitScheduleReportDto>('/admin/api/rate-limits/schedule/preview', {
          method: 'POST',
          body: JSON.stringify({
            rules: buildRateLimitsPayload(draft()).rules,
            from: from.toISOString(),
            to: to.toISOString(),
            take: 200,
          }),
        })
      : savedReq;
    const [savedRes, draftRes] = await Promise.allSettled([savedReq, draftReq]);
    if (seq !== scheduleSeq) return;
    if (savedRes.status === 'fulfilled') setScheduleSaved(normalizeScheduleReport(savedRes.value));
    else {
      setScheduleSaved(null);
      setScheduleError(savedRes.reason?.message || 'Could not load the schedule.');
    }
    if (draftRes.status === 'rejected' && !scheduleError()) {
      setScheduleError(draftRes.reason?.message || 'Could not load the schedule.');
    }
  } catch (e) {
    if (seq !== scheduleSeq) return;
    setScheduleError((e as Error).message || 'Could not load the schedule.');
  }
}

export async function previewRateLimitWindow(
  rule: RuleDrawerState,
  schedule: RateLimitWindow[],
  candidate: RateLimitWindow,
  editIndex: number,
): Promise<RateLimitWindowPreviewDto | null> {
  const seq = ++previewSeq;
  const body = buildWindowPreviewBody(rule, schedule, candidate, editIndex);
  if (!candidate.name.trim()) {
    setWindowPreview(null);
    return null;
  }
  try {
    const preview = await apiClient.apiJson<RateLimitWindowPreviewDto>('/admin/api/rate-limits/windows/preview', {
      method: 'POST',
      body: JSON.stringify(body),
    });
    if (seq !== previewSeq) return null;
    setWindowPreview(preview);
    return preview;
  } catch (e) {
    if (seq !== previewSeq) return null;
    setWindowPreview(null);
    setWindowError((e as Error).message || 'Could not check the window.');
    return null;
  }
}

export function queueWindowPreview(): void {
  if (previewTimer) clearTimeout(previewTimer);
  previewTimer = setTimeout(() => {
    previewTimer = null;
    const rule = ruleDrawer();
    const form = windowForm();
    if (!rule || !form || !windowOpen()) return;
    void previewRateLimitWindow(rule, rule.schedule, form, windowEditIndex());
  }, 250);
}

export function openTierDrawer(kind: 'default' | 'plan', slug = ''): void {
  const cfg = draft();
  if (!cfg) return;
  if (!editable() && kind === 'plan' && !slug) return;
  const tier =
    kind === 'default'
      ? cfg.default
      : cfg.plans[slug] || { rpm: 60, burst: 10, maxConcurrentStreams: 5 };
  setTierDrawer({
    kind,
    slug: slug || '',
    originalSlug: slug || '',
    isNew: kind === 'plan' && !slug,
    rpm: tier.rpm,
    burst: tier.burst,
    maxConcurrentStreams: tier.maxConcurrentStreams,
  });
  setTierDrawerError('');
  setTierDrawerOpen(true);
}

export function closeTierDrawer(): void {
  setTierDrawerOpen(false);
  setTierDrawer(null);
}

export function applyTierDrawer(): boolean {
  const t = tierDrawer();
  const cfg = draft();
  if (!t || !cfg || !editable()) return false;
  const err = validateTierFields(
    { rpm: t.rpm, burst: t.burst, maxConcurrentStreams: t.maxConcurrentStreams },
    { floorRpm: true },
  );
  if (err) {
    setTierDrawerError(err);
    return false;
  }
  if (t.kind === 'plan') {
    const slugErr = validatePlanSlug(t.slug, cfg.plans, t.originalSlug);
    if (slugErr) {
      setTierDrawerError(slugErr);
      return false;
    }
    patchDraft((d) => {
      const slug = String(t.slug || '').trim();
      if (t.originalSlug && t.originalSlug !== slug) delete d.plans[t.originalSlug];
      d.plans[slug] = tierPayload(t);
    });
  } else {
    patchDraft((d) => {
      d.default = tierPayload(t);
    });
  }
  closeTierDrawer();
  queueScheduleRefresh();
  return true;
}

export function removePlanFromDrawer(): void {
  const t = tierDrawer();
  if (!t?.originalSlug) return;
  patchDraft((d) => {
    delete d.plans[t.originalSlug];
  });
  closeTierDrawer();
  queueScheduleRefresh();
}

export function openRuleDrawer(identity: string): void {
  const cfg = draft();
  if (!cfg) return;
  const rule = findDraftRule(cfg.rules, identity);
  if (!rule) return;
  setRuleDrawer({
    identity,
    scope: rule.scope,
    target: rule.target,
    rpm: rule.rpm,
    burst: rule.burst,
    maxConcurrentStreams: rule.maxConcurrentStreams,
    enabled: rule.enabled !== false,
    schedule: cloneRateLimitsConfig(rule.schedule || []),
  });
  setRuleDrawerError('');
  setWindowOpen(false);
  setRuleDrawerOpen(true);
  if (!usageReport()) void loadRateLimitUsage();
}

export function closeRuleDrawer(): void {
  setRuleDrawerOpen(false);
  setWindowOpen(false);
  setRuleDrawer(null);
}

export function applyRuleDrawer(): boolean {
  const form = ruleDrawer();
  const cfg = draft();
  if (!form || !cfg || !editable()) return false;
  const err = validateTierFields(
    { rpm: form.rpm, burst: form.burst, maxConcurrentStreams: form.maxConcurrentStreams },
    { scope: form.scope },
  );
  if (err) {
    setRuleDrawerError(err);
    return false;
  }
  patchDraft((d) => {
    const rule = findDraftRule(d.rules, form.identity);
    if (!rule) return;
    Object.assign(rule, tierPayload(form), {
      enabled: form.enabled !== false,
      schedule: cloneRateLimitsConfig(form.schedule),
    });
  });
  closeRuleDrawer();
  queueScheduleRefresh();
  return true;
}

export function deleteRuleFromDrawer(): void {
  const form = ruleDrawer();
  if (!form) return;
  patchDraft((d) => {
    d.rules = d.rules.filter((r) => rateLimitRuleIdentity(r.scope, r.target) !== form.identity);
  });
  closeRuleDrawer();
  queueScheduleRefresh();
}

export function openWindowEditor(index: number): void {
  const rule = ruleDrawer();
  if (!rule) return;
  const existing = index >= 0 ? rule.schedule[index] : null;
  setWindowEditIndex(existing ? index : -1);
  setWindowForm(existing ? cloneRateLimitsConfig(existing) : blankWindow(rule));
  setWindowError('');
  setWindowPreview(null);
  setWindowOpen(true);
  queueWindowPreview();
}

export function closeWindowEditor(): void {
  setWindowOpen(false);
  setWindowForm(null);
  setWindowPreview(null);
}

export function applyWindowEditor(): boolean {
  const rule = ruleDrawer();
  const form = windowForm();
  if (!rule || !form) return false;
  if (!form.name.trim()) {
    setWindowError('Give the window a name.');
    return false;
  }
  const payload = windowPayload(form);
  const duplicate = rule.schedule.some(
    (w, i) => i !== windowEditIndex() && w.name.toLowerCase() === payload.name.toLowerCase(),
  );
  if (duplicate) {
    setWindowError('Another window on this rule already has that name.');
    return false;
  }
  const nextSchedule = [...rule.schedule];
  const idx = windowEditIndex();
  if (idx >= 0) nextSchedule[idx] = payload;
  else nextSchedule.push(payload);
  setRuleDrawer({ ...rule, schedule: nextSchedule });
  closeWindowEditor();
  queueWindowPreview();
  queueScheduleRefresh();
  return true;
}

export function removeWindow(index: number): void {
  const rule = ruleDrawer();
  if (!rule) return;
  setRuleDrawer({
    ...rule,
    schedule: rule.schedule.filter((_, i) => i !== index),
  });
  queueScheduleRefresh();
}

export function updateTierDrawerField(field: keyof RateLimitTier, value: number): void {
  const t = tierDrawer();
  if (!t) return;
  setTierDrawer({ ...t, [field]: value });
}

export function updateTierDrawerSlug(slug: string): void {
  const t = tierDrawer();
  if (!t) return;
  setTierDrawer({ ...t, slug });
}

export function updateRuleDrawerField(
  field: keyof Pick<RuleDrawerState, 'rpm' | 'burst' | 'maxConcurrentStreams' | 'enabled' | 'target'>,
  value: number | boolean | string,
): void {
  const r = ruleDrawer();
  if (!r) return;
  setRuleDrawer({ ...r, [field]: value } as RuleDrawerState);
}

export function updateWindowForm(patch: Partial<RateLimitWindow>): void {
  const f = windowForm();
  if (!f) return;
  setWindowForm({ ...f, ...patch });
  queueWindowPreview();
}

export function toggleWindowDay(day: string): void {
  const f = windowForm();
  if (!f) return;
  const days = new Set(f.days || []);
  if (days.has(day)) days.delete(day);
  else days.add(day);
  setWindowForm({ ...f, days: [...days] });
  queueWindowPreview();
}

export function tierDrawerDirty(): boolean {
  const t = tierDrawer();
  const cfg = draft();
  if (!tierDrawerOpen() || !t || !cfg) return false;
  if (t.isNew) return true;
  const source = t.kind === 'default' ? cfg.default : cfg.plans[t.originalSlug];
  if (!source) return true;
  if (String(t.slug || '') !== String(t.originalSlug || '')) return true;
  return JSON.stringify(tierPayload(t)) !== JSON.stringify(tierPayload(source));
}

export function ruleDrawerDirty(): boolean {
  const form = ruleDrawer();
  const cfg = draft();
  if (!ruleDrawerOpen() || !form || !cfg) return false;
  const rule = findDraftRule(cfg.rules, form.identity);
  if (!rule) return false;
  return ruleFormSnapshot(form) !== ruleFormSnapshot(rule);
}

export function windowEditorDirty(): boolean {
  if (!windowOpen() || !windowForm()) return false;
  const blank = blankWindow(ruleDrawer() ?? undefined);
  return JSON.stringify(windowForm()) !== JSON.stringify(blank);
}

export function rateLimitsWorkInProgress(): boolean {
  return (
    isRateLimitsDirty(saved(), draft()) ||
    tierDrawerDirty() ||
    ruleDrawerDirty() ||
    windowEditorDirty()
  );
}

export function dirtyView(): RateLimitDirtyView {
  return computeDirtyView(saved(), draft(), isRateLimitsDirty(saved(), draft()));
}

export function undoDirtyChange(id: string): void {
  const baseline = saved();
  const current = draft();
  if (!baseline || !current || !editable()) return;
  setDraft(undoRateLimitChange(current, baseline, id));
  if (!isRateLimitsDirty(saved(), draft())) setReviewOpen(false);
  queueScheduleRefresh();
}

export async function requestSaveRateLimits(): Promise<void> {
  const view = dirtyView();
  if (!reviewOpen() && view.destructive > 0) {
    setReviewOpen(true);
    return;
  }
  await saveRateLimits();
  setReviewOpen(false);
}

export function useRateLimitActivityStore() {
  return {
    usageReport,
    usageSeries,
    usageMinutes,
    usageTab,
    usageLoading,
    usageError,
    usageStale,
    usageUnavailable,
    usageLoadedAt,
    setUsageMinutes: (m: number) => {
      setUsageMinutes(m);
      void loadRateLimitUsage();
    },
    setUsageTab,
    loadUsage: loadRateLimitUsage,
  };
}

export function useRateLimitDrawersStore() {
  return {
    tierDrawer,
    tierDrawerOpen,
    tierDrawerError,
    ruleDrawer,
    ruleDrawerOpen,
    ruleDrawerError,
    windowEditIndex,
    windowForm,
    windowOpen,
    windowError,
    windowPreview,
    scheduleSaved,
    scheduleError,
    reviewOpen,
    setReviewOpen,
    openTierDrawer,
    closeTierDrawer,
    applyTierDrawer,
    removePlanFromDrawer,
    openRuleDrawer,
    closeRuleDrawer,
    applyRuleDrawer,
    deleteRuleFromDrawer,
    openWindowEditor,
    closeWindowEditor,
    applyWindowEditor,
    removeWindow,
    updateTierDrawerField,
    updateTierDrawerSlug,
    updateRuleDrawerField,
    updateWindowForm,
    toggleWindowDay,
    tierDrawerDirty,
    ruleDrawerDirty,
    windowEditorDirty,
    dirtyView,
    undoDirtyChange,
    requestSave: requestSaveRateLimits,
    loadSchedule: loadRateLimitSchedule,
  };
}

export { diffRateLimits, computeDirtyView, type RateLimitDiffItem, type RateLimitDirtyView };
