export interface FinOpsCardView {
  currency: string;
  todayCost: number;
  mtdCost: number;
  topModels: Array<{ key: string; cost: number; requests: number }>;
  budgets: Array<{ name: string; ratio: number; spent: number; limit: number }>;
  unpricedCount: number;
  summary: string;
}

export interface PolicyCardView {
  grantDenials: Array<{ key: string; count: number }>;
  unknownModels: Array<{ key: string; count: number }>;
  quotaCount: number;
  summary: string;
}

export interface ActivityCardView {
  entries: Array<{ action: string; timestamp: string; detail: string }>;
  available: boolean;
  summary: string;
}

export interface TenantsCardView {
  tenantCount: number;
  keyCount: number;
  revokedKeyCount: number;
  topConsumers: Array<{ slug: string; requests: number; cost: number }>;
  summary: string;
}

export interface RateLimitsGlanceView {
  enforced: boolean;
  refusedLastHour: number;
  refusalShare: number;
  limits: Array<{ label: string; refused: number; utilization: number | null }>;
  hidden: boolean;
  summary: string;
  settingsLink: string;
}

function num(v: unknown, fallback = 0): number {
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}

export function parseFinOps(body: Record<string, unknown> | null): FinOpsCardView | null {
  if (!body) return null;
  const currency = String(body.currency ?? 'USD');
  const todayCost = num(body.todayCost);
  const mtdCost = num(body.monthToDateCost);
  const topModels = Array.isArray(body.topModelsMonthToDate)
    ? body.topModelsMonthToDate.map((row) => {
        const r = row as Record<string, unknown>;
        return { key: String(r.key ?? ''), cost: num(r.cost), requests: num(r.requests) };
      })
    : [];
  const budgets = Array.isArray(body.budgets)
    ? body.budgets.map((row) => {
        const r = row as Record<string, unknown>;
        return {
          name: String(r.name ?? ''),
          ratio: num(r.ratio),
          spent: num(r.spent),
          limit: num(r.limit),
        };
      })
    : [];
  const unpriced = Array.isArray(body.unpricedModelIds) ? body.unpricedModelIds.length : 0;
  return {
    currency,
    todayCost,
    mtdCost,
    topModels,
    budgets,
    unpricedCount: unpriced,
    summary: `${currency} ${mtdCost.toFixed(2)} MTD · ${todayCost.toFixed(2)} today`,
  };
}

export function parsePolicy(body: Record<string, unknown> | null): PolicyCardView | null {
  if (!body) return null;
  const mapRows = (rows: unknown) =>
    Array.isArray(rows)
      ? rows.map((row) => {
          const r = row as Record<string, unknown>;
          return { key: String(r.key ?? r.label ?? ''), count: num(r.count) };
        })
      : [];
  const grantDenials = mapRows(body.grantDenials);
  const unknownModels = mapRows(body.unknownModels);
  const quotas = Array.isArray(body.quotas) ? body.quotas.length : 0;
  const totalDenials = grantDenials.reduce((s, r) => s + r.count, 0);
  return {
    grantDenials,
    unknownModels,
    quotaCount: quotas,
    summary: totalDenials > 0 ? `${totalDenials} grant denials` : 'No policy pressure',
  };
}

export function parseActivity(body: Record<string, unknown> | null): ActivityCardView | null {
  if (!body) return null;
  const available = body.available !== false;
  const entries = Array.isArray(body.entries)
    ? body.entries.map((row) => {
        const r = row as Record<string, unknown>;
        return {
          action: String(r.action ?? ''),
          timestamp: String(r.timestampUtc ?? ''),
          detail: String(r.details ?? r.apiKeyLabel ?? r.tenantSlug ?? ''),
        };
      })
    : [];
  return {
    entries: entries.slice(0, 5),
    available,
    summary: entries.length ? `${entries.length} recent events` : 'No recent activity',
  };
}

export function parseTenants(body: Record<string, unknown> | null): TenantsCardView | null {
  if (!body) return null;
  const topConsumers = Array.isArray(body.topConsumersMonthToDate)
    ? body.topConsumersMonthToDate.map((row) => {
        const r = row as Record<string, unknown>;
        return {
          slug: String(r.tenantSlug ?? r.tenantId ?? '(unknown)'),
          requests: num(r.requests),
          cost: num(r.cost),
        };
      })
    : [];
  return {
    tenantCount: num(body.tenantCount),
    keyCount: num(body.keyCount),
    revokedKeyCount: num(body.revokedKeyCount),
    topConsumers: topConsumers.slice(0, 3),
    summary: `${num(body.tenantCount)} tenants · ${num(body.keyCount)} keys`,
  };
}

export function parseControlPlane(body: Record<string, unknown> | null): { modelCount: number; summary: string } | null {
  if (!body) return null;
  const modelCount = num(body.modelCount);
  const dbOk = (body.database as Record<string, unknown> | undefined)?.connected;
  return {
    modelCount,
    summary: `${modelCount} models · DB ${dbOk === false ? 'issue' : 'ok'}`,
  };
}

export function parseRateLimitsGlance(body: Record<string, unknown> | null): RateLimitsGlanceView | null {
  if (!body) return null;
  if (body.available === false) return { enforced: false, refusedLastHour: 0, refusalShare: 0, limits: [], hidden: true, summary: '', settingsLink: '/settings?sub=ratelimits' };
  const lastHour = (body.lastHour as Record<string, unknown> | undefined) ?? {};
  const refused = num(lastHour.refused);
  const share = num(lastHour.refusalShare);
  const limits = Array.isArray(body.limits)
    ? body.limits.slice(0, 4).map((row) => {
        const r = row as Record<string, unknown>;
        const util = r.peakUtilization ?? r.nearLimit;
        return {
          label: String(r.target ?? r.limitId ?? r.scope ?? ''),
          refused: num(r.refused),
          utilization: util != null ? num(util) : null,
        };
      })
    : [];
  return {
    enforced: !!body.enforced,
    refusedLastHour: refused,
    refusalShare: share,
    limits,
    hidden: false,
    summary: refused > 0 ? `${refused} refused last hour` : 'No refusals last hour',
    settingsLink: '/settings?sub=ratelimits',
  };
}
