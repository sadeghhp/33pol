import { describe, expect, it, vi } from 'vitest';
import { createConnectionMachine } from './connection';

describe('createConnectionMachine', () => {
  it('applyFrame_rejectsOlderVersion', () => {
    const onApplySummary = vi.fn();
    const machine = createConnectionMachine({
      getApiKey: () => 'test-key',
      getTab: () => 'dashboard',
      onApplySummary,
    });
    machine.applyFrame({ version: 5, summary: { totalErrors: 1 } }, 'stream');
    machine.applyFrame({ version: 4, summary: { totalErrors: 2 } }, 'stream');
    expect(onApplySummary).toHaveBeenCalledTimes(1);
    expect(onApplySummary.mock.calls[0][0]).toEqual({ totalErrors: 1 });
  });

  it('setStatusFail_recordsFailure', () => {
    const machine = createConnectionMachine({
      getApiKey: () => 'test-key',
      getTab: () => 'dashboard',
    });
    machine.sync();
    machine.setStatus('fail');
    expect(machine.snapshot().status).toBe('fail');
  });

  it('syncWithoutApiKey_clearsMode', () => {
    const machine = createConnectionMachine({
      getApiKey: () => '',
      getTab: () => 'dashboard',
    });
    machine.sync();
    expect(machine.snapshot().mode).toBe('off');
  });
});
