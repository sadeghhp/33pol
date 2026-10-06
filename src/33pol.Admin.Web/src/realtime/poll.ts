export const POLL_INTERVAL_MS = 2000;

export interface PollCadenceFlags {
  tick: number;
  master: boolean;
  health: boolean;
  logs: boolean;
  errors: boolean;
  slowOverview: boolean;
  rateLimitActivity: boolean;
}

export interface PollSchedulerOptions {
  intervalMs?: number;
  isHidden?: () => boolean;
  isConnectionFailed?: () => boolean;
  isStreamingOverview?: () => boolean;
  tab?: () => string;
  logsAutoRefresh?: () => boolean;
  errorsAutoRefresh?: () => boolean;
  rateLimitActivityPollDue?: (tick: number) => boolean;
  onTick: (cadence: PollCadenceFlags) => void;
  setIntervalImpl?: typeof setInterval;
  clearIntervalImpl?: typeof clearInterval;
}

export interface PollScheduler {
  start: () => void;
  stop: () => void;
  tick: number;
}

/** 2s master tick with ×5 (health/logs/errors) and ×15 (slow Overview cards) sub-cadences. */
export function createPollScheduler(options: PollSchedulerOptions): PollScheduler {
  const intervalMs = options.intervalMs ?? POLL_INTERVAL_MS;
  const setIntervalImpl = options.setIntervalImpl ?? setInterval;
  const clearIntervalImpl = options.clearIntervalImpl ?? clearInterval;
  let timer: ReturnType<typeof setInterval> | null = null;
  let tick = 0;

  function fire() {
    if (options.isHidden?.()) return;
    if (options.isConnectionFailed?.()) {
      options.onTick({
        tick,
        master: false,
        health: false,
        logs: false,
        errors: false,
        slowOverview: false,
        rateLimitActivity: false,
      });
      return;
    }

    const tab = options.tab?.() ?? '';
    const streaming = options.isStreamingOverview?.() ?? false;
    const cadence: PollCadenceFlags = {
      tick,
      master: true,
      health: tick % 5 === 0,
      logs: !!(options.logsAutoRefresh?.() && tab === 'logs' && tick % 5 === 0),
      errors: !!(options.errorsAutoRefresh?.() && tab === 'errors' && tick % 5 === 0),
      slowOverview: tab === 'dashboard' && tick > 0 && tick % 15 === 0,
      rateLimitActivity: options.rateLimitActivityPollDue?.(tick) ?? false,
    };

    options.onTick(cadence);

    if (!streaming && tab === 'dashboard') {
      /* summary + requests handled by consumer based on cadence.master */
    }

    tick++;
  }

  return {
    get tick() {
      return tick;
    },
    start() {
      if (timer) return;
      tick = 0;
      timer = setIntervalImpl(fire, intervalMs);
    },
    stop() {
      if (timer) {
        clearIntervalImpl(timer);
        timer = null;
      }
    },
  };
}

export function shouldPollSummaryRequests(cadence: PollCadenceFlags, streamingOverview: boolean): boolean {
  return cadence.master && !streamingOverview;
}
