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
│   └── 33pol.Api/                    # Minimal API endpoint groups (admin, health, models)
├── tests/
│   ├── 33pol.Core.Tests/
│   ├── 33pol.Registry.Tests/
│   ├── 33pol.Proxy.Tests/
│   ├── 33pol.Security.Tests/
│   ├── 33pol.Policy.Tests/
│   ├── 33pol.Observability.Tests/
│   ├── 33pol.Billing.Tests/
│   ├── 33pol.Integration.Tests/      # WebApplicationFactory, Testcontainers optional
│   └── 33pol.Architecture.Tests/     # NetArchTest dependency rules (Phase 1)
├── deploy/
│   ├── docker/
│   ├── helm/33pol/
│   ├── grafana/dashboards/
│   ├── prometheus/alerts/
│   └── otel-collector/
├── perf/
│   ├── k6/
│   └── benchmarks/                   # BenchmarkDotNet (optional)
└── docs/
```

**Migration note:** Current repo has single `33pol.csproj` console app — Phase 1 replaces with solution structure above without implementing proxy logic.

---

## Project dependency rules

```text
33pol.App
  → 33pol.Api, 33pol.Proxy, 33pol.Security, 33pol.Policy,
     33pol.Observability, 33pol.Billing, 33pol.Persistence, 33pol.Registry
  → 33pol.Core

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
| `ConfigReloadService` | `33pol.Registry` |
| `HealthCheckService` | `33pol.Registry` |
| `IModelGrantService` | `33pol.Security` (enforced in router after model resolve) |

### Configuration sections

| Section | Type | Phase |
|---------|------|-------|
| `Gateway` | `GatewayOptions` | 2 |
| `RateLimiting` | `RateLimitOptions` | 4 |
| `Observability` | `ObservabilityOptions` | 4 |
| `Billing` | `BillingOptions` | 5 |
| `ConnectionStrings:GatewayDb` | Identity + usage events (single DB default) | 3 |

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
| Metrics | OpenTelemetry Prometheus exporter **or** prometheus-net (pick one in Phase 4) |
| Traces | OpenTelemetry.Extensions.Hosting |
| Logging | Serilog.AspNetCore + enrichers |
| Unit tests | xUnit + FluentAssertions + NSubstitute |
| Integration | `Microsoft.AspNetCore.Mvc.Testing` |
| Containers | Testcontainers.PostgreSql (Phase 3+ integration) |
| Load | k6 (Phase 2 baseline, Phase 5 GA) |

---

## Interface-first extensibility

Define in `33pol.Core` early (Phase 1–2 stubs, Phase 3+ implementations):

| Interface | Responsibility |
|-----------|----------------|
| `IModelRegistry` | Model lookup, snapshots |
| `IBackendHealthStore` | Health state per model |
| `IApiKeyValidator` | Validate key → `TenantContext` |
| `IRateLimitPolicyResolver` | Limits for tenant/model |
| `IQuotaService` | Check/decrement quotas |
| `IUsageRecorder` | Queue usage events |
| `IErrorResponseWriter` | OpenAI + SDK error envelope |
| `IRequestTracker` | Metrics scope per request |
| `IModelGrantService` | Validate model against `TenantContext` grants |

Enables **in-memory fakes** in unit tests without HTTP.

### Billing domain model (Plan vs Quota vs Rate card)

| Concept | Phase | Role |
|---------|-------|------|
| **Plan** | 5 | Defines tenant tier limits (RPM, quotas, features); stored as `Plan` entity |
| **Quota** | 4 | Enforces monthly token/request budgets via `IQuotaService` |
| **Rate card** | 5 | Prices usage events for FinOps export (does not gate inference by default) |

Phase 4 rate limiting may use `Tenant.PlanSlug` (string) without the full FinOps engine.

---

## Admin UI

- Static files: `src/33pol.App/wwwroot/admin/`
- Alpine.js (or Petite-Vue); no Blazor WASM requirement
- Calls same-origin `/admin/api/*` only

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
| Policy, Observability, Api endpoints | 4 |
| Billing, wwwroot, deploy/, perf/ | 5 |

See phase documents for detailed work packages.
