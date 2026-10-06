import { describe, expect, it } from 'vitest';
import { parseSseFrame, shouldApplyFrame } from './sse';

describe('sse', () => {
  it('parseSseFrame_parsesUpdateEvent', () => {
    const raw = 'event: update\ndata: {"version":2,"summary":{"totalErrors":1}}\n';
    const frame = parseSseFrame(raw);
    expect(frame).toEqual({ version: 2, summary: { totalErrors: 1 } });
  });

  it('parseSseFrame_ignoresHeartbeatComments', () => {
    const raw = ': heartbeat\n';
    expect(parseSseFrame(raw)).toBeNull();
  });

  it('parseSseFrame_joinsMultipleDataLines', () => {
    const raw = 'event: update\ndata: {"version":1,\ndata: "requests":[]}\n';
    expect(parseSseFrame(raw)).toEqual({ version: 1, requests: [] });
  });

  it('shouldApplyFrame_rejectsOlderOrEqualVersion', () => {
    expect(shouldApplyFrame(5, { version: 4 })).toBe(false);
    expect(shouldApplyFrame(5, { version: 5 })).toBe(false);
    expect(shouldApplyFrame(5, { version: 6 })).toBe(true);
  });

  it('shouldApplyFrame_acceptsMissingVersion', () => {
    expect(shouldApplyFrame(5, { summary: {} })).toBe(true);
    expect(shouldApplyFrame(null, { version: 1 })).toBe(true);
  });
});
