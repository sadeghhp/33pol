# Phase 2 — Core Data Plane (v1 Parity)

**Epic:** `EPIC-P2-data-plane`  
**Duration (guide):** 2–3 weeks  
**Prerequisite:** Phase 1 complete  
**Blocks:** Phase 3  

---

## Objective

Deliver a **fully tested OpenAI-compatible inference proxy**: model registry, routing middleware, YARP forwarder, streaming, backend health, and models API — **without** database auth (optional static API keys stub only if needed for middleware ordering tests).

This phase produces a **deployable internal gateway** matching v1.2.0 proxy behavior with v2 fixes (JSON model rewrite, no unused YARP reverse proxy).

**Normative acceptance:** [09-v1-parity-spec.md](../09-v1-parity-spec.md) — Phase 2 exit tests tagged `V1Parity`.

---

## Outcomes

- `models.json` loaded; full hot reload via `ConfigReloadService` (WP2.5)  
- Inference POSTs forwarded to mock/real upstream  
- SSE streaming works with correct headers  
- Health probes and gating  
- `GET /v1/models` (+ detail)  
- **90%+ unit coverage** on Registry + Proxy assemblies  
- Integration test suite for all inference paths  
- k6 **smoke** script runs against mock upstream  

---

## Work packages

### WP2.1 — Model registry (`33pol.Registry`)

| Task | Details |
|------|---------|
| `ModelConfig`, `ModelRegistryConfig` | JSON DTOs |
| `ModelRegistryService` | Load, alias map, thread-safe (per v1 spec) |
| Empty file behavior | Warn; do not clear on empty reload (document v2 improvement option) |
| `IModelRegistry` implementation | |

**Unit tests (required):**

- Load valid file → correct count  
- Alias resolves to canonical  
- Case-insensitive lookup  
- Invalid JSON throws  
- Concurrent read during reload  
- Empty `models` array behavior  

### WP2.2 — Health check service

| Task | Details |
|------|---------|
| `HealthCheckService` | Background loop, interval from options |
| Probe order | `/health`, `/api/tags`, `/` |
| `IBackendHealthStore` | Concurrent dictionary |
| Optimistic default until first probe | Config flag for strict mode (implement flag; default optimistic) |

**Unit tests:**

- Probe success on first endpoint stops  
- Unhealthy when all fail  
- `IsBackendHealthy` before first check  

### WP2.3 — Proxy core (`33pol.Proxy`)

| Task | Details |
|------|---------|
| `ModelRouterMiddleware` | Path classification per [09-v1-parity-spec.md](../09-v1-parity-spec.md) §2–4 |
| Body parsing | `model`, `stream` via `Utf8JsonReader` or `JsonDocument` |
| `IHttpForwarder` + `HttpMessageInvoker` | SocketsHttpHandler settings per v1 spec |
| `StreamingHttpTransformer` | SSE headers; JSON model rewrite (not string replace) |
| OpenAI errors | 400, 404, 502 (basic types; full codes Phase 3) |
| `RequestTracker` stub | Hook for Phase 4 metrics |

**Unit tests:**

- Passthrough paths call next  
- Non-POST not routed  
- Missing model → 400  
- Unknown model → 404  
- Unhealthy → 502  
- Streaming flag detected  
- Model rewrite cases (spacing variants)  

### WP2.4 — API endpoints (`33pol.Api`)

| Task | Details |
|------|---------|
| `GET /v1/models` | Filter healthy only |
| `GET /v1/models/{model}` | Alias lookup; `available` field |
| `GET /health` | Gateway + backend list (v1 shape) |
| `GET /stats` | Basic in-memory counters (minimal) |
| Passthrough `/metrics` | Placeholder or basic counter (expanded Phase 4) |

**Integration tests:**

- Golden JSON for `/v1/models`  
- Unhealthy model omitted from list  

### WP2.5 — Configuration reload

| Task | Details |
|------|---------|
| `ConfigReloadService` | Poll 5s, hash detection (use SHA256 not `GetHashCode`) |
| `POST /admin/api/config/reload` | Unauthenticated in Phase 2 — **document security debt** fixed Phase 3 |
| `GET /admin/api/config/status` | |

**Unit tests:**

- Semaphore prevents concurrent reload  
- Hash change triggers reload  
- Failed reload keeps previous registry  

### WP2.6 — Kestrel, Serilog & host wiring

| Task | Details |
|------|---------|
| Serilog bootstrap | `UseSerilogRequestLogging` — method, path, status, duration (no body) |
| Streaming limits | `MaxResponseBufferSize = null`, etc. |
| Register middleware order | **Phase 2 interim** (see below) |
| `models.json` in `App` content or config path resolution |

**Phase 2 interim middleware** (final order in [01-solution-architecture.md](../01-solution-architecture.md)):

```text
Serilog → UseRouting → (minimal APIs: /health, /v1/models, /admin, /stats, /metrics) → UseModelRouter
```

Auth, rate limits, and quota middleware are added in Phases 3–4.

Phase 4 adds OTel trace enrichers to Serilog only; do not defer Serilog to Phase 4.

### WP2.7 — Integration & perf baseline

| Task | Details |
|------|---------|
| Mock upstream | WireMock or test handler returning SSE fixture |
| Integration suite | chat, completions, embeddings paths |
| `perf/k6/scripts/smoke.js` | Run against TestServer or docker compose mock |
| Baseline report | `perf/reports/phase2-baseline.md` template |

---

## Unit test checklist (Phase 2)

- [ ] Registry: all cases in WP2.1  
- [ ] Health: all cases in WP2.2  
- [ ] Router: path matrix + error responses  
- [ ] Transformer: rewrite + streaming headers  
- [ ] Config reload: concurrency + hash  
- [ ] Coverage ≥ 90% Registry + Proxy  

---

## Exit criteria

- [ ] All v1 inference paths work against mock upstream  
- [ ] Streaming integration test receives chunks  
- [ ] No `AddReverseProxy` / `MapReverseProxy`  
- [ ] `dotnet test` green; coverage gate met  
- [ ] k6 smoke passes locally  
- [ ] Phase 2 baseline report drafted  
- [ ] [09-v1-parity-spec.md](../09-v1-parity-spec.md) §13 checklist satisfied (integration / `V1Parity`)  
- [ ] Taiga epic P2 closed  

---

## Known Phase 2 debt (fixed in Phase 3)

- Admin reload unauthenticated  
- Basic error `type` only (no full `code` catalog)  
- `/metrics` minimal  

---

## Taiga story seeds

1. As an OpenAI client, I can call chat completions through the gateway.  
2. As an operator, I can register models in `models.json` with aliases.  
3. As an operator, unhealthy backends stop receiving traffic.  
