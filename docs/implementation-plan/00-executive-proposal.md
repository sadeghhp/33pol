# 33pol v2 — Executive Proposal

**Product:** 33pol — OpenAI-compatible, high-performance LLM gateway  
**Platform:** .NET 10 (LTS), ASP.NET Core, YARP `IHttpForwarder`  
**Baseline:** LLM Gateway v1.2.0 (`docs/old-version/`)  
**Plan version:** 1.1  

---

## 1. Vision

33pol v2 is a **production-grade LLM edge** that preserves v1’s strengths (body-based routing, SSE streaming, minimal buffering) and adds **multi-tenant security**, **policy enforcement**, **FinOps-grade metering**, **enterprise observability**, **SDK-stable errors**, **resilience**, and **ecosystem integrations**—implemented with a **modern .NET 10 architecture** and **massive unit-test coverage** on all business logic.

## 2. Goals

| Goal | Measure |
|------|---------|
| OpenAI compatibility | Official SDKs work with base URL + API key only |
| Performance | Gateway overhead &lt; 5 ms p99 vs direct upstream (excl. cold policy cache) |
| Reliability | Circuit breakers, probes, graceful drain; 99.9% SLO target |
| Security | Hashed keys; no anonymous admin; separate control plane |
| Operability | Prometheus + OTel + Grafana; HTTP control plane (`/admin/api/*`); optional in-process **operator console** (Spectre.Console, local/on-box, disabled in production containers by default) |
| Commercial | Per-tenant usage, quotas, rate cards, exports |
| Quality | Unit tests on all logic; integration tests on HTTP surface; planned load tests |

## 3. v1 parity (retained)

- `POST /v1/chat/completions`, `/v1/completions`, `/v1/embeddings`
- `GET /v1/models`, `/v1/models/{model}`
- `models.json` registry with aliases and hot reload
- Backend health probes; health gating on inference
- `GET /health`, `/stats`, `/metrics` (evolved in later phases)
- `IHttpForwarder` only — **no** unused YARP `MapReverseProxy`
- Kestrel streaming settings; OpenAI-shaped errors

## 4. v2 differentiators

### Core product

- .NET 10 web host with modular **vertical slices** / clean boundaries
- Database-backed API keys, tenants, scopes
- Rate limiting and concurrency caps (especially streaming)
- Usage metering and quotas
- Minimal admin UI (`wwwroot/admin`)

### Proposal additions

| Area | Highlights |
|------|------------|
| **Resilience & production hardening** | Timeouts, circuit breakers, bulkheads, strict readiness, config validation, graceful shutdown, body limits, TLS validation |
| **SDK-friendly error codes** | Stable `code` enum; `Retry-After`; `X-Request-Id`; documented catalog |
| **FinOps & advanced billing** | Rate cards, plans, budgets, cost labels, billing events, exports, webhooks |
| **Observability++** | OTel traces, SLO metrics, TTFT, exemplars, audit logs, SSE admin stream, runbooks |
| **Operator console** | Optional Spectre.Console TUI in-process; same control-plane commands as HTTP admin; config-gated; no inference-path coupling (see [08-operator-console.md](./08-operator-console.md)) |
| **Integration & ecosystem** | Helm, Compose, OpenAPI control plane, OpenAI/LangChain docs, ServiceMonitor, OTel collector samples |

## 5. Explicit non-goals (v2.0 GA)

- TLS termination at gateway (ingress responsibility)
- Multi-URL load balancing per model (single URL per model entry)
- Hosted SaaS control plane / Stripe billing (Phase 5 prepares exports; payment adapter optional post-GA)
- Prompt/content logging in production default
- PostgreSQL persistence of application / Serilog logs (use OTel + platform logging instead)
- Mandatory interactive TTY in production containers (operator console is opt-in; HTTP admin + Grafana are the default ops path)

## 6. Five-phase delivery model

Implementation is split into **five phases** with strict ordering (see [04-phase-overview.md](./04-phase-overview.md)):

1. **Platform foundation** — solution, CI, test projects, architecture skeleton  
2. **Core data plane** — proxy parity (testable without DB)  
3. **Security & resilience** — auth, persistence, hardening, error catalog  
4. **Policy & observability** — limits, quotas, metrics, OTel, admin APIs, optional operator console  
5. **FinOps, UI, ecosystem & GA** — billing, UI, deploy artifacts, load tests  

**No implementation** of gateway features begins outside Phase 1’s scope until Phase 1 exit criteria are **met**.

## 7. Quality contract

- **Unit tests:** Required for every class with behavior (see [02-testing-strategy.md](./02-testing-strategy.md))  
- **Integration tests:** HTTP surface via `WebApplicationFactory`  
- **Performance/load:** Planned from Phase 2 baselines through Phase 5 GA gates (see [03-performance-and-load-testing.md](./03-performance-and-load-testing.md))  

## 8. Success criteria (GA)

- [ ] Inference conformance suite passes against mock and real vLLM  
- [ ] Error code catalog fully implemented and published  
- [ ] `dotnet test` green; coverage gate met on business assemblies  
- [ ] Load test report: RPS, p99 latency, TTFT documented  
- [ ] Helm + Compose + Grafana + Alertmanager in repo  
- [ ] FinOps export and rate-card attribution demonstrated  

*Operator console (WP4.9) is **optional** for GA — required for GA: HTTP `/admin/api/*` control plane; console may be deferred with sign-off (see Phase 4 exit criteria).*

## 9. Documentation map (post-implementation)

| Path | Content |
|------|---------|
| `README.md` | Quick start |
| `docs/architecture.md` | Runtime architecture |
| `docs/errors.md` | SDK error codes |
| `docs/observability.md` | Metrics, traces, dashboards |
| `docs/finops.md` | Billing and quotas |
| `docs/integrations.md` | SDK, K8s, ingress |
| `docs/security.md` | OWASP API checklist, threat model (Phase 5) |
| `docs/operator-console.md` | Spectre operator console — config, commands, deployment (Phase 4) |

*This executive proposal is the source for Taiga epics #1–#5 aligned to implementation phases.*

## 10. v1 admin URL migration (breaking)

Operators upgrading from v1 (`docs/old-version/`) must update automation and scripts:

| v1 path | v2 path |
|---------|---------|
| `POST /admin/reload` | `POST /admin/api/config/reload` |
| `GET /admin/status` | `GET /admin/api/config/status` |

All `/admin/api/**` routes require an admin API key from Phase 3 onward.

**Real-time admin (breaking):** v1 used SignalR (`WebSocket /hubs/admin`). v2 uses optional **SSE** `GET /admin/api/events/stream` (Phase 4+) and polling against `/admin/api/summary`. Update dashboards and automation that depended on SignalR.

**Operator console (new in v2):** Optional in-process terminal UI (Spectre.Console) for local/on-box ops — not a replacement for `/admin/api/*`. Disabled by default in Production and Docker. See [08-operator-console.md](./08-operator-console.md).
