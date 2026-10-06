import { createSignal } from 'solid-js';
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

export function buildRateLimitsPayload(source: RateLimitConfig | null | undefined): RateLimitsPayload {
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
    load: (opts?: { force?: boolean }) => loadRateLimits(opts),
    save: () => saveRateLimits(),
    discard: () => discardRateLimitChanges(),
    setEnabled: (value: boolean) => patchDraft((d) => { d.enabled = value; }),
    setAdaptiveEnabled: (value: boolean) => patchDraft((d) => { d.adaptiveEnabled = value; }),
    setDefaultTier: (field: keyof RateLimitTier, value: number) =>
      patchDraft((d) => { d.default = { ...d.default, [field]: value }; }),
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
}

export function activateRateLimitsPage(): void {
  aborted = false;
  if (!draft() && !loading()) void loadRateLimits();
}

export function disposeRateLimitsPage(): void {
  aborted = true;
  fetchSeq += 1;
  fetchInFlight = null;
}
