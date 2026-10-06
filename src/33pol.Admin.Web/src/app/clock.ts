export interface Clock {
  nowMs: () => number;
  subscribe: (listener: (nowMs: number) => void) => () => void;
  dispose: () => void;
}

export interface ClockOptions {
  intervalMs?: number;
  setIntervalImpl?: typeof setInterval;
  clearIntervalImpl?: typeof clearInterval;
  now?: () => number;
}

/** 1s tick shared by time-dependent UI; starts only while at least one subscriber exists. */
export function createClock(options: ClockOptions = {}): Clock {
  const intervalMs = options.intervalMs ?? 1000;
  const setIntervalImpl = options.setIntervalImpl ?? setInterval;
  const clearIntervalImpl = options.clearIntervalImpl ?? clearInterval;
  const nowFn = options.now ?? (() => Date.now());

  let current = nowFn();
  let timer: ReturnType<typeof setInterval> | null = null;
  let refcount = 0;
  const listeners = new Set<(nowMs: number) => void>();

  function notify() {
    current = nowFn();
    for (const listener of listeners) listener(current);
  }

  function start() {
    if (timer) return;
    notify();
    timer = setIntervalImpl(notify, intervalMs);
  }

  function stop() {
    if (timer) {
      clearIntervalImpl(timer);
      timer = null;
    }
  }

  return {
    nowMs: () => current,
    subscribe(listener) {
      listeners.add(listener);
      refcount++;
      if (refcount === 1) start();
      listener(current);
      return () => {
        listeners.delete(listener);
        refcount = Math.max(0, refcount - 1);
        if (refcount === 0) stop();
      };
    },
    dispose() {
      listeners.clear();
      refcount = 0;
      stop();
    },
  };
}
