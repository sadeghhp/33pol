import { createSignal } from 'solid-js';
import { createResource, RESOURCE_FRESH_MS } from '../realtime/resources';
import { apiClient, handleApiError } from './auth';

const ROLLUP_CAP = 100;
const EVENTS_CAP = 200;

const [from, setFrom] = createSignal('');
const [to, setTo] = createSignal('');
const [loadedFrom, setLoadedFrom] = createSignal('');
const [loadedTo, setLoadedTo] = createSignal('');
const [eventsCursor, setEventsCursor] = createSignal<string | null>(null);
const [eventsHasMore, setEventsHasMore] = createSignal(false);
const [rollup, setRollup] = createSignal<Record<string, unknown>[]>([]);
const [events, setEvents] = createSignal<Record<string, unknown>[]>([]);

const resource = createResource<Record<string, unknown>>({
  freshMs: RESOURCE_FRESH_MS.usage,
  fetch: async (signal) => {
    const params = new URLSearchParams();
    if (loadedFrom()) params.set('from', loadedFrom());
    if (loadedTo()) params.set('to', loadedTo());
    params.set('rollupLimit', String(ROLLUP_CAP));
    const data = await apiClient.apiJson<Record<string, unknown>>(`/admin/api/usage?${params}`, { signal });
    return data ?? {};
  },
});

export function useUsageStore() {
  return {
    from,
    to,
    setFrom,
    setTo,
    loadedFrom,
    loadedTo,
    rollup,
    events,
    eventsHasMore,
    phase: () => resource.snapshot().phase,
    loadReport,
    loadMoreEvents,
  };
}

export async function loadReport(): Promise<void> {
  setLoadedFrom(from());
  setLoadedTo(to());
  setEventsCursor(null);
  try {
    const data = await resource.load({ force: true });
    if (!data) return;
    const roll = Array.isArray(data.rollup) ? data.rollup.slice(0, ROLLUP_CAP) : [];
    setRollup(roll);
    const ev = Array.isArray(data.events) ? data.events.slice(0, EVENTS_CAP) : [];
    setEvents(ev);
    setEventsHasMore(!!data.eventsHasMore || ev.length >= EVENTS_CAP);
    setEventsCursor((data.eventsCursor as string) ?? null);
  } catch (e) {
    handleApiError(e, 'usage');
  }
}

export async function loadMoreEvents(): Promise<void> {
  const cursor = eventsCursor();
  if (!cursor) return;
  try {
    const params = new URLSearchParams({ cursor, limit: String(EVENTS_CAP) });
    if (loadedFrom()) params.set('from', loadedFrom());
    if (loadedTo()) params.set('to', loadedTo());
    const data = await apiClient.apiJson<{ events?: Record<string, unknown>[]; eventsHasMore?: boolean; eventsCursor?: string }>(
      `/admin/api/usage/events?${params}`,
    );
    const next = data?.events ?? [];
    setEvents((prev) => [...prev, ...next].slice(0, EVENTS_CAP * 2));
    setEventsHasMore(!!data?.eventsHasMore);
    setEventsCursor(data?.eventsCursor ?? null);
  } catch (e) {
    handleApiError(e, 'usage');
  }
}

export function activateUsagePage(): void {
  const snap = resource.snapshot();
  if (snap.phase === 'idle') void loadReport();
  else if (snap.phase === 'stale') void resource.load({ background: true });
}

export function disposeUsagePage(): void {
  resource.abort();
}
