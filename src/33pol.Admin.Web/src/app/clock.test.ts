import { describe, expect, it, vi } from 'vitest';
import { createClock } from './clock';

describe('createClock', () => {
  it('notifiesSubscribersOnTick', () => {
    vi.useFakeTimers();
    let now = 5000;
    const clock = createClock({ intervalMs: 1000, now: () => now });
    const seen: number[] = [];
    clock.subscribe((ms) => seen.push(ms));

    now = 6000;
    vi.advanceTimersByTime(1000);
    expect(seen.length).toBeGreaterThanOrEqual(2);
    vi.useRealTimers();
  });

  it('disposeStopsFurtherTicks', () => {
    vi.useFakeTimers();
    const clock = createClock({ intervalMs: 1000 });
    const seen: number[] = [];
    clock.subscribe((ms) => seen.push(ms));
    const len = seen.length;
    clock.dispose();
    vi.advanceTimersByTime(5000);
    expect(seen.length).toBe(len);
    vi.useRealTimers();
  });
});
