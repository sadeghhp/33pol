# Phase 4 — Policy, Quotas & Observability++

**Epic:** `EPIC-P4-policy-obs`  
**Duration (guide):** 2–3 weeks  
**Prerequisite:** Phase 3 complete  
**Blocks:** Phase 5  

---

## Objective

Enforce **rate limits and quotas** per tenant/key/model, complete **Prometheus metrics** and **OpenTelemetry** traces, expose **control-plane REST APIs** for operators, and deliver **Observability++** (SLO **metric hooks** for latency/error SLIs — Prometheus **recording rules** and sign-off in Phase 5; structured logs with trace correlation; admin summary; optional SSE event stream).

Billing **metering hooks** are implemented here; FinOps **rate cards and exports** finalize in Phase 5.

See [01-solution-architecture.md](../01-solution-architecture.md) — **Billing domain model**: Plan (P5) defines limits; Quota (P4) enforces; Rate card (P5) prices usage.

---

## Outcomes

- 429 responses with `rate_limit_exceeded`, `quota_exceeded`, `concurrency_limit_exceeded`  
- Full Prometheus catalog + Grafana dashboard JSON  
- OTel traces on inference path  
- Admin APIs: summary, backends, recent requests  
- Request UUID correlation across structured logs (stdout/OTel), metrics, and DB usage rows  
- **85–90%+ unit coverage** on Policy + Observability  

---

## Work packages

### WP4.1 — Rate limiting (`33pol.Policy`)

| Task | Details |
|------|---------|
| ASP.NET Core `AddRateLimiter` | Global + per-policy |
| `IRateLimitPolicyResolver` | From tenant + model + plan |
| Algorithms | Fixed window + token bucket + **concurrency** limiter for streams |
| Redis provider (optional) | Interface `IDistributedRateLimitStore` — in-memory default |
| Response | 429 + `Retry-After` + error JSON |

**Unit tests:**

- Under limit → pass  
- Over RPM → 429 `rate_limit_exceeded`  
- Concurrent streams capped → `concurrency_limit_exceeded`  
- Resolver returns correct policy per tenant tier  

### WP4.2 — Quotas (`33pol.Policy` + Persistence)

| Task | Details |
|------|---------|
| `IQuotaService` | Monthly token/request budgets |
| Soft vs hard | Warn header vs 429 `quota_exceeded` |
| DB tables | `QuotaAllocation`, `QuotaUsage` rolling counters |
| Hook after request | Decrement on usage event (async) |

**Unit tests:**

- Quota exhausted → 429  
- Soft quota → response header `X-33pol-Quota-Warning`  

### WP4.3 — Metrics (`33pol.Observability`)

Implement catalog from executive proposal:

| Metric group | Examples |
|--------------|----------|
| Inference RED | `gateway_inference_requests_total`, `gateway_inference_duration_seconds` |
| Streaming | `gateway_active_streams`, `gateway_time_to_first_token_seconds` |
| Auth | `gateway_auth_attempts_total` |
| Rate limits | `gateway_rate_limit_rejections_total` |
| Backends | `gateway_backend_health` |
| Runtime | ASP.NET / Kestrel (OTel or prometheus-net) |

| Task | Details |
|------|---------|
| `RequestTracker` | IDisposable scope; wire in router |
| `/metrics` | Prometheus scrape |
| `/stats` | JSON snapshot for UI |
| TTFT | Record on first byte when streaming |

**Unit tests:**

- Tracker records success/failure  
- Histogram buckets configured  
- Label cardinality guard (no raw API key label)  

### WP4.4 — OpenTelemetry

| Task | Details |
|------|---------|
| `AddOpenTelemetry()` | Traces + metrics export |
| Instrumentation | AspNetCore, HttpClient (forwarder) |
| Custom spans | `resolve_model`, `rate_limit`, `forward` |
| Propagation | Inject `traceparent` to upstream |
| Serilog enricher | `trace_id`, `span_id` |
| `deploy/otel-collector/config.yaml` | Sample |

**Integration tests:**

- Activity created for inference request  
- Trace context header present on outbound call (mock handler)  

### WP4.5 — Logging++ (enrichers only)

| Task | Details |
|------|---------|
| Serilog enrichers | `trace_id`, `span_id` from OTel (request ID from WP3.3) |
| Structured completion log | tenant, model, duration, status, tokens if known |
| No body logging | Enforced by policy test |
| Audit logs | Wire `IAuditLogger` (P3) to structured admin channel (stdout/OTel); durable retention/export in Phase 5 |

### WP4.6 — Control plane APIs (`33pol.Api`)

Implement `IRecentRequestStore` in `33pol.Observability` (in-memory ring buffer; not PostgreSQL).

| Endpoint | Purpose |
|----------|---------|
| `GET /admin/api/summary` | Metrics snapshot for UI |
| `GET /admin/api/backends` | Health + registry |
| `GET /admin/api/requests?limit=` | Recent in-memory ring buffer (`IRecentRequestStore`) |
| `GET /admin/api/events/stream` | SSE live events (optional; metrics/requests, not log DB tail) |
| OpenAPI | Document all admin routes |

**Integration tests:**

- Admin auth required  
- Summary returns expected shape  

### WP4.7 — Observability artifacts

| Task | Details |
|------|---------|
| `deploy/grafana/dashboards/33pol-gateway.json` | RED + tenants + backends |
| `deploy/prometheus/alerts/33pol.yml` | Error rate, no healthy backends, inference latency (writer queue alerts in Phase 5 WP5.2) |
| `docs/observability.md` | Runbook links |

### WP4.8 — Usage recording (metering hook)

| Task | Details |
|------|---------|
| `IUsageRecorder` | Channel-based queue |
| `UsageEvent` | tenant, key, model, tokens, duration, request_id |
| Parse upstream `usage` | Non-stream + SSE tail |
| `gateway_tokens_total` counter | |
| DB table `usage_events` | Writer batch — full FinOps Phase 5 |

**Unit tests:**

- Parse usage JSON  
- SSE last-chunk parse  
- `usage_parse_failed` metric when missing  

---

## Unit test checklist (Phase 4)

- [ ] Rate limit matrix per policy  
- [ ] Quota hard/soft behavior  
- [ ] Metrics label constraints (static analysis or unit)  
- [ ] Error codes for **P4** 429 variants + `Retry-After`  
- [ ] Usage parser fixtures  
- [ ] Coverage ≥ 85% Observability, ≥ 90% Policy  

---

## Exit criteria

- [ ] Rate limits demonstrably enforced in integration tests  
- [ ] Grafana dashboard JSON valid; `promtool` validates alert rules (full Compose stack verification in Phase 5)  
- [ ] OTel traces visible in collector sample  
- [ ] `/admin/api/summary` authenticated and populated  
- [ ] Prometheus alert rules validate (promtool)  
- [ ] Taiga epic P4 closed  

---

## Taiga story seeds

1. As an operator, I see p99 latency and error rate in Grafana.  
2. As a tenant, I receive 429 with retry guidance when over limit.  
3. As support, I correlate logs and traces with `X-Request-Id`.  
