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
- **Storage:** in-memory counters per tenant partition when no database; the embedded SQLite `billing_events` + `daily_usage_rollups` tables when configured

## Usage APIs (Phase 5)

| Endpoint | Purpose |
|----------|---------|
| `GET /admin/api/usage` | Daily rollup report + summary (admin auth) |
| `GET /admin/api/usage/export?format=csv\|json` | Download rollups |
| `GET /admin/api/usage/forecast?days=7` | Trailing spend + projected monthly cost |
| `GET /admin/api/usage/events?limit=100` | Paginated billing events (newest first) |

**Forecast formula:** sums `total_cost` from daily rollups over the trailing window (default 7 days, clamped 1–90), divides by window length for a daily average, then multiplies by days in the current UTC calendar month. Response includes `trailingDays`, `trailingTotalCost`, `projectedMonthlyCost`, and `currency` (from `Billing:DefaultCurrency`).

Query params: `from`, `to` (dates), `tenantId` (optional).

## Webhooks

Configure in `appsettings`:

```json
"Billing": {
  "Webhooks": {
    "EndpointUrl": "https://your-receiver.example/hooks/33pol",
    "Secret": "shared-hmac-secret"
  }
}
```

Events include `quota.warning` when period spend crosses a budget's warning threshold, and `usage.daily` once per tenant per UTC day (on rollup update plus a scheduled catch-up at `Billing:DailyWebhookUtcHour` for yesterday). Payloads are signed with `X-33pol-Signature` (HMAC-SHA256 hex of the JSON body). Envelope shape: `{ "type", "timestamp", "data" }`.

## Budget hard stop

Budgets with `HardStopEnabled` block inference when period spend ≥ `AmountLimit` (429 `quota_exceeded`, checked in `QuotaMiddleware` before monthly token quota).

## Usage writer (WP5.2)

- In-memory channel capacity **10,000** events with **`DropOldest`** when saturated (oldest event discarded, newest kept).
- Batched persistence: flush at **100** events or **1 s** (`Billing:UsageWriterBatchSize`, `Billing:UsageWriterFlushIntervalMs`).
- Metrics: `gateway_usage_writer_queue_depth`, `gateway_usage_writer_dropped_total` (see `deploy/prometheus/alerts/33pol-writer.yml`).
- Admin read API: `GET /admin/api/usage/events?tenantId=&from=&to=&limit=` for paginated billing event history.

## Model pricing (rate cards)

Costs are only produced for models that have a price. Set one per model in the admin UI under
**Routing → Models → (edit a model)**, or via the model admin API:

```http
PATCH /admin/api/models/{id}
{
  "model": { "id": "gpt-4o", "url": "https://openrouter.ai/api" },
  "pricing": { "inputPricePerMillionTokens": 3.00, "outputPricePerMillionTokens": 15.00 }
}
```

- Prices are per **one million tokens**, in `Billing:DefaultCurrency` (USD).
- `GET /admin/api/models` returns each model's current `pricing` (null when unpriced).
- Omitting `pricing` leaves the existing price unchanged; `"clearPricing": true` removes it.
- Pricing is stored in the `rate_cards` table, not on the model itself, so it never reaches the
  `models.json` fallback registry. It therefore **requires a configured database** — without one the
  endpoint returns 503.
- Deleting a model also deletes its rate card.

**A model with no rate card records zero cost.** Its billing events persist with null costs and roll
up as `0`, which is indistinguishable from genuinely free usage in the Usage & cost tab. The gateway
logs a warning once per unpriced model to make this visible.

**Caching:** rate cards are cached in-process for `Billing:RateCardCacheTtlSeconds` (default 60),
because budget enforcement prices every request before forwarding it. Admin edits invalidate the
entry immediately in-process; across replicas the TTL bounds staleness. Historical lookups bypass
the cache.

## Persistence pipeline

1. `IUsageRecorder` enqueues `UsageEvent` after inference.
2. Quota commit runs in-process (in-memory or future PG-backed quota).
3. When `ConnectionStrings:GatewayDb` is set, `BillingUsageBatchPersistenceHandler` batches writes (default 100 events or 1s), then `BillingUsagePersistenceHandler` appends `billing_events` (idempotent by `request_id`), applies **rate-card costs** from `rate_cards`, and upserts `daily_usage_rollups`.

**Idempotency:** `billing_events.request_id` has a unique index. `TryAppendAsync` returns `false` on duplicates (including concurrent inserts caught via unique-constraint violation), and rollup aggregation is skipped so retries or duplicate commits never double-count usage.

Prometheus metrics: `gateway_usage_writer_queue_depth`, `gateway_usage_writer_dropped_total`.

**Retention:** `Billing:UsageRetentionDays` (default 90) is the configured TTL for `billing_events` and rollup history. A background purge job is not implemented in v1; operators may run scheduled SQL deletes or rely on table partitioning in production.

Admin UI: `/admin` (static Alpine.js dashboard).

## Soft quota

When usage crosses `SoftLimitRatio` of the monthly limit, responses include header `X-33pol-Quota-Warning`.
