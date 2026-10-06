import { createEffect, createMemo, createSignal, onCleanup, onMount } from 'solid-js';
import { createClock } from './clock';

const WALLBOARD_IDLE_MS = 8000;
const WALLBOARD_STALE_MS = 20000;

const sharedClock = createClock();

export interface WallboardOptions {
  active: () => boolean;
  summaryUpdatedAt: () => number;
  connectionFailed: () => boolean;
  hasCriticalAttention: () => boolean;
  onExit?: () => void;
}

export function useWallboard(options: WallboardOptions) {
  const [clockText, setClockText] = createSignal('');
  const [idle, setIdle] = createSignal(false);
  let idleTimer: ReturnType<typeof setTimeout> | null = null;
  let wakeLock: WakeLockSentinel | null = null;
  let weEnteredFullscreen = false;

  const stale = createMemo(() => {
    if (!options.active()) return false;
    const at = options.summaryUpdatedAt();
    if (!at) return false;
    return sharedClock.nowMs() - at >= WALLBOARD_STALE_MS;
  });

  const staleTitle = createMemo(() => (options.connectionFailed() ? 'DISCONNECTED' : 'STALE'));
  const staleText = createMemo(() => {
    if (options.connectionFailed()) return 'Figures stopped updating — check your admin key.';
    const at = options.summaryUpdatedAt();
    if (!at) return 'Waiting for first update…';
    const sec = Math.max(0, Math.floor((sharedClock.nowMs() - at) / 1000));
    return `Last update ${sec}s ago — data may no longer be current.`;
  });

  function applyHtmlClasses() {
    const el = document.documentElement;
    const on = options.active();
    el.classList.toggle('wallboard', on);
    el.classList.toggle('wallboard-idle', on && idle());
    el.classList.toggle('wallboard-stale', on && stale());
    el.classList.toggle('wallboard-critical', on && options.hasCriticalAttention());
  }

  function noteActivity() {
    if (idleTimer) clearTimeout(idleTimer);
    if (idle()) setIdle(false);
    idleTimer = setTimeout(() => {
      idleTimer = null;
      setIdle(true);
    }, WALLBOARD_IDLE_MS);
  }

  async function acquireWakeLock() {
    if (!options.active() || !('wakeLock' in navigator)) return;
    try {
      wakeLock = await navigator.wakeLock.request('screen');
      wakeLock.addEventListener('release', () => {
        wakeLock = null;
      });
    } catch {
      /* denied or unsupported */
    }
  }

  function releaseWakeLock() {
    wakeLock?.release().catch(() => {});
    wakeLock = null;
  }

  onMount(() => {
    const unsubClock = sharedClock.subscribe((now) => {
      setClockText(
        new Date(now).toLocaleTimeString([], {
          hour12: false,
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
        }),
      );
      applyHtmlClasses();
    });

    const onActivity = () => {
      if (options.active()) noteActivity();
    };
    window.addEventListener('mousemove', onActivity, { passive: true });
    window.addEventListener('keydown', onActivity, { passive: true });
    window.addEventListener('touchstart', onActivity, { passive: true });

    const onVisibility = () => {
      if (document.hidden) releaseWakeLock();
      else if (options.active()) void acquireWakeLock();
    };
    document.addEventListener('visibilitychange', onVisibility);

    const onFullscreen = () => {
      if (!document.fullscreenElement && weEnteredFullscreen && options.active()) {
        weEnteredFullscreen = false;
        options.onExit?.();
      }
    };
    document.addEventListener('fullscreenchange', onFullscreen);

    onCleanup(() => {
      unsubClock();
      if (idleTimer) clearTimeout(idleTimer);
      window.removeEventListener('mousemove', onActivity);
      window.removeEventListener('keydown', onActivity);
      window.removeEventListener('touchstart', onActivity);
      document.removeEventListener('visibilitychange', onVisibility);
      document.removeEventListener('fullscreenchange', onFullscreen);
      releaseWakeLock();
      document.documentElement.classList.remove(
        'wallboard',
        'wallboard-idle',
        'wallboard-stale',
        'wallboard-critical',
      );
    });
  });

  createEffect(() => {
    options.active();
    options.summaryUpdatedAt();
    options.connectionFailed();
    options.hasCriticalAttention();
    idle();
    stale();
    applyHtmlClasses();
    if (options.active()) {
      noteActivity();
      void acquireWakeLock();
    } else {
      releaseWakeLock();
      setIdle(false);
      weEnteredFullscreen = false;
    }
  });

  function exitWallboard() {
    if (document.fullscreenElement && document.exitFullscreen) {
      document.exitFullscreen().catch(() => {});
    }
    return { wall: undefined };
  }

  async function enterFullscreen() {
    if (document.fullscreenElement) return;
    try {
      await document.documentElement.requestFullscreen();
      weEnteredFullscreen = true;
    } catch {
      /* user denied */
    }
  }

  return {
    clockText,
    stale,
    staleTitle,
    staleText,
    exitWallboard,
    enterFullscreen,
  };
}
