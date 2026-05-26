# FinOps — Quota & Usage (Phase 4–5)

Phase 4 implements **quota gating** on the inference hot path. Phase 5 adds **billing event persistence**, **daily rollups**, and **admin usage APIs**.

## Quota vs rate limit

| Layer | When | Code |
|-------|------|------|
| Rate limit (RPM / concurrency) | Before forward | `rate_limit_exceeded`, `concurrency_limit_exceeded` |
| Quota (monthly tokens) | Before forward (check); after completion (commit) | `quota_exceeded` |

## Implementation (Phase 4)

- **Check:** synchronous `IQuotaService.CheckBeforeForward` in `QuotaMiddleware`
- **Commit:** `IUsageRecorder` queue commits tokens idempotently by `request_id`
- **Storage:** in-memory counters per tenant partition when no database; PostgreSQL `billing_events` + `daily_usage_rollups` when configured

## Usage APIs (Phase 5)

| Endpoint | Purpose |
|----------|---------|
| `GET /admin/api/usage` | Daily rollup report + summary (admin auth) |
| `GET /admin/api/usage/export?format=csv\|json` | Download rollups |

Query params: `from`, `to` (dates), `tenantId` (optional).

## Persistence pipeline

1. `IUsageRecorder` enqueues `UsageEvent` after inference.
2. Quota commit runs in-process (in-memory or future PG-backed quota).
3. When `ConnectionStrings:GatewayDb` is set, `BillingUsagePersistenceHandler` appends `billing_events` (idempotent by `request_id`) and upserts `daily_usage_rollups`.

Prometheus metrics: `gateway_usage_writer_queue_depth`, `gateway_usage_writer_dropped_total`.

Admin UI: `/admin` (static Alpine.js dashboard).

## Soft quota

When usage crosses `SoftLimitRatio` of the monthly limit, responses include header `X-33pol-Quota-Warning`.
