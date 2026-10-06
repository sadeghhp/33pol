import { describe, expect, it } from 'vitest';
import { KEYS_RENDER_CAP, normalizeKeysListResponse } from './keys';

describe('keys store constants', () => {
  it('renderCap_isFifty', () => {
    expect(KEYS_RENDER_CAP).toBe(50);
  });
});

describe('normalizeKeysListResponse', () => {
  it('acceptsBareArrayFromGateway', () => {
    const rows = [{ id: '1', label: 'a' }];
    expect(normalizeKeysListResponse(rows)).toEqual(rows);
  });

  it('acceptsItemsWrapper', () => {
    const rows = [{ id: '2', label: 'b' }];
    expect(normalizeKeysListResponse({ items: rows })).toEqual(rows);
  });

  it('returnsEmptyForUnexpectedShape', () => {
    expect(normalizeKeysListResponse(null)).toEqual([]);
    expect(normalizeKeysListResponse({})).toEqual([]);
  });
});

describe('keys render cap slice', () => {
  it('capsFilteredListAtFifty', () => {
    const filtered = Array.from({ length: 120 }, (_, i) => ({ id: String(i) }));
    const capped = filtered.slice(0, KEYS_RENDER_CAP);
    expect(capped).toHaveLength(50);
    expect(filtered.length).toBe(120);
  });
});
