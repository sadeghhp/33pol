# Phase 1 — Platform Foundation & Test Harness

**Epic:** `EPIC-P1-platform`  
**Duration (guide):** 1–2 weeks  
**Prerequisite:** None  
**Blocks:** Phase 2  

---

## Objective

Establish a **modern .NET 10 solution skeleton**, dependency boundaries, CI pipeline, and **test pyramid infrastructure** so all later phases ship logic with **massive unit test coverage** from day one.

**No proxy or business features** beyond a minimal host proving the pipeline works.

---

## Outcomes

- `33pol.sln` with projects per [01-solution-architecture.md](../01-solution-architecture.md)  
- CI runs `dotnet build` + `dotnet test` + coverage collection  
- `33pol.App` responds `200` on `GET /health/live`  
- NetArchTest enforces dependency rules  
- `perf/` directory scaffolded  

---

## Work packages

### WP1.1 — Solution and projects

| Task | Details |
|------|---------|
| Create solution | `dotnet new sln -n 33pol` |
| App host | `33pol.App` — `net10.0`, `Microsoft.NET.Sdk.Web` |
| Libraries | `33pol.Core`, `33pol.Registry`, `33pol.Proxy`, … (empty or minimal) |
| Test projects | One test project per library (including `33pol.Persistence.Tests`) + `Integration` + `Architecture` |
| Namespaces | Assembly-aligned `RootNamespace` on new projects (replace legacy `_33pol` from placeholder `33pol.csproj`) |
| Central package management | `Directory.Packages.props` for version pinning |
| EditorConfig | `.editorconfig`, analyzers (`EnableNETAnalyzers`) |

**Tests:** Architecture test — solution contains expected projects.

### WP1.2 — Core abstractions (stubs)

Implement in `33pol.Core` (no external deps):

| Type | Purpose |
|------|---------|
| `GatewayOptions` | Config binding shape |
| `TenantContext` | Placeholder for Phase 3 |
| `GatewayErrorCode` | Enum of stable codes (full catalog Phase 3) |
| `GatewayException` / `ErrorResult` | Error model |
| Interfaces | `IModelRegistry`, `IBackendHealthStore`, `IApiKeyValidator`, etc. (stubs) |

**Tests:** Every defined `GatewayErrorCode` value serializes to a stable string (not full catalog completeness — catalog grows in P3–P4; row-count match to `06-sdk-error-catalog.md` tested from Phase 3); options validation unit tests.

### WP1.3 — Host shell

| Task | Details |
|------|---------|
| `Program.cs` | Thin: `WebApplication.CreateBuilder`, register extensions, `Run` |
| `ServiceCollectionExtensions` | `AddGatewayCore()`, `AddGatewayHealthChecks()` |
| Health | `GET /health/live` always 200 |
| OpenAPI | Register OpenAPI generation (empty document OK) |
| Version endpoint | `GET /` returns service name + version |

**Tests:** Integration — `WebApplicationFactory` GET `/health/live` returns 200.

### WP1.4 — CI/CD

| Task | Details |
|------|---------|
| GitHub Actions / Azure Pipelines | build, test, coverage artifact |
| Coverage | coverlet + threshold placeholder (0% → raise Phase 2) |
| Dockerfile skeleton | multi-stage build placeholder (no publish yet) |

**Tests:** Pipeline runs on PR.

### WP1.5 — Test infrastructure

| Task | Details |
|------|---------|
| xUnit + FluentAssertions + NSubstitute | All test projects |
| `GlobalUsings` in tests | Common imports |
| Sample unit test per project | Proves wiring |
| NetArchTest | Core has no ASP.NET reference; Proxy no Persistence reference |
| Test data folder convention | Document in 02-testing-strategy |

### WP1.6 — Documentation & perf scaffold

| Task | Details |
|------|---------|
| `perf/k6/scripts/.gitkeep` | Structure per load test plan |
| `perf/k6/thresholds.json` | Initial smoke thresholds |
| Update root `README.md` | Point to implementation-plan |

---

## Unit test checklist (Phase 1)

- [ ] `GatewayOptions` validation (invalid paths, negative intervals)  
- [ ] `ErrorResult` serialization shape (golden JSON)  
- [ ] NetArchTest: all dependency rules  
- [ ] Options binding from configuration dictionary  

---

## Exit criteria

- [ ] `dotnet build` / `dotnet test` green  
- [ ] All projects target `net10.0`  
- [ ] `GET /health/live` integration test passes  
- [ ] NetArchTest passes  
- [ ] CI green on default branch  
- [ ] No proxy, registry, or DB code beyond stubs  
- [ ] Taiga epic P1 closed  

---

## Deferred to Phase 2+

- Model registry implementation  
- YARP forwarder  
- PostgreSQL  
- Prometheus metrics  

---

## Taiga story seeds

1. As a developer, I have a multi-project solution so features are isolated and testable.  
2. As a developer, CI fails if architectural dependency rules break.  
3. As an operator, I can hit `/health/live` to know the process is up.  
