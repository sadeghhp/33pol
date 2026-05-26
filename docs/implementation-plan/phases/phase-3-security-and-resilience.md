# Phase 3 — Security, Resilience & SDK Errors

**Epic:** `EPIC-P3-security`  
**Duration (guide):** 2–3 weeks  
**Prerequisite:** Phase 2 complete  
**Blocks:** Phase 4  

---

## Objective

Secure the gateway with **hashed API keys and tenants**, **production resilience** (timeouts, circuit breakers, limits on body size, graceful shutdown), **split control/data plane auth**, and **SDK error codes for all P3 rows** in [06-sdk-error-catalog.md](../06-sdk-error-catalog.md) (P4 429 codes are Phase 4).

Introduce **PostgreSQL** for identity (not yet full billing).

---

## Outcomes

- API key authentication on inference and admin (separate policies)  
- EF Core migrations for tenants, keys, scopes  
- Resilience policies active on forward path  
- All P3 catalog error codes implemented with stable `code` values  
- `/health/live` (since P1), `/health/ready` (readiness semantics), legacy `/health`  
- **90%+ unit coverage** on Security; **90%+** on Proxy resilience components  

---

## Work packages

### WP3.1 — Persistence foundation (`33pol.Persistence`)

**Schema:** [10-identity-data-model.md](../10-identity-data-model.md) (entities, roles, bootstrap, default grant policy).

| Task | Details |
|------|---------|
| `GatewayDbContext` | Tenants, ApiKeys (hash, prefix, scopes), ModelGrants |
| Migrations | Initial schema |
| Repositories | `IApiKeyRepository`, `ITenantRepository` |
| Testcontainers | Integration tests against real Postgres |

**Unit tests:**

- Entity configuration valid  
- Repository mapping (with in-memory provider for fast tests)  

### WP3.2 — Authentication (`33pol.Security`)

| Task | Details |
|------|---------|
| `ApiKeyAuthenticationHandler` or middleware | Bearer + `X-API-Key` |
| Key hashing | HMAC or PasswordHasher + pepper from config |
| `IApiKeyValidator` | Cache with `IMemoryCache`, TTL 1–5 min |
| Public paths (when keys configured) | `/health`, `/health/live`, `/health/ready`, `/metrics`, `/stats` — required for probes and v1 parity |
| Admin policy | Separate scheme or role `Admin` |
| `TenantContext` middleware | Populate `HttpContext` |

**Unit tests:**

- Valid key → success  
- Invalid, expired, revoked → 401 with `invalid_api_key` / `expired_api_key`  
- Model not in grant → 403 `insufficient_scope` (see catalog grant vs policy table)  
- Cache invalidation on revoke  

**Model grant enforcement:** After registry resolves the model alias, `IModelGrantService` validates against `TenantContext` before forward (in `ModelRouterMiddleware` or middleware immediately before router).

**Empty API key list:**

| Environment | Behavior |
|-------------|----------|
| Development | Empty `ApiKeys` allowed — gateway open (document risk) |
| Production | Startup fails if no keys configured **or** admin endpoints enabled without keys |

### WP3.3 — SDK error catalog (`33pol.Core` + `IErrorResponseWriter`)

Implement all **P3** rows in [06-sdk-error-catalog.md](../06-sdk-error-catalog.md) (not P4 429 codes):

| Codes | HTTP |
|-------|------|
| `invalid_json`, `missing_model`, `model_not_allowed`, `model_not_found`, `request_too_large`, … | 400/404 |
| `invalid_api_key`, `expired_api_key` | 401 |
| `insufficient_scope` | 403 |
| `backend_unhealthy`, `upstream_error`, `circuit_open` | 502 |
| `gateway_draining`, `not_ready` | 503 |

| Task | Details |
|------|---------|
| `RequestIdMiddleware` | UUID per request → `HttpContext`, response header `X-Request-Id` on **all** responses |
| `ErrorResponseWriter` | OpenAI envelope + `details` + headers `X-Request-Id`, `X-33pol-Error-Code` |
| `docs/errors.md` | Generated or maintained alongside enum |

**Unit tests:**

- One test per Phase 3 code → golden JSON file  
- `RequestIdMiddleware` sets header on success and error responses  

### WP3.4 — Resilience (`33pol.Proxy` + options; breaker config in `33pol.Policy`)

| Task | Details |
|------|---------|
| Request timeout | Linked to `HttpContext.RequestAborted` + options |
| Circuit breaker | Per model/backend — **options/thresholds** in `33pol.Policy`, **execution** in `33pol.Proxy` (Polly 8 or custom) |
| Bulkhead | Max concurrent forwards per model |
| Max request body size | Kestrel + middleware check |
| Graceful shutdown | `IHostApplicationLifetime` — drain flag; 503 `gateway_draining` |
| Upstream TLS | Validate certs; configurable for dev |
| Config validation | `IValidateOptions<GatewayOptions>` on startup |

**Unit tests:**

- Breaker opens after N failures  
- Timeout cancels forward  
- Body over limit → 400  
- Drain rejects new inference  

### WP3.5 — Health endpoints

`GET /health/live` exists since Phase 1 (always 200). This WP adds readiness semantics and v1-compatible aggregate health.

| Task | Details |
|------|---------|
| `GET /health/live` | Confirm liveness unchanged; must stay on public allowlist (WP3.2) |
| `GET /health/ready` | Registry loaded + policy (≥1 healthy backend optional) |
| Update `GET /health` | Keep v1 JSON compatibility |

**Integration tests:**

- Ready fails when DB required and down  
- Ready fails when draining  

### WP3.6 — Secure admin

| Task | Details |
|------|---------|
| Protect `/admin/api/**` | Admin API key or JWT (minimal JWT OK) |
| Audit log interface | `IAuditLogger` — log key reload, key create (implementation Phase 5) |

### WP3.7 — CORS & configuration

| Task | Details |
|------|---------|
| Environment-based CORS | Strict in Production |
| Remove always-on AllowAny in prod | |

### WP3.8 — API key administration (`33pol.Api` + `33pol.Security`)

| Endpoint | Behavior |
|----------|----------|
| `POST /admin/api/keys` | Create key; return secret **once** in response |
| `GET /admin/api/keys` | List keys (prefix, scopes, created; never full secret) |
| `POST /admin/api/keys/{id}/revoke` | Revoke key; cache invalidation |

Requires admin credential (WP3.6). Satisfies GA checklist “API key create/revoke (admin)”.

**Unit tests:**

- Create returns secret once; list never includes hash/secret  
- Revoke → subsequent inference 401  
- Non-admin → 403  

---

## Unit test checklist (Phase 3)

- [x] Every **P3** `GatewayErrorCode` row has golden JSON test (P4 rows in Phase 4)  
- [x] Auth middleware matrix (keys on/off, paths)  
- [x] Circuit breaker state machine  
- [x] Repository tests (in-memory + Testcontainers)  
- [x] Coverage ≥ 90% Security  
- [x] Coverage ≥ 90% Proxy resilience (WP3.4)  

---

## Exit criteria

- [x] Inference requires valid API key when configured  
- [x] Admin can create and revoke API keys (WP3.8)  
- [x] `X-Request-Id` present on all responses  
- [x] Admin mutations require admin credential  
- [x] No plaintext API keys in database  
- [x] Timeouts and circuit breaker integration tests pass  
- [x] `docs/errors.md` published  
- [x] Postgres migrations apply cleanly  
- [x] Taiga epic P3 closed  

---

## Taiga story seeds

1. As a tenant admin, I can create and revoke API keys.  
2. As a client SDK, I receive stable error codes on failure.  
3. As an operator, readiness fails when the gateway cannot serve traffic.  

---

## Taiga backlog (sadeghhp-33pol)

**Epic:** `EPIC-P3-security` (id 357930)

| Milestone | ID | Dates |
|-----------|-----|--------|
| P3-Sprint-1 — Postgres identity & repositories | 520861 | 2026-07-07 ~ 2026-07-20 |
| P3-Sprint-2 — API key auth, grants & tenant context | 520862 | 2026-07-21 ~ 2026-08-03 |
| P3-Sprint-3 — SDK errors & proxy resilience | 520863 | 2026-08-04 ~ 2026-08-17 |
| P3-Sprint-4 — Health, secure admin, key CRUD & exit | 520864 | 2026-08-18 ~ 2026-08-31 |

| Ref | User story | Sprint | WP |
|-----|------------|--------|-----|
| #187 | US-P3-01: Postgres identity and repositories | P3-Sprint-1 | 3.1 |
| #188 | US-P3-02: API key auth, grants and tenant context | P3-Sprint-2 | 3.2 |
| #189 | US-P3-03: SDK error catalog and request IDs | P3-Sprint-3 | 3.3 |
| #190 | US-P3-04: Timeouts, circuit breaker, drain and body limits | P3-Sprint-3 | 3.4 |
| #191 | US-P3-05: Readiness and aggregate health | P3-Sprint-4 | 3.5 |
| #192 | US-P3-06: Secure admin routes and production CORS | P3-Sprint-4 | 3.6–3.7 |
| #193 | US-P3-07: Admin key CRUD and Phase 3 exit | P3-Sprint-4 | 3.8 |

### Tasks by story

| Story | Tasks (refs) |
|-------|----------------|
| #187 | P3-T-01 #194 … P3-T-08 #232 (persistence scaffold, repos, bootstrap, tests) |
| #188 | P3-T-09 #233, P3-T-10 #200 … P3-T-19 #234 (auth, grants, host wiring, matrix tests) |
| #189 | P3-T-20 #209 … P3-T-25 #236 (request ID, errors, golden JSON, inference wiring) |
| #190 | P3-T-30 #213 … P3-T-38 #238 (resilience + integration tests) |
| #191 | P3-T-40 #220 … P3-T-43 #239 (ready, /health, /health/live, integration) |
| #192 | P3-T-50 #223 … P3-T-54 #241 (admin auth, audit, CORS, secure config reload) |
| #193 | P3-T-60 #226 … P3-T-65 #242 (key CRUD, exit checklist, phase integration) |

### Phase 3 checklist → Taiga coverage

| Doc item | Taiga task(s) |
|----------|----------------|
| WP3.1 `GatewayDbContext`, migrations, repos | #194–#196, #231 |
| WP3.1 Testcontainers + in-memory tests | #198, #199 |
| WP3.1 Bootstrap + ModelGrant policy | #197, #232 |
| WP3.2 Auth handler, hashing, validator, grants | #200–#208, #233–#234 |
| WP3.3 All 13 P3 error codes + `docs/errors.md` | #209–#212, #235–#236 |
| WP3.4 Timeout, breaker, bulkhead, body, drain, TLS | #213–#238 |
| WP3.5 `/health/ready`, `/health`, `/health/live` | #220–#222, #239 |
| WP3.6 Secure `/admin/api/**` + `IAuditLogger` | #223–#224, #240 |
| WP3.7 Production CORS | #225 |
| WP3.8 Key create/list/revoke | #226–#229 |
| Unit test checklist (matrix, breaker FSM, coverage) | #234, #237, #219, #230 |
| Exit criteria + v1 parity integration | #242, #238 |
| P2 debt: unauthenticated admin reload | #240 |
