import { describe, expect, it } from 'vitest';
import { attentionKey, attentionLinkPath, attentionRows, hasCriticalAttention } from './attention';

describe('attention', () => {
  it('attentionKey_isStable', () => {
    expect(attentionKey({ id: 'x', title: 't' })).toBe('x');
    expect(attentionKey({ code: 'disk_full', modelId: 'm1', tenantId: 't1' })).toBe('disk_full|m1|t1');
  });

  it('attentionLinkPath_buildsRouteWithParams', () => {
    expect(
      attentionLinkPath({ tab: 'errors', params: { code: 'model_not_found', range: '1h' } }),
    ).toBe('/errors?code=model_not_found&range=1h');
  });

  it('attentionRows_filtersDismissedUnlessWallboard', () => {
    const items = [{ id: 'a', severity: 'info', title: 'One' }, { id: 'b', severity: 'critical', title: 'Two' }];
    const rows = attentionRows(items, ['a'], false);
    expect(rows).toHaveLength(1);
    expect(rows[0].key).toBe('b');
    expect(attentionRows(items, ['a'], true)).toHaveLength(2);
  });

  it('attentionRows_exposesLinkTargets', () => {
    const rows = attentionRows(
      [{ code: 'quota', severity: 'warning', title: 'Quota', link: { tab: 'usage', params: { costCenter: 'eng' } } }],
      [],
      false,
    );
    expect(rows[0].hasLink).toBe(true);
    expect(rows[0].linkPath).toBe('/usage?costCenter=eng');
  });

  it('hasCriticalAttention_detectsCritical', () => {
    expect(hasCriticalAttention([{ severity: 'info' }, { severity: 'critical' }])).toBe(true);
    expect(hasCriticalAttention([{ severity: 'warn' }])).toBe(false);
  });
});
