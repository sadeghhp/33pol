import { describe, expect, it, vi } from 'vitest';
import { createPollScheduler, shouldPollSummaryRequests } from './poll';

describe('shouldPollSummaryRequests', () => {
  it('pollsOnMasterTickWhenNotStreaming', () => {
    expect(
      shouldPollSummaryRequests(
        { tick: 1, master: true, health: false, logs: false, errors: false, slowOverview: false, rateLimitActivity: false },
        false,
      ),
    ).toBe(true);
  });

  it('skipsPollWhenStreamingOverview', () => {
    expect(
      shouldPollSummaryRequests(
        { tick: 1, master: true, health: false, logs: false, errors: false, slowOverview: false, rateLimitActivity: false },
        true,
      ),
    ).toBe(false);
  });
});

describe('createPollScheduler', () => {
  it('firesMasterCadenceOnInterval', () => {
    vi.useFakeTimers();
    const onTick = vi.fn();
    const scheduler = createPollScheduler({
      intervalMs: 2000,
      tab: () => 'dashboard',
      onTick,
    });
    scheduler.start();
    vi.advanceTimersByTime(2000);
    expect(onTick).toHaveBeenCalled();
    expect(onTick.mock.calls[0][0].master).toBe(true);
    scheduler.stop();
    vi.useRealTimers();
  });

  it('skipsTicksWhenHidden', () => {
    vi.useFakeTimers();
    const onTick = vi.fn();
    const scheduler = createPollScheduler({
      intervalMs: 1000,
      isHidden: () => true,
      onTick,
    });
    scheduler.start();
    vi.advanceTimersByTime(3000);
    expect(onTick).not.toHaveBeenCalled();
    scheduler.stop();
    vi.useRealTimers();
  });
});
