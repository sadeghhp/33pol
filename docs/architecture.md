# 33pol Gateway — Architecture

High-level view of the modular monolith. Normative detail: [implementation-plan/01-solution-architecture.md](./implementation-plan/01-solution-architecture.md).

## Overview

33pol is a single **ASP.NET Core 10** process that terminates OpenAI-compatible client traffic, applies security and policy, and forwards inference to configured upstream backends (vLLM, OpenAI-compatible servers, mocks).

```text
Clients (SDKs) ──► 33pol.App (Kestrel)
                      ├── Security (API keys, tenant context)
                      ├── Policy (rate limit, quota, circuit breaker)
                      ├── Proxy (model router, streaming)
                      ├── Registry (models.json + live CRUD)
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
| **Ops** | `/health/*`, `/metrics`, `/stats` | Public (probes / scrape) |

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
| `33pol.Persistence` | EF Core + Postgres (tenants, keys, grants, billing) |
| `33pol.Api` | Minimal API endpoint mapping |
| `33pol.OperatorConsole` | Optional Spectre.Console TUI |

## Middleware order (inference path)

```text
Serilog → Routing → CORS → RequestId → Auth → Authorization
  → (admin / health / metrics branches)
  → Rate limit → Quota → Model router → upstream HTTP
```

## Data flows

**Inference:** Client → gateway validates key and model grant → policy checks → proxy selects backend URL → stream or buffer response → usage event enqueued → optional persistence batch.

**Admin:** Browser or CLI → `/admin/api/*` → `IControlPlaneCommands` / billing services → registry or DB.

## Deployment artifacts

| Artifact | Use |
|----------|-----|
| [Dockerfile](../Dockerfile) | Container image |
| [deploy/docker/](../deploy/docker/) | Local Compose (Postgres, Prometheus, Grafana, mock) |
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
