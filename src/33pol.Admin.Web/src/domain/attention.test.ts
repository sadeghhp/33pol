import { describe, expect, it } from 'vitest';
import { attentionKey, attentionRows, hasCriticalAttention } from './attention';

describe('attention', () => {
  it('attentionKey_isStable', () => {
    expect(attentionKey({ id: 'x', title: 't' })).toBe('x');
    expect(attentionKey({ severity: 'warn', title: 'Disk', message: 'full' })).toContain('warn');
  });

  it('attentionRows_filtersDismissedUnlessWallboard', () => {
    const items = [{ id: 'a', severity: 'info', title: 'One' }, { id: 'b', severity: 'critical', title: 'Two' }];
    const rows = attentionRows(items, ['a'], false);
    expect(rows).toHaveLength(1);
    expect(rows[0].key).toBe('b');
    expect(attentionRows(items, ['a'], true)).toHaveLength(2);
  });

  it('hasCriticalAttention_detectsCritical', () => {
    expect(hasCriticalAttention([{ severity: 'info' }, { severity: 'critical' }])).toBe(true);
    expect(hasCriticalAttention([{ severity: 'warn' }])).toBe(false);
  });
});
