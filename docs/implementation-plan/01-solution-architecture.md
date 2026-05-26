# Solution Architecture — .NET 10

## Design principles

1. **Modular monolith** — one deployable gateway process; clear project boundaries for testability and future extraction.
2. **Vertical slices** — feature folders over layered “Managers/Helpers”.
3. **Test-first** — business logic in testable libraries; `Program.cs` is composition only.
4. **Data plane vs control plane** — separate middleware branches and authorization policies.
5. **Framework-native** — prefer ASP.NET Core built-ins (rate limiting, health checks, OpenAPI, OTel) over custom frameworks.

---

## Solution layout (target)

```text
33pol.sln
├── src/
│   ├── 33pol.App/                    # Host: Program.cs, DI composition, middleware order
│   ├── 33pol.Core/                   # Domain models, interfaces, error codes, result types
│   ├── 33pol.Registry/               # models.json, ModelRegistryService
│   ├── 33pol.Proxy/                  # ModelRouterMiddleware, StreamingTransformer, forwarder
│   ├── 33pol.Security/               # ApiKey auth, tenant context, policies
│   ├── 33pol.Policy/                 # Rate limits, quotas, circuit breaker config
│   ├── 33pol.Observability/          # Metrics, tracing, logging enrichers, RequestTracker
│   ├── 33pol.Billing/                # Usage events, rate cards, writers
│   ├── 33pol.Persistence/            # EF Core, entities, migrations
│   ├── 33pol.Api/                    # Minimal API endpoint groups (admin, health, models)
│   └── 33pol.OperatorConsole/        # Spectre.Console TUI + hosted service (Phase 4, optional)
├── tests/
│   ├── 33pol.Core.Tests/
│   ├── 33pol.Registry.Tests/
│   ├── 33pol.Proxy.Tests/
│   ├── 33pol.Security.Tests/
│   ├── 33pol.Policy.Tests/
│   ├── 33pol.Observability.Tests/
│   ├── 33pol.Billing.Tests/
│   ├── 33pol.Persistence.Tests/
│   ├── 33pol.OperatorConsole.Tests/
│   ├── 33pol.Integration.Tests/      # WebApplicationFactory, Testcontainers optional
│   ├── 33pol.Architecture.Tests/     # NetArchTest dependency rules (Phase 1)
│   └── 33pol.Conformance.Tests/      # Phase 5 GA — OpenAI shape / error golden tests
├── deploy/
│   ├── docker/
│   ├── helm/33pol/                   # Phase 5
│   ├── grafana/
│   │   ├── provisioning/             # datasources, dashboard provider (scaffold early)
│   │   └── dashboards/               # e.g. 33pol-gateway.json (Phase 4)
│   ├── prometheus/alerts/            # Phase 4–5
│   └── otel-collector/               # Phase 4 sample, Phase 5 compose
├── perf/
│   ├── k6/
│   └── benchmarks/                   # BenchmarkDotNet (optional)
└── docs/
```

**Migration note:** Current repo has single `33pol.csproj` console app (`RootNamespace` `_33pol`) — Phase 1 replaces with the solution structure above without implementing proxy logic. Use assembly-aligned namespaces (not legacy `_33pol`) on new projects.

---

## Project dependency rules

```text
33pol.App
  → 33pol.Api, 33pol.Proxy, 33pol.Security, 33pol.Policy,
     33pol.Observability, 33pol.Billing, 33pol.Persistence, 33pol.Registry,
     33pol.OperatorConsole (optional reference; register only when enabled)
  → 33pol.Core

33pol.OperatorConsole → 33pol.Core only
33pol.Proxy → 33pol.Registry, 33pol.Core
33pol.Security → 33pol.Core, 33pol.Persistence (Phase 3+)
33pol.Policy → 33pol.Core, 33pol.Security
33pol.Observability → 33pol.Core
33pol.Billing → 33pol.Core, 33pol.Persistence
33pol.Persistence → 33pol.Core
33pol.Registry → 33pol.Core
33pol.Api → 33pol.Core only (endpoints resolve services via DI-registered interfaces from App)
```

**Forbidden:**

- `33pol.Core` must not reference ASP.NET, EF, or YARP.
- `33pol.Registry` must not reference HTTP pipeline types.
- `33pol.OperatorConsole` must not reference ASP.NET, YARP, `33pol.Proxy`, or `33pol.Api`.
- Spectre.Console package reference only in `33pol.OperatorConsole`.
- Circular references between feature projects.

Enforce with `33pol.Architecture.Tests` (NetArchTest) in Phase 1.

---

## Host composition (`33pol.App`)

### Middleware order (final state)

Inference and ops paths share the early stack; branches avoid sending `/metrics` or admin traffic through the model router.

```text
Logging → Routing → CORS → RequestId → Auth → RateLimit → Quota
  → (branch: /metrics → prometheus scrape; terminal, no router)
  → (branch: /v1/models, /admin, /health → minimal APIs)
  → ModelRouter → forward (inference only)
```

| # | Middleware | Phase introduced | Notes |
|---|------------|------------------|-------|
| 1 | Serilog request logging | 2 | Phase 4 adds trace enrichers only |
| 2 | `UseRouting` | 1 | |
| 3 | `UseCors` (environment-specific) | 3 | |
| 4 | `RequestIdMiddleware` | 3 | UUID on all responses (`X-Request-Id`) |
| 5 | `UseAuthentication` / `UseAuthorization` | 3 | |
| 6 | `UseRateLimiter` | 4 | |
| 7 | Quota enforcement | 4 | |
| 8 | Prometheus HTTP metrics / `UseMetricServer` | 4 | `/metrics` bypasses router (parity with v1) |
| 9 | `UseModelRouter` | 2 | Terminal for inference POSTs only |
| — | Minimal APIs + static files | 2+ | |

**Circuit breaker:** `33pol.Policy` defines options and thresholds (`CircuitBreakerOptions`); `33pol.Proxy` executes the breaker on forward — Policy must not reference YARP or HTTP types.

**Service ownership (no `33pol.Operations` project):**

| Component | Owner |
|-----------|--------|
| `ConfigReloadService` | `33pol.Registry` — file watch (debounced) + poll fallback |
| `ModelRegistryWriter` (`IModelRegistryWriter`) | `33pol.Registry` — validate, persist `models.json`, apply in-memory |
| `HealthCheckService` | `33pol.Registry` |
| `IModelGrantService` | `33pol.Security` (enforced in router after model resolve) |

### Configuration sections

| Section | Type | Phase |
|---------|------|-------|
| `Gateway` | `GatewayOptions` (`ModelsConfigPath`, `ConfigReloadIntervalSeconds`, `RegistryWatchEnabled`) | 2 |
| `RateLimiting` | `RateLimitOptions` | 4 |
| `Observability` | `ObservabilityOptions` | 4 |
| `Billing` | `BillingOptions` | 5 |
| `ConnectionStrings:GatewayDb` | Identity + usage events (single DB default) | 3 |
| `Gateway:OperatorConsole` | `OperatorConsoleOptions` (nested) | 4 |

**Application logs are not stored in PostgreSQL.** Serilog writes to stdout; production uses OpenTelemetry log export to the platform log stack (Loki, CloudWatch, etc.). Admin UI does not query historical logs from the gateway database.

Enterprise deployments may split databases; document connection names in `docs/architecture.md` if split.

Use **Options pattern** + `IValidateOptions<T>` for fail-fast startup validation.

---

## Key technology choices (.NET 10)

| Concern | Choice |
|---------|--------|
| Web SDK | `Microsoft.NET.Sdk.Web`, `net10.0` |
| Forwarding | `Yarp.ReverseProxy` — `AddHttpForwarder()` only |
| APIs | Minimal APIs in `33pol.Api` |
| Validation | `AddValidation()` + data annotations (admin APIs) |
| OpenAPI | `Microsoft.AspNetCore.OpenApi` 3.1 (control plane) |
| ORM | EF Core 10 + Npgsql |
| Metrics | OpenTelemetry Prometheus exporter **or** prometheus-net — **pick one in Phase 4; do not register both** |
| Traces | OpenTelemetry.Extensions.Hosting |
| Logging | Serilog.AspNetCore + enrichers |
| Unit tests | xUnit + FluentAssertions + NSubstitute |
| Integration | `Microsoft.AspNetCore.Mvc.Testing` |
| Containers | Testcontainers.PostgreSql (Phase 3+ integration) |
| Load | k6 (Phase 2 baseline, Phase 5 GA) |
| Operator console | Spectre.Console in `33pol.OperatorConsole` only; opt-in `IHostedService` |

---

## Control plane surfaces

Operators manage the gateway **without** sending admin traffic through `ModelRouterMiddleware`. Three first-class surfaces share the same domain services:

| Surface | Transport | Primary use | Phase | Production default |
|---------|-----------|-------------|-------|---------------------|
| Admin API | HTTP `/admin/api/*` | Automation, OpenAPI clients, remote ops | 3–4 | On (secured) |
| Admin UI | Browser → same-origin `/admin/api/*` | Dashboards, FinOps views | 5 | On |
| Operator console | stdin/stdout, Spectre.Console | Local dev, on-box troubleshooting | 4 (optional) | **Off** |
| Metrics stack | `GET /metrics`, Grafana | SLOs, alerting | 4–5 | On |

**Normative rule:** HTTP admin APIs are **canonical**. The operator console and browser UI are clients of `IControlPlaneCommands` / Core interfaces—not parallel implementations of reload, registry, or metrics logic.

Full specification: [08-operator-console.md](./08-operator-console.md).

### Operator console (summary)

- **Host:** `OperatorConsoleHostedService` (`IHostedService`) in `33pol.OperatorConsole`, registered from `33pol.App` when `Gateway:OperatorConsole:Enabled` is `true`.
- **Loop:** Dedicated long-running task for read/eval; **not** the Kestrel thread pool and not `Console.ReadLine` on the main host thread.
- **Data access:** `IControlPlaneCommands` (implemented by `ControlPlaneCommands` in Observability), `IAdminSummaryReader`, and other Core services — same orchestration as minimal APIs.
- **Performance:** Snapshot reads, throttled `AnsiConsole.Live` (default 1 Hz), atomic registry reload; see performance contract in [08-operator-console.md](./08-operator-console.md) §6.
- **Deployment:** Disabled in Production and Docker Compose by default; Development may enable via `appsettings.Development.json`.

---

## Interface-first extensibility

Define in `33pol.Core` early (Phase 1–2 stubs, Phase 3+ implementations):

| Interface | Responsibility |
|-----------|----------------|
| `IModelRegistry` | Model lookup, snapshots, load from file |
| `IModelRegistryWriter` | Add/update/remove models — persist `models.json` + atomic in-memory apply ([13-live-model-registry.md](./13-live-model-registry.md)) |
| `IBackendHealthStore` | Health state per model |
| `IApiKeyValidator` | Validate key → `TenantContext` |
| `IRateLimitPolicyResolver` | Limits for tenant/model |
| `IQuotaService` | Check/decrement quotas |
| `IUsageRecorder` | Queue usage events |
| `IErrorResponseWriter` | OpenAI + SDK error envelope |
| `IRequestTracker` | Metrics scope per request |
| `IModelGrantService` | Validate model against `TenantContext` grants |
| `IConfigReload` | File-only reload trigger/status (`33pol.Registry`; distinct from CRUD writer) |
| `IRecentRequestStore` | In-memory ring buffer for recent requests (`33pol.Observability`, Phase 4) |
| `IAuditLogger` | Admin/security audit events (interface Phase 3; durable sink Phase 5) |
| `IControlPlaneCommands` | Shared orchestration for admin HTTP + operator console — **`ControlPlaneCommands` in `33pol.Observability`** (Phase 4); registered in `33pol.App` |
| `IAdminSummaryReader` | Read-only operational snapshot for `/admin/api/summary` and console (`33pol.Observability`, Phase 4) |

Enables **in-memory fakes** in unit tests without HTTP. Admin endpoints in `33pol.Api` (thin) and commands in `33pol.OperatorConsole` depend only on these Core interfaces. **`33pol.Api` does not implement** `IControlPlaneCommands` (Api → Core only).

### Billing domain model (Plan vs Quota vs Budget vs Rate card)

| Concept | Phase | Role |
|---------|-------|------|
| **Plan** | 5 | Defines tenant tier limits (RPM, quotas, features); stored as `Plan` entity |
| **Quota** | 4 | **Inference gate:** monthly token/request budgets via `IQuotaService` → 429 `quota_exceeded` |
| **Budget** | 5 | **FinOps spend cap:** alerts/webhooks and optional hard stop; does not replace `IQuotaService` unless explicitly configured to call it |
| **Rate card** | 5 | Prices usage events for FinOps export (does not gate inference by default) |

Phase 4 rate limits resolve via `Tenant.PlanSlug` → `RateLimiting:Plans` in configuration until the `Plan` entity exists (Phase 5). See [10-identity-data-model.md](./10-identity-data-model.md) § Rate limit source.

**Normative contracts:** [09-v1-parity-spec.md](./09-v1-parity-spec.md) (proxy), [10-identity-data-model.md](./10-identity-data-model.md) (identity), [11-ha-and-scaling.md](./11-ha-and-scaling.md) (replicas), [12-metrics-and-runtime-contracts.md](./12-metrics-and-runtime-contracts.md) (metrics/quota/SSE), [13-live-model-registry.md](./13-live-model-registry.md) (live registry).

---

## Admin UI

- Static files: `src/33pol.App/wwwroot/admin/`
- Alpine.js (or Petite-Vue); no Blazor WASM requirement
- Calls same-origin `/admin/api/*` only
- Does **not** embed Spectre or terminal UI; browser only

---

## Operator console (`33pol.OperatorConsole`)

See [08-operator-console.md](./08-operator-console.md) for commands, config, security, and exit criteria.

| Concern | Decision |
|---------|----------|
| Library | Spectre.Console (presentation only) |
| Activation | `Gateway:OperatorConsole:Enabled` (default `false`) |
| Registration | `AddOperatorConsole()` extension; no-op when disabled |
| Tests | `33pol.OperatorConsole.Tests` — handlers without TTY |
| Phase | WP4.9 (after WP4.6 control-plane APIs) |

---

## Versioning and packaging

- Assembly informational version from CI build id
- Container image: `ghcr.io/<org>/33pol:2.x.y`
- Single binary publish optional later (`EnableRequestDelegateGenerator`, AOT evaluation post-GA)

---

## Phase mapping

| Architecture artifact | Phase |
|----------------------|-------|
| Solution + projects + NetArchTest | 1 |
| Registry, Proxy, basic host | 2 |
| Security, Persistence, resilience | 3 |
| Policy, Observability, Api endpoints, Operator console (optional) | 4 |
| Billing, wwwroot, deploy/, perf/ | 5 |

See phase documents for detailed work packages.
