import { describe, expect, it } from 'vitest';

function buildTenantSavePayload(restricted: boolean, selected: string[]) {
  return restricted ? { modelIds: selected } : { modelIds: [], allowAllModels: true };
}

describe('tenantGrants save payload', () => {
  it('allowAllWhenUnrestricted', () => {
    expect(buildTenantSavePayload(false, ['m1'])).toEqual({ modelIds: [], allowAllModels: true });
  });

  it('sendsSelectedWhenRestricted', () => {
    expect(buildTenantSavePayload(true, ['m1', 'm2'])).toEqual({ modelIds: ['m1', 'm2'] });
  });
});
