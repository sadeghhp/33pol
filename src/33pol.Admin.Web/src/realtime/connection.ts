import { readLiveStream, shouldApplyFrame } from './sse';
import { createPollScheduler, shouldPollSummaryRequests, type PollCadenceFlags } from './poll';
import {
  STREAM_BACKOFF_INITIAL_MS,
  type ConnectionMode,
  type ConnectionSnapshot,
  type ConnectionSource,
  type ConnectionStatus,
  type LiveFrame,
} from './types';

export interface ConnectionMachineOptions {
  getApiKey: () => string;
  getTab: () => string;
  isHidden?: () => boolean;
  fetchImpl?: typeof fetch;
  setTimeoutImpl?: typeof setTimeout;
  clearTimeoutImpl?: typeof clearTimeout;
  onSnapshot?: (snapshot: ConnectionSnapshot) => void;
  onApplySummary?: (summary: Record<string, unknown>, source: ConnectionSource) => void;
  onApplyRequests?: (requests: NonNullable<LiveFrame['requests']>, source: ConnectionSource) => void;
  onPollCadence?: (cadence: PollCadenceFlags) => void;
  pollOptions?: Omit<Parameters<typeof createPollScheduler>[0], 'onTick'>;
}

export interface ConnectionMachine {
  snapshot: () => ConnectionSnapshot;
  sync: () => void;
  stop: () => void;
  setStatus: (status: ConnectionStatus, degraded?: boolean) => void;
  applyFrame: (frame: LiveFrame, source?: ConnectionSource) => boolean;
}

function defaultSnapshot(): ConnectionSnapshot {
  return {
    status: '',
    mode: '',
    source: null,
    lastFrameAt: 0,
    lastVersion: null,
    retryDelayMs: STREAM_BACKOFF_INITIAL_MS,
    degraded: false,
  };
}

/**
 * Connection FSM: 401 ⇒ fail (halts poll + stream); only the active source may write summary/requests.
 */
export function createConnectionMachine(options: ConnectionMachineOptions): ConnectionMachine {
  const setTimeoutImpl = options.setTimeoutImpl ?? setTimeout;
  const clearTimeoutImpl = options.clearTimeoutImpl ?? clearTimeout;

  let snapshot = defaultSnapshot();
  let streamAbort: AbortController | null = null;
  let retryTimer: ReturnType<typeof setTimeout> | null = null;
  let liveFrameCount = 0;
  let writer: ConnectionSource | null = null;

  function emit() {
    options.onSnapshot?.({ ...snapshot });
  }

  function setMode(mode: ConnectionMode) {
    snapshot = { ...snapshot, mode };
    emit();
  }

  function setSource(source: ConnectionSource | null) {
    writer = source;
    snapshot = { ...snapshot, source };
    emit();
  }

  function stopStream() {
    if (retryTimer) {
      clearTimeoutImpl(retryTimer);
      retryTimer = null;
    }
    if (streamAbort) {
      streamAbort.abort();
      streamAbort = null;
    }
    snapshot = { ...snapshot, retryDelayMs: STREAM_BACKOFF_INITIAL_MS };
    if (snapshot.mode === 'stream' || snapshot.mode === 'reconnecting' || snapshot.mode === 'connecting') {
      setMode('');
    }
    setSource(null);
    emit();
  }

  function applyFrame(frame: LiveFrame, source: ConnectionSource = 'stream'): boolean {
    if (!shouldApplyFrame(snapshot.lastVersion, frame)) return false;
    if (writer && writer !== source) return false;

    if (frame.version != null && Number.isFinite(frame.version)) {
      snapshot = { ...snapshot, lastVersion: frame.version, lastFrameAt: Date.now() };
    } else {
      snapshot = { ...snapshot, lastFrameAt: Date.now() };
    }

    if (frame.summary) options.onApplySummary?.(frame.summary, source);
    if (Array.isArray(frame.requests)) options.onApplyRequests?.(frame.requests, source);
    emit();
    return true;
  }

  async function openStream() {
    const apiKey = options.getApiKey();
    if (!apiKey || snapshot.status === 'fail') return;
    if (streamAbort || retryTimer) return;

    const controller = new AbortController();
    streamAbort = controller;
    setMode(snapshot.mode || 'reconnecting');

    try {
      await readLiveStream({
        apiKey,
        signal: controller.signal,
        fetchImpl: options.fetchImpl,
        priorFrameCount: liveFrameCount,
        handlers: {
          onUnauthorized: () => {
            snapshot = { ...snapshot, status: 'fail', degraded: true };
            stopStream();
            setMode('off');
            emit();
          },
          onFrame: (frame) => {
            liveFrameCount++;
            snapshot = { ...snapshot, retryDelayMs: STREAM_BACKOFF_INITIAL_MS };
            setSource('stream');
            setMode('stream');
            applyFrame(frame, 'stream');
          },
          onError: () => {
            /* handled in onClose */
          },
          onClose: ({ gotFrame, hadPriorFrames }) => {
            if (controller.signal.aborted) return;
            streamAbort = null;
            const nextMode: ConnectionMode = gotFrame || hadPriorFrames ? 'reconnecting' : 'polling';
            setMode(nextMode);
            if (writer === 'stream') setSource(null);
            const delay = snapshot.retryDelayMs;
            snapshot = { ...snapshot, retryDelayMs: Math.min(delay * 2, 15000) };
            retryTimer = setTimeoutImpl(() => {
              retryTimer = null;
              machine.sync();
            }, delay);
            emit();
          },
        },
      });
    } catch {
      /* onClose schedules reconnect */
    }
  }

  function wantsStream(): boolean {
    return (
      !!options.getApiKey() &&
      options.getTab() === 'dashboard' &&
      !(options.isHidden?.() ?? false) &&
      snapshot.status !== 'fail'
    );
  }

  const poll = createPollScheduler({
    ...options.pollOptions,
    isConnectionFailed: () => snapshot.status === 'fail',
    isStreamingOverview: () => snapshot.mode === 'stream' && options.getTab() === 'dashboard',
    tab: options.getTab,
    onTick: (cadence) => {
      options.onPollCadence?.(cadence);
      if (snapshot.status === 'fail') return;
      const streaming = snapshot.mode === 'stream' && options.getTab() === 'dashboard';
      if (shouldPollSummaryRequests(cadence, streaming)) {
        setSource('poll');
      }
    },
  });

  const machine: ConnectionMachine = {
    snapshot: () => ({ ...snapshot }),
    applyFrame,
    setStatus(status, degraded = snapshot.degraded) {
      snapshot = { ...snapshot, status, degraded };
      if (status === 'fail') {
        stopStream();
        poll.stop();
      }
      emit();
    },
    sync() {
      if (!options.getApiKey()) {
        stopStream();
        poll.stop();
        setMode('off');
        emit();
        return;
      }
      if (snapshot.status === 'fail') {
        stopStream();
        return;
      }
      poll.start();
      if (!wantsStream()) {
        stopStream();
        if (!snapshot.mode) setMode('polling');
        emit();
        return;
      }
      void openStream();
    },
    stop() {
      stopStream();
      poll.stop();
      setMode('off');
      emit();
    },
  };

  return machine;
}
