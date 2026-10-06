export type ResourcePhase = 'idle' | 'loading' | 'ready' | 'stale' | 'error' | 'refreshing';

export interface ResourceSnapshot<T> {
  phase: ResourcePhase;
  data: T | null;
  error: unknown;
  fetchedAt: number;
  seq: number;
}

export interface ResourceOptions<T> {
  freshMs: number;
  fetch: (signal: AbortSignal) => Promise<T>;
  now?: () => number;
}

export interface ResourceController<T> {
  snapshot: () => ResourceSnapshot<T>;
  load: (options?: { force?: boolean; background?: boolean }) => Promise<T | null>;
  abort: () => void;
  tick: (nowMs?: number) => ResourcePhase;
  applyIfCurrent: (seq: number, data: T) => boolean;
  discardIfStale: (seq: number) => boolean;
}

export function createResource<T>(options: ResourceOptions<T>): ResourceController<T> {
  const nowFn = options.now ?? (() => Date.now());
  let phase: ResourcePhase = 'idle';
  let data: T | null = null;
  let error: unknown = null;
  let fetchedAt = 0;
  let seq = 0;
  let inFlight: Promise<T | null> | null = null;
  let abortController: AbortController | null = null;

  function snapshot(): ResourceSnapshot<T> {
    return { phase, data, error, fetchedAt, seq };
  }

  function setPhase(next: ResourcePhase) {
    phase = next;
  }

  function abort() {
    abortController?.abort();
    abortController = null;
    inFlight = null;
  }

  function ageMs(at = nowFn()): number {
    return fetchedAt > 0 ? at - fetchedAt : Number.POSITIVE_INFINITY;
  }

  function isFresh(at = nowFn()): boolean {
    return phase === 'ready' && ageMs(at) < options.freshMs;
  }

  function tick(nowMs = nowFn()): ResourcePhase {
    if (phase === 'ready' && ageMs(nowMs) >= options.freshMs) {
      setPhase('stale');
    }
    return phase;
  }

  function applyIfCurrent(responseSeq: number, value: T): boolean {
    if (responseSeq !== seq) return false;
    data = value;
    error = null;
    fetchedAt = nowFn();
    setPhase('ready');
    return true;
  }

  function discardIfStale(responseSeq: number): boolean {
    return responseSeq !== seq;
  }

  function load(loadOptions?: { force?: boolean; background?: boolean }): Promise<T | null> {
    const background = loadOptions?.background ?? (phase === 'ready' || phase === 'stale');
    if (!loadOptions?.force && isFresh()) return Promise.resolve(data);
    if (inFlight) {
      if (!loadOptions?.force) return inFlight;
      abort();
    }

    const nextSeq = ++seq;
    abortController?.abort();
    const controller = new AbortController();
    abortController = controller;
    setPhase(background ? 'refreshing' : 'loading');

    const pending = options
      .fetch(controller.signal)
      .then((value) => {
        if (discardIfStale(nextSeq)) return data;
        applyIfCurrent(nextSeq, value);
        return value;
      })
      .catch((err) => {
        if (controller.signal.aborted) return data;
        if (discardIfStale(nextSeq)) return data;
        error = err;
        setPhase('error');
        return data;
      })
      .finally(() => {
        if (seq === nextSeq) {
          inFlight = null;
          abortController = null;
        }
      });

    inFlight = pending;
    return pending;
  }

  return {
    snapshot,
    load,
    abort,
    tick,
    applyIfCurrent,
    discardIfStale,
  };
}

/** Recommended freshness windows from the migration plan. */
export const RESOURCE_FRESH_MS = {
  summary: 2000,
  requests: 2000,
  logs: 10000,
  errors: 10000,
  keys: 30000,
  routing: 30000,
  usage: 60000,
  settings: 60000,
} as const;
