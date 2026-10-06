import { describe, expect, it } from 'vitest';
import { reconcileById, reconcileRequests, reconcileSummary } from './reconcile';

describe('reconcile', () => {
  it('reconcileSummary_reportsChangedPaths', () => {
    const prev = { totalErrors: 1, window: '5m' };
    const next = { totalErrors: 2, window: '5m' };
    const result = reconcileSummary(prev, next);
    expect(result.changedPaths).toContain('totalErrors');
    expect(result.changedPaths).not.toContain('window');
    expect(result.value?.totalErrors).toBe(2);
  });

  it('reconcileSummary_keepsReferenceWhenUnchanged', () => {
    const prev = { totalErrors: 1 };
    const next = { totalErrors: 1 };
    const result = reconcileSummary(prev, next);
    expect(result.changedPaths).toHaveLength(0);
    expect(result.value).toBe(prev);
  });

  it('reconcileRequests_preservesUnchangedRowReferences', () => {
    const rowA = { requestId: 'a', statusCode: 200 };
    const rowB = { requestId: 'b', statusCode: 500 };
    const first = reconcileRequests({}, [rowA, rowB]);
    const second = reconcileRequests(first.byId, [rowA, rowB]);
    expect(second.changedIds).toEqual([]);
    expect(second.byId.b).toBe(rowB);
  });

  it('reconcileById_removesEvictedRows', () => {
    const prev = reconcileById({}, [{ id: 'a' }, { id: 'b' }], 'id');
    const next = reconcileById(prev.byId, [{ id: 'a' }], 'id');
    expect(next.order).toEqual(['a']);
    expect(next.removedIds).toEqual(['b']);
    expect(next.byId.b).toBeUndefined();
  });
});
