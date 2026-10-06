import {
  LIVE_STALE_MS,
  STREAM_BACKOFF_INITIAL_MS,
  STREAM_BACKOFF_MAX_MS,
  type LiveFrame,
  type StreamContext,
} from './types';

export interface ReadLiveStreamHandlers {
  onFrame: (frame: LiveFrame) => void;
  onBytes?: () => void;
  onUnauthorized?: () => void;
  onError?: (error: unknown) => void;
  onClose?: (info: { gotFrame: boolean; hadPriorFrames: boolean }) => void;
}

export interface ReadLiveStreamOptions extends StreamContext {
  url?: string;
  fetchImpl?: typeof fetch;
  now?: () => number;
  handlers: ReadLiveStreamHandlers;
  priorFrameCount?: number;
}

/** Parse one SSE block (`event` + `data` lines) into a live update frame, or null for heartbeats. */
export function parseSseFrame(raw: string): LiveFrame | null {
  let event = 'message';
  const data: string[] = [];
  for (const line of raw.split('\n')) {
    if (!line || line.startsWith(':')) continue;
    if (line.startsWith('event:')) event = line.slice(6).trim();
    else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
  }
  if (event !== 'update' || data.length === 0) return null;
  try {
    return JSON.parse(data.join('\n')) as LiveFrame;
  } catch {
    return null;
  }
}

/** Drop duplicate or out-of-order frames using the monotonic server version field. */
export function shouldApplyFrame(lastAppliedVersion: number | null, frame: LiveFrame | null | undefined): boolean {
  if (!frame || typeof frame !== 'object') return false;
  if (frame.version == null || !Number.isFinite(frame.version)) return true;
  if (lastAppliedVersion == null || !Number.isFinite(lastAppliedVersion)) return true;
  return frame.version > lastAppliedVersion;
}

export function nextStreamBackoffMs(currentDelayMs: number): number {
  return Math.min(currentDelayMs * 2, STREAM_BACKOFF_MAX_MS);
}

/**
 * Read the admin live SSE stream over fetch (header auth). Handles heartbeats, 45s staleness,
 * and abort via the supplied AbortSignal.
 */
export async function readLiveStream(options: ReadLiveStreamOptions): Promise<void> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const now = options.now ?? (() => Date.now());
  const url = options.url ?? `/admin/api/live?limit=${options.limit ?? 25}`;
  let lastDataAt = now();
  let gotFrame = false;
  let retryDelay = STREAM_BACKOFF_INITIAL_MS;
  const priorFrames = options.priorFrameCount ?? 0;

  const staleTimer = setInterval(() => {
    if (options.signal.aborted) return;
    if (now() - lastDataAt >= LIVE_STALE_MS) {
      staleAbort.abort();
    }
  }, 1000);

  const linked = new AbortController();
  options.signal.addEventListener('abort', () => linked.abort(), { once: true });

  const staleAbort = new AbortController();
  staleAbort.signal.addEventListener('abort', () => linked.abort(), { once: true });

  try {
    const res = await fetchImpl(url, {
      headers: {
        ...(options.apiKey ? { 'X-API-Key': options.apiKey } : {}),
        Accept: 'text/event-stream',
      },
      cache: 'no-store',
      signal: linked.signal,
    });

    if (res.status === 401) {
      options.handlers.onUnauthorized?.();
      return;
    }
    if (!res.ok || !res.body) {
      throw new Error('live stream unavailable: ' + res.status);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      lastDataAt = now();
      options.handlers.onBytes?.();
      buffer += decoder.decode(value, { stream: true });
      let sep: number;
      while ((sep = buffer.indexOf('\n\n')) >= 0) {
        const raw = buffer.slice(0, sep);
        buffer = buffer.slice(sep + 2);
        const frame = parseSseFrame(raw);
        if (!frame) continue;
        gotFrame = true;
        retryDelay = STREAM_BACKOFF_INITIAL_MS;
        options.handlers.onFrame(frame);
      }
    }
    throw new Error('live stream ended');
  } catch (error) {
    if (options.signal.aborted || linked.signal.aborted) return;
    options.handlers.onError?.(error);
    options.handlers.onClose?.({ gotFrame, hadPriorFrames: priorFrames > 0 });
    throw Object.assign(error instanceof Error ? error : new Error(String(error)), { retryDelay });
  } finally {
    clearInterval(staleTimer);
  }
}

export { LIVE_STALE_MS, STREAM_BACKOFF_INITIAL_MS, STREAM_BACKOFF_MAX_MS };
