export type ConnectionStatus = 'unknown' | 'ok' | 'degraded' | 'fail' | '';

export type ConnectionMode = 'off' | 'polling' | 'stream' | 'reconnecting' | 'connecting' | '';

export type ConnectionSource = 'poll' | 'stream';

export interface LiveFrame {
  version?: number;
  summary?: Record<string, unknown>;
  requests?: LiveRequestRow[];
}

export interface LiveRequestRow {
  requestId?: string;
  [key: string]: unknown;
}

export interface ConnectionSnapshot {
  status: ConnectionStatus;
  mode: ConnectionMode;
  source: ConnectionSource | null;
  lastFrameAt: number;
  lastVersion: number | null;
  retryDelayMs: number;
  degraded: boolean;
}

export interface StreamContext {
  apiKey: string;
  limit?: number;
  signal: AbortSignal;
}

export interface ParsedSseFrame {
  event: string;
  data: string;
}

/** 3× the server's 15s idle heartbeat interval. */
export const LIVE_STALE_MS = 3 * 15000;

export const STREAM_BACKOFF_INITIAL_MS = 1000;
export const STREAM_BACKOFF_MAX_MS = 15000;
