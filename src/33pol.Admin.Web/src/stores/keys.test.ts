import { describe, expect, it } from 'vitest';
import { KEYS_RENDER_CAP } from './keys';

describe('keys store constants', () => {
  it('renderCap_isFifty', () => {
    expect(KEYS_RENDER_CAP).toBe(50);
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
