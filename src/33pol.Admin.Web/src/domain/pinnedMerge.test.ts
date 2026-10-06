import { describe, expect, it } from 'vitest';
import { mergePinnedIntoFeed } from './pinnedMerge';

type Row = { requestId: string; status: string };

describe('mergePinnedIntoFeed', () => {
  it('retainsPinnedRowEvictedFromLiveFeed', () => {
    const snapshots = new Map<string, Row>();
    const prev: Record<string, Row> = {
      a: { requestId: 'a', status: 'ok' },
      b: { requestId: 'b', status: 'fail' },
    };
    const nextById: Record<string, Row> = {
      c: { requestId: 'c', status: 'ok' },
    };
    const merged = mergePinnedIntoFeed(prev, nextById, new Set(['b']), snapshots);
    expect(merged.b).toEqual({ requestId: 'b', status: 'fail' });
    expect(merged.c).toEqual({ requestId: 'c', status: 'ok' });
    expect(merged.a).toBeUndefined();
    expect(snapshots.get('b')).toEqual({ requestId: 'b', status: 'fail' });
  });

  it('updatesSnapshotWhenPinnedRowStillLive', () => {
    const snapshots = new Map<string, Row>();
    const prev: Record<string, Row> = {};
    const nextById: Record<string, Row> = {
      a: { requestId: 'a', status: 'done' },
    };
    mergePinnedIntoFeed(prev, nextById, new Set(['a']), snapshots);
    expect(snapshots.get('a')).toEqual({ requestId: 'a', status: 'done' });
  });

  it('restoresFromSnapshotWhenNeitherPrevNorLive', () => {
    const snapshots = new Map<string, Row>([['x', { requestId: 'x', status: 'cached' }]]);
    const merged = mergePinnedIntoFeed({}, {}, new Set(['x']), snapshots);
    expect(merged.x).toEqual({ requestId: 'x', status: 'cached' });
  });
});
