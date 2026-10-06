import { describe, expect, it } from 'vitest';
import { computeSortKey, decorateForSort, sortedList, sortDecorated, undecorate } from './sort';

describe('sort', () => {
  it('computeSortKey_parsesDatesOnce', () => {
    const key = computeSortKey({ createdAt: '2024-01-02T00:00:00Z' }, 'createdAt');
    expect(key).toBe(new Date('2024-01-02T00:00:00Z').getTime());
  });

  it('sortedList_sortsDescendingByNumber', () => {
    const rows = [{ n: 3 }, { n: 1 }, { n: 2 }];
    const sorted = sortedList(rows, { key: 'n', dir: -1 });
    expect(sorted.map((r) => r.n)).toEqual([3, 2, 1]);
  });

  it('decorateSortUndecorate_preservesOriginalItems', () => {
    const a = { id: 'b', label: 'Bravo' };
    const b = { id: 'a', label: 'Alpha' };
    const decorated = decorateForSort([a, b], { key: 'label', dir: 1 });
    const sorted = sortDecorated(decorated, 1);
    expect(undecorate(sorted)).toEqual([b, a]);
    expect(undecorate(sorted)[0]).toBe(b);
  });
});
