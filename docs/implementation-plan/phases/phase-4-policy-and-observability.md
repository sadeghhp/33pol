# Phase 4 — Policy, Quotas & Observability++

**Epic:** `EPIC-P4-policy-obs`  
**Duration (guide):** 2–3 weeks  
**Prerequisite:** Phase 3 complete  
**Blocks:** Phase 5  

---

## Objective

Enforce **rate limits and quotas** per tenant/key/model, complete **Prometheus metrics** and **OpenTelemetry** traces, expose **control-plane REST APIs** for operators, deliver **Observability++** (SLO **metric hooks** for latency/error SLIs — Prometheus **recording rules** and sign-off in Phase 5; structured logs with trace correlation; admin summary; optional SSE event stream), and ship an **optional in-process operator console** (Spectre.Console) that shares control-plane logic with HTTP admin.

Billing **metering hooks** are implemented here; FinOps **rate cards and exports** finalize in Phase 5.

See [01-solution-architecture.md](../01-solution-architecture.md) — **Billing domain model**: Plan (P5) defines limits; Quota (P4) enforces; Rate card (P5) prices usage.

---

## Outcomes

- 429 responses with `rate_limit_exceeded`, `quota_exceeded`, `concurrency_limit_exceeded`  
- Full Prometheus catalog + Grafana dashboard JSON  
- OTel traces on inference path  
- Admin APIs: summary, backends, recent requests  
- Shared `IControlPlaneCommands` for HTTP admin and operator console  
- Optional operator console (WP4.9): Spectre TUI, config-gated, off in Production/Docker/CI defaults  
- Request UUID correlation across structured logs (stdout/OTel), metrics, and DB usage rows  
- **85–90%+ unit coverage** on Policy + Observability  

---

## Work packages

### WP4.1 — Rate limiting (`33pol.Policy`)

**Policy source (pre-`Plan` entity):** [10-identity-data-model.md](../10-identity-data-model.md) § Rate limit source. **Multi-replica:** [11-ha-and-scaling.md](../11-ha-and-scaling.md).

| Task | Details |
|------|---------|
| ASP.NET Core `AddRateLimiter` | Global + per-policy |
| `IRateLimitPolicyResolver` | `Tenant.PlanSlug` → `RateLimiting:Plans` + defaults |
| Algorithms | Fixed window + token bucket + **concurrency** limiter for streams |
| Redis provider (optional) | Interface `IDistributedRateLimitStore` — in-memory default |
| Response | 429 + `Retry-After` + error JSON |

**Unit tests:**

- Under limit → pass  
- Over RPM → 429 `rate_limit_exceeded`  
- Concurrent streams capped → `concurrency_limit_exceeded`  
- Resolver returns correct policy per tenant tier  

### WP4.2 — Quotas (`33pol.Policy` + Persistence)

**Semantics:** [12-metrics-and-runtime-contracts.md](../12-metrics-and-runtime-contracts.md) § Quota (check before forward; commit on completion; idempotent by `request_id`).

| Task | Details |
|------|---------|
| `IQuotaService` | Monthly token/request budgets |
| Soft vs hard | Warn header vs 429 `quota_exceeded` |
| DB tables | `QuotaAllocation`, `QuotaUsage` rolling counters |
| Commit path | Sync check + commit per chosen option (document in `docs/finops.md`) |

**Unit tests:**

- Quota exhausted → 429  
- Soft quota → response header `X-33pol-Quota-Warning`  

### WP4.3 — Metrics (`33pol.Observability`)

Implement [12-metrics-and-runtime-contracts.md](../12-metrics-and-runtime-contracts.md) §2 index (v1 rename table §1):

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

Implement **`IControlPlaneCommands`** and **`IAdminSummaryReader`** in Core (interfaces). Implement **`ControlPlaneCommands`** and summary reader in **`33pol.Observability`**; register in **`33pol.App`**. `33pol.Api` endpoints stay thin (delegate to `IControlPlaneCommands` only). Same implementation used by WP4.9 console — no duplicated reload or registry logic.

| Endpoint | Purpose |
|----------|---------|
| `GET /admin/api/summary` | Metrics snapshot for UI / console |
| `GET /admin/api/backends` | Health + registry |
| `GET/POST/PATCH/DELETE /admin/api/models` | Registry CRUD — [13-live-model-registry.md](../13-live-model-registry.md) §5; delegate to `IModelRegistryWriter` |
| `GET /admin/api/requests?limit=` | Recent in-memory ring buffer (`IRecentRequestStore`) |
| `GET /admin/api/events/stream` | SSE per [12-metrics-and-runtime-contracts.md](../12-metrics-and-runtime-contracts.md) §5 (optional) |
| OpenAPI | Document all admin routes |

**Integration tests:**

- Admin auth required  
- Summary returns expected shape  
- `IControlPlaneCommands` covered via HTTP (console uses same impl in unit tests)  

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

### WP4.9 — Operator console (`33pol.OperatorConsole`) — optional

**Spec:** [08-operator-console.md](../08-operator-console.md)

| Task | Details |
|------|---------|
| Project | `33pol.OperatorConsole` + `33pol.OperatorConsole.Tests`; package `Spectre.Console` in `Directory.Packages.props` |
| Core interfaces | Consume `IControlPlaneCommands` from WP4.6 (`ControlPlaneCommands` in Observability — **not** in Api) |
| Options | `OperatorConsoleOptions` nested under `Gateway`; `IValidateOptions` for `RefreshInterval` bounds |
| Hosted service | `OperatorConsoleHostedService` — read/eval loop on dedicated task; respects `CancellationToken` |
| Registration | `AddOperatorConsole()` in `33pol.OperatorConsole`; called from `33pol.App` only when `Enabled` |
| Commands (MVP) | `help`, `exit`, `status`, `summary`, `watch summary`, `backends`, `requests [--limit N]`, `reload`, `models list`, `models add`, `models edit`, `models remove` |
| Spectre | Tables/panels for snapshots; `AnsiConsole.Live` for `watch summary` throttled by `RefreshInterval` |
| Security | `RequireAdminApiKey` validates admin scope; no secrets in output; audit `reload` via `IAuditLogger` |
| Logging | Serilog unchanged; no Spectre sink |
| Docs | `docs/operator-console.md`; update `deploy/docker/README.md` (console off in Compose) |
| Performance | Satisfy contract P1–P6 in [08-operator-console.md](../08-operator-console.md) §6; optional k6 smoke P7 |

**Unit tests:**

- `ControlPlaneCommands` in `33pol.Observability.Tests` with fakes (reload, summary, backends)  
- Command parser in `33pol.OperatorConsole.Tests`: input string → intent  
- `OperatorConsoleOptions` validation (refresh min/max)  
- Console disabled → hosted service not registered (composition test or integration factory)  

**Integration tests:**

- Default test host: `Gateway:OperatorConsole:Enabled` = `false`  
- HTTP admin still works when console enabled in a dedicated test class  

**Exit criteria (WP4.9):**

- [x] Spectre reference only in `33pol.OperatorConsole` (NetArchTest)  
- [x] Development `appsettings` sample enables console; Production/CI samples disable  
- [ ] Operator can run `watch summary` and `reload` locally without stopping inference (manual smoke — optional for GA)  
- [x] `docs/operator-console.md` complete  

---

## Unit test checklist (Phase 4)

- [x] Rate limit matrix per policy  
- [x] Quota hard/soft behavior  
- [x] Metrics label constraints (static analysis or unit)  
- [x] Error codes for **P4** 429 variants + `Retry-After`  
- [x] Usage parser fixtures  
- [x] Control plane command handlers + console parser (if WP4.9 in scope)  
- [x] Coverage ≥ 85% Observability, ≥ 90% Policy, ≥ 90% OperatorConsole (if WP4.9 in scope)  

---

## Exit criteria

- [x] Rate limits demonstrably enforced in integration tests  
- [x] Grafana dashboard JSON valid; `promtool` validates alert rules (full Compose stack verification in Phase 5)  
- [x] OTel traces visible in collector sample  
- [x] `/admin/api/summary` authenticated and populated  
- [x] Prometheus alert rules validate (promtool)  
- [x] WP4.9: operator console complete **or** explicitly deferred with user sign-off (HTTP admin remains required)  
- [x] Taiga epic P4 closed  

---

## Taiga story seeds

1. As an operator, I see p99 latency and error rate in Grafana.  
2. As a tenant, I receive 429 with retry guidance when over limit.  
3. As support, I correlate logs and traces with `X-Request-Id`.  
4. As an operator on my laptop, I use the Spectre console to inspect backends and reload config without stopping the gateway.  

---

## Taiga backlog (sadeghhp-33pol)

**Epic:** `EPIC-P4-policy-obs` (id 357940)

| Milestone | ID | Dates |
|-----------|-----|--------|
| P4-Sprint-1 — Rate limits & quotas | 520880 | 2026-09-01 ~ 2026-09-14 |
| P4-Sprint-2 — Metrics, OTel & logging | 520881 | 2026-09-15 ~ 2026-09-28 |
| P4-Sprint-3 — Control plane & usage metering | 520883 | 2026-09-29 ~ 2026-10-12 |
| P4-Sprint-4 — Ops artifacts, console & exit | 520882 | 2026-10-13 ~ 2026-10-26 |

| Ref | User story | Sprint | WP |
|-----|------------|--------|-----|
| #244 | US-P4-01: Rate limiting | P4-Sprint-1 | 4.1 |
| #245 | US-P4-02: Quotas | P4-Sprint-1 | 4.2 |
| #246 | US-P4-03: Metrics catalog | P4-Sprint-2 | 4.3 |
| #247 | US-P4-04: OpenTelemetry | P4-Sprint-2 | 4.4 |
| #248 | US-P4-05: Logging++ enrichers | P4-Sprint-2 | 4.5 |
| #249 | US-P4-06: Control plane APIs | P4-Sprint-3 | 4.6 |
| #251 | US-P4-08: Usage recording metering hook | P4-Sprint-3 | 4.8 |
| #250 | US-P4-07: Observability artifacts | P4-Sprint-4 | 4.7 |
| #252 | US-P4-09: Operator console (optional) | P4-Sprint-4 | 4.9 |

### Tasks by story

| Story | Tasks (refs) |
|-------|----------------|
| #244 | P4-T-01 #253 … P4-T-09 #314, #332 optional Redis |
| #245 | P4-T-10 #259 … P4-T-16 #316 (quota + finops stub) |
| #246 | P4-T-20 #264 … P4-T-27 #318 (metrics catalog) |
| #247 | P4-T-30 #270 … P4-T-36 #276, #330 exit OTel smoke |
| #248 | P4-T-40 #277 … P4-T-43 #280 |
| #249 | P4-T-50 #281 … P4-T-59 #290, #319–#320, #331 exit summary smoke |
| #250 | P4-T-60 #291 … P4-T-62 #293, #328–#329 exit promtool + Grafana JSON |
| #251 | P4-T-70 #294 … P4-T-75 #321 (usage + router wire) |
| #252 | P4-T-80 #299 … P4-T-99 #334, #311 umbrella (console, arch, docs, exit) |

**Total:** 82 tasks (#253–#334). Initial backlog (#253–#311), gap-fill (#312–#327), exit split + optional (#328–#334).

### Phase 4 checklist → Taiga coverage

| Doc item | Taiga task(s) |
|----------|----------------|
| WP4.1 AddRateLimiter, resolver, algorithms, distributed store | #253–#258, #312–#314, #332 optional |
| WP4.2 IQuotaService, soft/hard, DB tables, commit path | #259–#263, #315–#316 |
| WP4.3 Metrics catalog, RequestTracker, /metrics, /stats | #264–#269, #317–#318 |
| WP4.4 OTel traces, instrumentation, spans, propagation | #270–#276, #330 |
| WP4.5 Serilog enrichers, completion log, audit channel | #277–#280 (#274 OTel enricher in WP4.4) |
| WP4.6 Control plane APIs + IControlPlaneCommands | #281–#290, #319–#320, #331 |
| WP4.7 Grafana, Prometheus alerts, observability.md | #291–#293, #328–#329 |
| WP4.8 IUsageRecorder, usage parse, DB writer | #294–#298, #321 |
| WP4.9 Operator console (optional) | #299–#308, #322–#326, #333 |
| P4 429 error codes + Retry-After golden tests + errors.md | #309, #327 |
| Coverage gates + phase exit | #310–#311 (umbrella), #328–#334 |

### Optional Taiga tasks (may skip with sign-off)

| Ref | Task | Story |
|-----|------|-------|
| #332 | P4-T-97: Optional Redis rate limit integration test | #244 |
| #333 | P4-T-98: Optional IOperatorConsoleTestHarness | #252 |

### Phase 4 exit tasks (sprint 4)

| Ref | Task | Exit criterion |
|-----|------|----------------|
| #328 | promtool validate `33pol.yml` | Prometheus alert rules validate |
| #329 | Validate Grafana dashboard JSON | Grafana JSON valid |
| #330 | OTel collector sample smoke | OTel traces visible |
| #331 | `/admin/api/summary` integration smoke | Summary authenticated and populated |
| #334 | Epic P4 sign-off checklist | Taiga epic P4 closed; WP4.9 or defer |
| #311 | Umbrella exit coordination | Links #328–#334, #314, #308 |

### Intentionally deferred (no Taiga task)

| Item | Where |
|------|--------|
| Prometheus **recording rules** / SLO sign-off | Phase 5 ([12-metrics-and-runtime-contracts.md](../12-metrics-and-runtime-contracts.md) header) |
| `gateway_usage_writer_*` metrics | Phase 5 WP5.2 |
| Full `docs/finops.md` (rate cards, exports) | Phase 5; P4 adds quota semantics stub (#316) |
| Taiga **story seeds** (#1–#4 operator/tenant narratives) | Covered by WP user stories #244–#252 |
| Full Compose stack Grafana/OTel verification | Phase 5; P4 uses promtool (#328) + collector sample (#330) |
