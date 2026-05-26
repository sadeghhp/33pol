# LLM Gateway Core Specification (v1.2.0)

This folder contains **standalone technical documentation** for the core LLM Gateway application (`src/LlmGateway`). The documents describe behavior as implemented in **version 1.2.0** and are intended to support a **from-scratch rewrite** (e.g. v2) without requiring access to the original repository source code.

> **33pol v2:** Does **not** implement v1’s optional `LogsDb` / PostgreSQL **application log** persistence or a `logs` table. v2 uses Serilog → stdout and OpenTelemetry log export. See [../implementation-plan/01-solution-architecture.md](../implementation-plan/01-solution-architecture.md).

## Documents

| # | File | What it covers |
|---|------|----------------|
| 1 | [01-overview-and-architecture.md](./01-overview-and-architecture.md) | Product purpose, technology stack, system architecture, application startup, dependency injection, middleware pipeline, and all configuration (including `models.json`). |
| 2 | [02-core-proxy-and-routing.md](./02-core-proxy-and-routing.md) | Model registry, request routing middleware, HTTP forwarding, streaming, authentication, health checks, and end-to-end request lifecycle. |
| 3 | [03-api-operations-and-observability.md](./03-api-operations-and-observability.md) | Public HTTP APIs, metrics, hot reload, real-time admin (SignalR), v1 optional PostgreSQL persistence (reference only), deployment notes, known gaps, and rewrite checklist. |

## Recommended read order

1. **01-overview-and-architecture.md** — Understand what the gateway is and how the host is wired.
2. **02-core-proxy-and-routing.md** — Implement the proxy path (the critical logic).
3. **03-api-operations-and-observability.md** — Expose operations, monitoring, and v1 persistence reference (not carried forward for logs).

Each document can also be read **independently**: cross-links at the end point to the other two.

## Scope

- **In scope:** Core gateway process only (OpenAI-compatible proxy, registry, health, metrics, admin/reload APIs, optional v1 Postgres writes for logs/requests, SignalR for v1 admin).
- **Out of scope:** Blazor Admin UI (`src/LlmGateway.AdminUI`), nginx admin container, k6 load tests.
- **Not carried to v2:** `LogsDb`, `LogDbContext`, `LogPersistenceService`, PostgreSQL `logs` table, `GET /admin/api/logs`.

## Source version

Gateway application version constant: **1.2.0**

## Document boundaries (avoid overlap)

| Topic | Primary document |
|-------|------------------|
| Startup, DI, `models.json`, Kestrel, CORS | 01 |
| Router algorithm, forwarding, auth, health gating | 02 |
| `/health`, `/stats`, `/metrics`, `/v1/models`, admin, SignalR, v1 Postgres (reference) | 03 |
| v2 rewrite checklist and known gaps | 03 |

When a topic is mentioned briefly in one doc, the other docs link to the primary section rather than repeating full algorithms.
