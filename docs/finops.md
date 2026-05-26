# FinOps — Quota Semantics (Phase 4)

Phase 4 implements **quota gating** on the inference hot path. Full rate cards and exports are Phase 5.

## Quota vs rate limit

| Layer | When | Code |
|-------|------|------|
| Rate limit (RPM / concurrency) | Before forward | `rate_limit_exceeded`, `concurrency_limit_exceeded` |
| Quota (monthly tokens) | Before forward (check); after completion (commit) | `quota_exceeded` |

## Implementation (Phase 4)

- **Check:** synchronous `IQuotaService.CheckBeforeForward` in `QuotaMiddleware`
- **Commit:** `IUsageRecorder` queue commits tokens idempotently by `request_id`
- **Storage:** in-memory counters per tenant partition (PostgreSQL tables in Phase 5)

## Soft quota

When usage crosses `SoftLimitRatio` of the monthly limit, responses include header `X-33pol-Quota-Warning`.
