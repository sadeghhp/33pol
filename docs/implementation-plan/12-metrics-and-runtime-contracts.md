# Metrics Catalog & Runtime Contracts

**Phase 4:** Implement metrics and hooks; **Phase 5:** recording rules, alert wiring, GA dashboards  
**Exporter:** Pick **one** — OpenTelemetry Prometheus exporter **or** prometheus-net ([01-solution-architecture.md](./01-solution-architecture.md))

---

## 1. Metric naming — v1 → v2

| v1 (prometheus-net) | v2 (canonical) | Notes |
|---------------------|----------------|-------|
| `llm_gateway_requests_total` | `gateway_inference_requests_total` | **BREAKING** — new dashboards |
| `llm_gateway_active_streams` | `gateway_active_streams` | |
| `llm_gateway_request_duration_seconds` | `gateway_inference_duration_seconds` | |
| `llm_gateway_errors_total` | `gateway_inference_errors_total` | v2 splits by `code` label where possible |

**Migration:** Do **not** dual-publish v1 names at GA unless operator requests; document rename in `docs/observability.md` (Phase 5).

**Label rules (MUST):**

- Allowed: `model` (canonical id), `status`, `code`, `tenant_slug` (low cardinality tiers ok — prefer slug over uuid in metrics).
- **Forbidden:** raw API key, full request id as label.

---

## 2. v2 metric index (implement in Phase 4 unless noted)

### Inference RED

| Metric | Type | Labels | Phase |
|--------|------|--------|-------|
| `gateway_inference_requests_total` | Counter | `model`, `status` | 4 |
| `gateway_inference_duration_seconds` | Histogram | `model` | 4 |
| `gateway_inference_errors_total` | Counter | `model`, `code` | 4 |
| `gateway_active_streams` | Gauge | `model` | 4 |
| `gateway_time_to_first_token_seconds` | Histogram | `model` | 4 |

### Auth & policy

| Metric | Type | Labels | Phase |
|--------|------|--------|-------|
| `gateway_auth_attempts_total` | Counter | `result` | 4 |
| `gateway_rate_limit_rejections_total` | Counter | `reason` | 4 |
| `gateway_quota_rejections_total` | Counter | — | 4 |

### Backends & registry

| Metric | Type | Labels | Phase |
|--------|------|--------|-------|
| `gateway_backend_health` | Gauge | `model` | 4 |
| `gateway_config_reload_total` | Counter | `result` | 4 |

### Usage pipeline

| Metric | Type | Labels | Phase |
|--------|------|--------|-------|
| `gateway_tokens_total` | Counter | `model`, `direction` (`input`, `output`, `total`) | 4 |
| `gateway_usage_parse_failures_total` | Counter | `model` | 4 |
| `gateway_usage_writer_queue_depth` | Gauge | — | 5 |
| `gateway_usage_writer_dropped_total` | Counter | — | 5 |

### Runtime (framework)

- ASP.NET Core / Kestrel standard metrics via OTel or prometheus-net — **do not** duplicate custom RED if redundant.

---

## 3. Hooks (where to record)

| Event | Component | Phase |
|-------|-----------|-------|
| Request start/end, stream gauge | `IRequestTracker` in router `try/finally` | 4 |
| Recent request row | `IRecentRequestStore.Add` in router `finally` | 4 |
| TTFT first byte | Streaming transformer or forwarder callback | 4 |
| Usage tokens | After response complete → `IUsageRecorder` | 4 |
| Auth result | Authentication handler | 3–4 |

---

## 4. Quota: reserve vs commit (Phase 4)

Avoid race between pre-forward check and post-hoc usage decrement.

### Recommended semantics

| Step | Action |
|------|--------|
| Before forward | **Check** quota (read `QuotaUsage` vs `QuotaAllocation`) |
| On successful completion | **Commit** estimated or actual tokens (from upstream `usage` or heuristic) |
| On hard quota | 429 `quota_exceeded` **before** forward |
| Soft quota | Response header `X-33pol-Quota-Warning`; allow request |

**Streaming:** Commit on **final** usage (SSE tail chunk) or on connection end; partial failure **SHOULD NOT** double-commit (idempotent commit keyed by `request_id`).

**Async writer:** `IUsageRecorder` queue is **async**; quota commit for hard limits **MUST** be synchronous on the hot path or use DB reservation:

- **Option A (GA default):** Synchronous quota check + commit in same transaction as usage row insert.
- **Option B:** Reserve tokens before forward, adjust on completion (more accurate, more complex).

WP4.2 **MUST** document chosen option in code and `docs/finops.md`.

**Interaction with rate limits:** Rate limit (RPM) → Quota (monthly) → Bulkhead/circuit (resilience) — reject in that order on the hot path.

---

## 5. SSE admin stream vs v1 SignalR (Phase 4 optional)

v1 hub: `/hubs/admin` — see [03-api §9](../old-version/03-api-operations-and-observability.md).

### v2 endpoint

`GET /admin/api/events/stream` — `text/event-stream`, **admin auth required**.

### Event mapping

| v1 SignalR method | v2 SSE `event` name | Payload (JSON) |
|-------------------|---------------------|----------------|
| `ReceiveMetrics` | `metrics` | Subset of `/admin/api/summary` |
| `ReceiveHealthBatch` | `health` | Backend health list |
| `ReceiveRequest` | `request` | Same fields as `RealTimeRequest` (§ below) |
| `ReceiveLog` | — | **Not streamed** — use platform logs (OTel/Loki) |
| `ReceiveInitialState` | `snapshot` | Summary + backends + recent requests |

### `request` event fields (parity with v1 `RealTimeRequest`)

| Field | Type |
|-------|------|
| `id` | string (short id or `X-Request-Id` prefix) |
| `model` | string (canonical) |
| `endpoint` | string |
| `isStreaming` | bool |
| `durationMs` | number |
| `statusCode` | int |
| `success` | bool |
| `errorType` | string? |
| `timestamp` | ISO-8601 UTC |

**Polling fallback:** Admin UI Phase 5 polls `GET /admin/api/summary` every 2s if SSE unavailable.

---

## 6. Alerting scope (GA)

| Deliverable | Phase | Notes |
|-------------|-------|-------|
| `deploy/prometheus/alerts/*.yml` | 4–5 | `promtool check rules` **MUST** pass in CI |
| Alertmanager in Compose | 5 optional | GA **MAY** be rules-only; firing tests use `promtool` + manual eval |
| Runbooks | 5 | Link from alert annotations |

---

## 7. SLO hooks vs recording rules

| Item | Phase |
|------|-------|
| Histograms/counters for SLIs | 4 |
| Prometheus recording rules + SLO dashboards | 5 |
| Exemplars | Post-GA unless explicitly scheduled |

---

## Related documents

- [03-performance-and-load-testing.md](./03-performance-and-load-testing.md) — load test SLIs
- [09-v1-parity-spec.md](./09-v1-parity-spec.md) — stream gauge in router `finally`
- [11-ha-and-scaling.md](./11-ha-and-scaling.md) — per-replica metrics aggregation
