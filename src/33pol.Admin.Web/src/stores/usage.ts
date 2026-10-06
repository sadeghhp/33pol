import { createSignal } from 'solid-js';
import { createResource, RESOURCE_FRESH_MS } from '../realtime/resources';
import { apiClient, handleApiError, pushToast } from './auth';

const ROLLUP_CAP = 100;
const EVENTS_CAP = 200;

export interface UsageSnapshot {
  from: string;
  to: string;
  costCenter: string;
  apiKeyId: string;
  modelId: string;
  includeAnonymous: boolean;
}

const [from, setFrom] = createSignal('');
const [to, setTo] = createSignal('');
const [costCenter, setCostCenter] = createSignal('');
const [apiKeyId, setApiKeyId] = createSignal('');
const [modelId, setModelId] = createSignal('');
const [includeAnonymous, setIncludeAnonymous] = createSignal(
  localStorage.getItem('33pol-usage-anon') !== 'false',
);
const [loadedSnapshot, setLoadedSnapshot] = createSignal<UsageSnapshot | null>(null);
const [eventsCursor, setEventsCursor] = createSignal<string | null>(null);
const [eventsHasMore, setEventsHasMore] = createSignal(false);
const [rollup, setRollup] = createSignal<Record<string, unknown>[]>([]);
const [events, setEvents] = createSignal<Record<string, unknown>[]>([]);
const [keyShares, setKeyShares] = createSignal<Record<string, unknown> | null>(null);
const [summary, setSummary] = createSignal<Record<string, unknown> | null>(null);

export function usagePresetRange(days: number | 'mtd'): { from: string; to: string } {
  const toDate = new Date();
  const fromDate = new Date(toDate);
  if (days === 'mtd') fromDate.setUTCDate(1);
  else fromDate.setUTCDate(fromDate.getUTCDate() - (Number(days) - 1));
  return { from: fromDate.toISOString().slice(0, 10), to: toDate.toISOString().slice(0, 10) };
}

export function usageRangeError(fromVal: string, toVal: string): string {
  if (!fromVal || !toVal) return '';
  if (fromVal > toVal) return '"From" must be on or before "To".';
  const days = (Date.parse(toVal) - Date.parse(fromVal)) / 86400000 + 1;
  if (days > 366) return 'The range may span at most 366 days.';
  return '';
}

export function usageSnapshot(): UsageSnapshot {
  return {
    from: from(),
    to: to(),
    costCenter: costCenter().trim(),
    apiKeyId: apiKeyId(),
    modelId: modelId().trim(),
    includeAnonymous: includeAnonymous(),
  };
}

export function usageParamsFrom(snap: UsageSnapshot, extra?: Record<string, string | number | boolean>): string {
  const q = new URLSearchParams();
  if (snap.from) q.set('from', snap.from);
  if (snap.to) q.set('to', snap.to);
  if (snap.costCenter) q.set('costCenter', snap.costCenter);
  if (snap.apiKeyId) q.set('apiKeyId', snap.apiKeyId);
  if (snap.modelId) q.set('modelId', snap.modelId);
  if (snap.includeAnonymous) q.set('includeAnonymous', 'true');
  if (extra) {
    for (const [k, v] of Object.entries(extra)) {
      if (v !== undefined && v !== null && v !== '') q.set(k, String(v));
    }
  }
  return q.toString();
}

const resource = createResource<Record<string, unknown>>({
  freshMs: RESOURCE_FRESH_MS.usage,
  fetch: async (signal) => {
    const snap = loadedSnapshot();
    if (!snap) return {};
    const params = usageParamsFrom(snap, { rollupLimit: ROLLUP_CAP });
    const data = await apiClient.apiJson<Record<string, unknown>>(`/admin/api/usage?${params}`, { signal });
    return data ?? {};
  },
});

export function initUsageDates(): void {
  const { from: f, to: t } = usagePresetRange(30);
  setFrom(f);
  setTo(t);
}

export function applyHashUsageParams(params: Record<string, string | string[] | undefined>): void {
  const pick = (key: string) => {
    const v = params[key];
    return typeof v === 'string' ? v : undefined;
  };
  const cc = pick('costCenter');
  const key = pick('apiKeyId');
  const model = pick('modelId');
  if (cc != null) setCostCenter(cc);
  if (key != null) setApiKeyId(key);
  if (model != null) setModelId(model);
}

export function useUsageStore() {
  return {
    from,
    to,
    setFrom,
    setTo,
    costCenter,
    setCostCenter,
    apiKeyId,
    setApiKeyId,
    modelId,
    setModelId,
    includeAnonymous,
    setIncludeAnonymous: (on: boolean) => {
      setIncludeAnonymous(on);
      localStorage.setItem('33pol-usage-anon', on ? 'true' : 'false');
    },
    loadedSnapshot,
    rollup,
    events,
    keyShares,
    summary,
    eventsHasMore,
    phase: () => resource.snapshot().phase,
    loadReport,
    loadMoreEvents,
    setPreset,
    exportDataset,
    rangeError: () => usageRangeError(from(), to()),
  };
}

export async function setPreset(days: number | 'mtd'): Promise<void> {
  const { from: f, to: t } = usagePresetRange(days);
  setFrom(f);
  setTo(t);
  await loadReport();
}

export async function loadReport(): Promise<void> {
  const err = usageRangeError(from(), to());
  if (err) {
    pushToast(err, 'error');
    return;
  }
  const snap = usageSnapshot();
  setLoadedSnapshot(snap);
  setEventsCursor(null);
  try {
    const [usageData, keysData, eventsData] = await Promise.all([
      resource.load({ force: true }),
      apiClient.apiJson<Record<string, unknown>>(`/admin/api/usage/keys?${usageParamsFrom(snap)}`),
      apiClient.apiJson<{ events?: Record<string, unknown>[]; hasMore?: boolean; nextCursor?: string }>(
        `/admin/api/usage/events?${usageParamsFrom(snap, { limit: EVENTS_CAP })}`,
      ),
    ]);
    if (usageData) {
      const roll = Array.isArray(usageData.rollup) ? usageData.rollup.slice(0, ROLLUP_CAP) : [];
      setRollup(roll);
      setSummary(usageData);
    }
    setKeyShares(keysData);
    const ev = eventsData?.events ?? [];
    setEvents(ev.slice(0, EVENTS_CAP));
    setEventsHasMore(!!eventsData?.hasMore || ev.length >= EVENTS_CAP);
    setEventsCursor(eventsData?.nextCursor ?? null);
  } catch (e) {
    handleApiError(e, 'usage');
  }
}

export async function loadMoreEvents(): Promise<void> {
  const cursor = eventsCursor();
  const snap = loadedSnapshot();
  if (!cursor || !snap) return;
  try {
    const params = usageParamsFrom(snap, { cursor, limit: EVENTS_CAP });
    const data = await apiClient.apiJson<{ events?: Record<string, unknown>[]; hasMore?: boolean; nextCursor?: string }>(
      `/admin/api/usage/events?${params}`,
    );
    const next = data?.events ?? [];
    setEvents((prev) => [...prev, ...next].slice(0, EVENTS_CAP * 2));
    setEventsHasMore(!!data?.hasMore);
    setEventsCursor(data?.nextCursor ?? null);
  } catch (e) {
    handleApiError(e, 'usage');
  }
}

export async function exportDataset(dataset: 'rollups' | 'events', format: 'json' | 'csv'): Promise<void> {
  const snap = loadedSnapshot() ?? usageSnapshot();
  const err = usageRangeError(snap.from, snap.to);
  if (err) {
    pushToast(err, 'error');
    return;
  }
  const ext = format === 'csv' ? 'csv' : 'json';
  try {
    const res = await apiClient.downloadBlob(
      `/admin/api/usage/export?${usageParamsFrom(snap, { dataset, format })}`,
      `usage-${dataset}.${ext}`,
    );
    const truncated = res.headers?.get?.('X-Export-Truncated') === 'true';
    pushToast(
      truncated
        ? 'Export downloaded — capped at 5,000 events; narrow the range for the rest.'
        : 'Export downloaded.',
      truncated ? 'error' : 'success',
    );
  } catch (e) {
    handleApiError(e, 'usage');
    throw e;
  }
}

export function activateUsagePage(): void {
  if (!from()) initUsageDates();
  const snap = resource.snapshot();
  if (snap.phase === 'idle') void loadReport();
  else if (snap.phase === 'stale') void loadReport();
}

export function disposeUsagePage(): void {
  resource.abort();
}
