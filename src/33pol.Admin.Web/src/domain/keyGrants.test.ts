import { describe, expect, it } from 'vitest';
import { buildReplaceGrantsPayload, canGrantKey, toggleGrantSelection } from './keyGrants';

describe('keyGrants', () => {
  it('canGrantKey_rejectsAdminAndRevoked', () => {
    expect(canGrantKey({ role: 'Admin' })).toBe(false);
    expect(canGrantKey({ role: 'Inference', isRevoked: true })).toBe(false);
    expect(canGrantKey({ role: 'Inference', active: true })).toBe(true);
  });

  it('toggleGrantSelection_addsAndRemoves', () => {
    expect(toggleGrantSelection(['a'], 'b')).toEqual(['a', 'b']);
    expect(toggleGrantSelection(['a', 'b'], 'a')).toEqual(['b']);
  });

  it('buildReplaceGrantsPayload_copiesIds', () => {
    expect(buildReplaceGrantsPayload(['m1'])).toEqual({ modelIds: ['m1'] });
  });
});
