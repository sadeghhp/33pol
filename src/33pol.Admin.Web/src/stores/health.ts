import { createSignal, onCleanup } from 'solid-js';

const POLL_MS = 10000;

const [healthLive, setHealthLive] = createSignal<boolean | null>(null);
const [healthReady, setHealthReady] = createSignal<boolean | null>(null);

let pollTimer: ReturnType<typeof setInterval> | null = null;
let subscribers = 0;

async function pollHealth(): Promise<void> {
  try {
    const [live, ready] = await Promise.all([
      fetch('/health/live').then((r) => r.ok),
      fetch('/health/ready').then((r) => r.ok),
    ]);
    setHealthLive(live);
    setHealthReady(ready);
  } catch {
    setHealthLive(false);
    setHealthReady(false);
  }
}

export function useHealthSignals() {
  return { healthLive, healthReady };
}

export function subscribeHealthPolling(): () => void {
  subscribers++;
  if (subscribers === 1) {
    void pollHealth();
    pollTimer = setInterval(() => void pollHealth(), POLL_MS);
  }
  return () => {
    subscribers = Math.max(0, subscribers - 1);
    if (subscribers === 0 && pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  };
}

export function useHealthPolling(): void {
  onCleanup(subscribeHealthPolling());
}
