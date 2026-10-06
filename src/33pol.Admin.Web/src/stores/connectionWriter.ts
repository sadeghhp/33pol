import type { ConnectionSnapshot } from '../realtime/types';

let currentSnapshot: ConnectionSnapshot = {
  status: '',
  mode: '',
  source: null,
  lastFrameAt: 0,
  lastVersion: null,
  retryDelayMs: 1000,
  degraded: false,
};

export function setConnectionSnapshot(s: ConnectionSnapshot): void {
  currentSnapshot = s;
}

export function snapshot(): ConnectionSnapshot {
  return currentSnapshot;
}
