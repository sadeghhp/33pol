# 33pol Gateway — Architecture

High-level view of the modular monolith. The boundaries below are enforced by `tests/33pol.Architecture.Tests` (NetArchTest), not just documented.

## Overview

33pol is a single **ASP.NET Core 10** process that terminates OpenAI-compatible client traffic, applies security and policy, and forwards inference to configured upstream backends (vLLM, OpenAI-compatible servers, mocks).

```text
Clients (SDKs) ──► 33pol.App (Kestrel)
                      ├── Security (API keys, tenant context)
                      ├── Policy (rate limit, quota, circuit breaker)
                      ├── Proxy (model router, streaming)
                      ├── Registry (DB-backed routes + live CRUD; models.json seed/fallback)
                      ├── Billing (usage events, rollups, FinOps APIs)
                      ├── Observability (metrics, recent requests, admin summary)
                      └── Api (admin + health + models list)
                           └── Upstream backends (per model URL)
```

## Planes

| Plane | Paths | Auth |
|-------|-------|------|
| **Inference** | `/v1/chat/completions`, `/v1/completions`, `/v1/embeddings`, `GET /v1/models` | Inference or Admin API key when DB/bootstrap enabled |
| **Control** | `/admin/api/*`, `/admin` UI | Admin API key |
| **Ops** | `/health`, `/health/live`, `/health/ready`, `/metrics` | Probes public; `/health` detail and `/metrics` need scrape token / Operator key (see [security.md](./security.md)) |
| **Ops (privileged)** | `/stats` | Admin API key — the snapshot names every model that served traffic and its error count |

## Projects

| Project | Responsibility |
|---------|----------------|
| `33pol.App` | Host, middleware order, static admin UI |
| `33pol.Core` | Models, options, error catalog, abstractions |
| `33pol.Proxy` | YARP-style forwarding, resilience, quotas middleware |
| `33pol.Registry` | Model registry file + hot reload |
| `33pol.Security` | API key validation, authorization, admin keys |
| `33pol.Policy` | Rate limits, quotas, circuit breaker |
| `33pol.Observability` | Metrics, usage channel, control plane commands |
| `33pol.Billing` | Rate cards, rollups, exports, webhooks |
| `33pol.Persistence` | EF Core + SQLite (tenants, keys, grants, billing, DB-backed config) |
| `33pol.Api` | Minimal API endpoint mapping |
| `33pol.OperatorConsole` | Optional Spectre.Console TUI |

## Middleware order (inference path)

```text
Serilog → Routing → CORS → RequestId → Auth → Authorization
  → (admin / health / metrics branches)
  → Rate limit → Quota → Model router → upstream HTTP
```

## Concurrency model and admission control

Every stage of the inference path is asynchronous and per-request: Kestrel accepts connections without a per-connection thread, the body is buffered and scanned once (`Utf8JsonReader`, no `JsonDocument`), and the upstream call is an `HttpClient` send with `ResponseHeadersRead` followed by a 16 KB chunk copy that flushes per chunk when streaming. Nothing on the path holds a process-wide lock across an await, and no request awaits a database write — usage, errors and stats are enqueued to bounded in-memory buffers drained by background writers. Measured against a slow, deliberately concurrent mock (see `perf/reports/concurrency-2026-08-16.md`), 64 simultaneous 2-second requests complete in ~2 s wall-clock, streaming or not.

What *bounds* concurrency is admission control, applied in this order and each answering `429` with `Retry-After` and an `X-33pol-Error-Code`:

| Stage | Scope | Setting | Default | On breach |
|-------|-------|---------|---------|-----------|
| Request rate | per partition (tenant, or remote address when anonymous) | Admin → Rate limits: `Rpm`, `Burst` (token bucket, capacity `Rpm + Burst`, refill `Rpm`/min) | 3000 / 500 | `rate_limit_exceeded`, `Retry-After ≈ 1 s` |
| Model bulkhead | per model | `Gateway:Resilience:MaxConcurrentForwardsPerModel` + `MaxQueuedForwardsPerModel` (bounded FIFO wait, `BulkheadQueueTimeoutSeconds`) | 256 in flight + 256 queued, 30 s | `concurrency_limit_exceeded` once queue is full or the wait times out |
| Stream slots | per partition | Admin → Rate limits: `MaxConcurrentStreams` (0 = unlimited) | 256 | `concurrency_limit_exceeded` |
| Circuit breaker | per model | `Gateway:Resilience:CircuitBreaker*` | 5 failures / 50 % in 30 s | `circuit_open` |

Two things about partitions are easy to miss. Every API key issued from the admin console belongs to the operator tenant, so all API-key traffic shares **one** partition and the default tier applies to the deployment as a whole; and without `Gateway:ForwardedHeaders`, all anonymous traffic behind a proxy shares the proxy's address. The rate-limit tier is read from the config snapshot — the database when one is configured, seeded on first boot — so it is edited in the admin UI, not in `appsettings.json`. The gateway logs the effective ceilings at startup (`Admission limits: …`) and warns when the stream cap is below the bulkhead.

The model server keeps its own queue behind the bulkhead: vLLM/TGI/SGLang batch continuously up to `--max-num-seqs`; Ollama serves `OLLAMA_NUM_PARALLEL` (auto 1–4) at a time and queues the rest; LM Studio serves one request at a time. If requests through the gateway complete one after another while the gateway reports no 429s and `gateway_bulkhead_queued` stays at zero, the serialization is in the model server — `perf/scripts/concurrency-bench.py` run against the gateway and against the server directly attributes it in one step.

## Data flows

**Inference:** Client → gateway validates key and model grant → policy checks → proxy selects backend URL → stream or buffer response → usage event enqueued → optional persistence batch.

**Admin:** Browser or CLI → `/admin/api/*` → `IControlPlaneCommands` / billing services → registry or DB.

## Deployment artifacts

| Artifact | Use |
|----------|-----|
| [Dockerfile](../Dockerfile) | Container image |
| [deploy/docker/](../deploy/docker/) | Local Compose (gateway with embedded SQLite, Prometheus, Grafana, mock) |
| [deploy/helm/33pol/](../deploy/helm/33pol/) | Kubernetes |
| [deploy/otel-collector/](../deploy/otel-collector/) | OTLP sample |

## Testing layout

| Suite | Purpose |
|-------|---------|
| `33pol.*.Tests` | Unit tests per library |
| `33pol.Integration.Tests` | HTTP surface, auth, proxy |
| `33pol.Architecture.Tests` | NetArchTest dependency rules |
| `33pol.Conformance.Tests` | OpenAI shapes + error golden JSON (GA) |

## Related docs

- [errors.md](./errors.md) — SDK error catalog  
- [observability.md](./observability.md) — metrics and dashboards  
- [finops.md](./finops.md) — billing and exports  
- [integrations.md](./integrations.md) — client and K8s guides  
- [security.md](./security.md) — threat model summary  
