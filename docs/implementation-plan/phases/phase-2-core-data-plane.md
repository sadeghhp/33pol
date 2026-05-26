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

- `models.json` loaded; **live registry** via `IModelRegistryWriter` + `ConfigReloadService` (watch/poll) ([13-live-model-registry.md](../13-live-model-registry.md))  
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
| `IModelRegistryWriter` | Validate → atomic file write → `LoadModelsAsync` in same call ([13](../13-live-model-registry.md) §3) |

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

### WP2.5 — Live registry & configuration reload

**Normative:** [13-live-model-registry.md](../13-live-model-registry.md)

| Task | Details |
|------|---------|
| `ModelRegistryWriter` | Implements `IModelRegistryWriter`; shares lock/swap with `ModelRegistryService` |
| `ConfigReloadService` | Debounced `FileSystemWatcher` when `RegistryWatchEnabled`; else poll **2 s** default, SHA-256 hash |
| `GatewayOptions` | Add `RegistryWatchEnabled` (default `true` in Development, `false` in Production) |
| `POST /admin/api/config/reload` | File-only re-read; unauthenticated in Phase 2 — **security debt** fixed Phase 3 |
| `GET /admin/api/config/status` | Include `watchEnabled` |

**Unit tests:**

- Semaphore prevents concurrent reload/apply  
- `AddModel` visible via `TryGetModel` before poll cycle  
- Persisted JSON matches in-memory after writer call  
- Hash / watcher change triggers reload from file  
- Failed reload keeps previous registry  
- Apply during concurrent reads (stress)  

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

- [x] Registry: all cases in WP2.1  
- [x] Health: all cases in WP2.2  
- [x] Router: path matrix + error responses  
- [x] Transformer: rewrite + streaming headers  
- [x] Live registry writer + config reload: concurrency, watch/poll, persist+apply  
- [x] Coverage ≥ 90% Registry + Proxy  

---

## Exit criteria

- [x] All v1 inference paths work against mock upstream  
- [x] Streaming integration test receives chunks  
- [x] No `AddReverseProxy` / `MapReverseProxy`  
- [x] `dotnet test` green; coverage gate met  
- [x] k6 smoke passes locally  
- [x] Phase 2 baseline report drafted  
- [x] [09-v1-parity-spec.md](../09-v1-parity-spec.md) §13 checklist satisfied (integration / `V1Parity`)  
- [x] Taiga epic P2 closed  

---

## Known Phase 2 debt (fixed in Phase 3)

- Admin reload unauthenticated  
- Basic error `type` only (no full `code` catalog)  
- `/metrics` minimal  

---

## Taiga backlog (sadeghhp-33pol)

**Epic:** `EPIC-P2-data-plane` (id 357919)

| Ref | User story | Sprint | WP |
|-----|------------|--------|-----|
| #82 | US-P2-01: Model registry | P2-Sprint-1 (520853) | 2.1 |
| #83 | US-P2-02: Backend health | P2-Sprint-1 | 2.2 |
| #86 | US-P2-05: Live registry & config reload *(was Config reload)* | P2-Sprint-1 | 2.5 |
| #84 | US-P2-03: Proxy core | P2-Sprint-2 (520854) | 2.3 |
| #85 | US-P2-04: API endpoints | P2-Sprint-2 | 2.4 |
| #87 | US-P2-06: Host Serilog & middleware | P2-Sprint-3 (520855) | 2.6 |
| #88 | US-P2-07: Integration & perf | P2-Sprint-3 | 2.7 |

### Live registry tasks (2026-05-26, doc [13-live-model-registry.md](../13-live-model-registry.md))

| Ref | Task | Story |
|-----|------|-------|
| #172 | P2-T-LR01: `IModelRegistryWriter` | #86 |
| #173 | P2-T-LR02: `ModelRegistryWriter` | #86 |
| #174 | P2-T-LR03: `RegistryWatchEnabled` | #86 |
| #176 | P2-T-LR04: FileSystemWatcher debounce | #86 |
| #175 | P2-T-LR05: poll SHA-256 | #86 |
| #177 | P2-T-LR06: POST config reload | #86 |
| #178 | P2-T-LR07: GET config status | #86 |
| #179 | P2-T-LR08: unit writer apply | #86 |
| #180 | P2-T-LR09: unit reload | #86 |
| #181 | P2-T-LR10: unit concurrent reads | #86 |
| #185 | P2-T-LR14: WP2.1 empty-array writer | #82 |
| #182 | P2-T-LR11: integration file watch | #88 |
| #183 | P2-T-LR12: integration vLLM no restart | #88 |
| #184 | P2-T-LR13: integration reload JSON | #88 |

**Phase 4 (not P2):** `/admin/api/models` CRUD, console `models add/edit/remove`, admin UI Models page.

## Taiga story seeds

1. As an OpenAI client, I can call chat completions through the gateway.  
2. As an operator, I can register a vLLM (or other) backend in `models.json` (or via writer API in tests) and use it on `/v1/chat/completions` without restarting the gateway.  
3. As an operator, unhealthy backends stop receiving traffic.  
