export interface ModelGrantsResponse {
  modelIds: string[];
  usesDefaultAccess?: boolean;
}

export interface ReplaceModelGrantsRequest {
  modelIds: string[];
}

export function canGrantKey(row: Record<string, unknown>): boolean {
  if (String(row.role ?? '') === 'Admin') return false;
  if (row.isRevoked || row.isArchived) return false;
  return true;
}

export function buildReplaceGrantsPayload(selected: string[]): ReplaceModelGrantsRequest {
  return { modelIds: [...selected] };
}

export function toggleGrantSelection(selected: string[], modelId: string): string[] {
  const set = new Set(selected);
  if (set.has(modelId)) set.delete(modelId);
  else set.add(modelId);
  return [...set];
}
