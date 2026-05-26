# Implementation Plan vs Repository — Gap & Issue Report

**Date:** 2026-05-26  
**Last verified:** 2026-05-26 (post-remediation)  
**Scope:** `docs/implementation-plan/` (phases 1–5, GA checklist, normative specs 09–13) vs current repo  
**Test run:** `dotnet test 33pol.sln -c Release` — **307+ tests, 0 failed**  
**Coverage run:** `build/check-coverage.sh` — **all gated assemblies pass** (Proxy 91.3%, Observability 95.4%)

### Verification summary (implementation check)

| Report claim | Re-checked | Result |
|--------------|------------|--------|
| P1–P3 complete in code | Solution layout, endpoints, auth, persistence, golden errors | **Confirmed** |
| P4 mostly complete | Rate limit, quota, OTel, control plane, deploy artifacts | **Confirmed** with listed partials |
| `GET /admin/api/models` | Added 2026-05-26 — `AdminControlPlaneEndpoints` + integration tests | **Resolved** |
| SSE `/admin/api/events/stream` missing | No endpoint registration in `src/` | **Confirmed** |
| Console `models add/edit/remove` missing | `ConsoleCommandParser.cs` — `ModelsList` only | **Confirmed** |
| Usage not persisted to PG | `ChannelUsageRecorder` → quota + metrics only | **Confirmed** |
| P5 not started (UI, Helm, k6 GA, conformance) | No `wwwroot/admin`, no `deploy/helm/`, 1 k6 script, 1 conformance test | **Confirmed** |
| Stale README / GA checklist | `README.md` L5, `GA-CHECKLIST.md` P2–P4 unchecked | **Confirmed** |
| Coverage CI fail | Fixed via unit tests (Proxy 91.3%, Observability 95.4%) | **Resolved** |
| `NoOpAuditLogger`, in-memory rate limit/quota | DI registrations in Policy/Security | **Confirmed** |

No report findings were invalidated by the current tree; nothing listed as missing has been implemented since the original audit.

---

## Executive summary

| Area | Plan status (docs) | Repo reality | Verdict |
|------|-------------------|--------------|---------|
| Phase 1 — Platform | Exit criteria checked | Matches | **Complete** |
| Phase 2 — Data plane | Exit criteria checked | Matches (V1Parity tests, live registry, k6 smoke) | **Complete** |
| Phase 3 — Security | Exit criteria checked | Matches (auth, persistence, errors, resilience) | **Complete** |
| Phase 4 — Policy & obs | Exit criteria checked | Mostly matches; WP4.9 partial; SSE missing | **Mostly complete** (gaps below) |
| Phase 5 — FinOps & GA | Not started in plan checklists | Early scaffolding only | **Not started** |
| Documentation hygiene | README says “P2 next” | Phase docs + Taiga audit say P1–4 done | **Stale plan index** |
| GA readiness | Checklist largely open | Expected — Phase 5 + sign-off pending | **Pre-GA** |

Phases **1–4** are largely implemented in code; **Phase 5** and **GA sign-off** remain the primary delivery gap. Several **doc vs doc** and **doc vs CI** inconsistencies should be fixed to avoid false “done” signals.

---

## Documentation inconsistencies (issues)

These are process/metadata problems, not missing features:

| Issue | Where | Detail |
|-------|--------|--------|
| **Stale README status** | `implementation-plan/README.md` L5 | **Resolved** 2026-05-26 — P1–4 complete, P5 active. |
| **GA checklist vs phase docs** | `GA-CHECKLIST.md` | **Resolved** 2026-05-26 — P2–P4 signed with dates. |
| **WP4.9 exit criteria unchecked** | `phase-4-policy-and-observability.md` L198–201 | Phase 4 body marks epic done, but operator-console exit bullets (manual smoke, dev/prod samples) remain `[ ]`. |
| **Taiga vs repo** | `phase-audit-gap-matrix.md` | Board shows P1–4 Done; P5 epic/tasks exist in plan but implementation is minimal — risk of **board ahead of code** for P5. |

**Recommendation:** Single source of truth pass: update `README.md` + `GA-CHECKLIST.md` phase table to reflect P2–4 code-complete / P5 in progress; align WP4.9 checkboxes with `docs/operator-console.md`.

---

## Phase 1 — Platform foundation

**Plan:** Solution skeleton, CI, NetArchTest, `/health/live`, no proxy/DB.  
**Repo:** `33pol.sln`, 10+ library projects, matching test projects, `Dockerfile`, `.github/workflows/ci.yml`, architecture tests.

| Work package | Status | Notes |
|--------------|--------|-------|
| WP1.1 Solution | Done | Includes `33pol.Api.Tests` (not listed in architecture tree — harmless extra). |
| WP1.2 Core stubs | Done | `GatewayOptions`, `GatewayErrorCode`, abstractions present. |
| WP1.3 Host shell | Done | `GatewayHostBuilderExtensions`, health live, OpenAPI in Development. |
| WP1.4 CI/CD | Done | Coverage step + `check-coverage.sh`; promtool optional skip in CI. |
| WP1.5 Tests | Done | xUnit, NetArchTest, per-library tests. |
| WP1.6 Perf scaffold | Done | `perf/k6/` present. |

**Gaps:** None material.

---

## Phase 2 — Core data plane

**Plan:** v1 parity proxy, live registry, integration + k6 smoke, 90% Registry/Proxy coverage.

| Work package | Status | Notes |
|--------------|--------|-------|
| WP2.1 Registry | Done | `ModelRegistryService`, writer, tests. |
| WP2.2 Health | Done | Probe loop, `IBackendHealthStore`. |
| WP2.3 Proxy | Done | Router, streaming transformer, forwarder. |
| WP2.4 API endpoints | Done | `/v1/models`, `/health`, `/stats`. |
| WP2.5 Live registry | Done | Watch/poll, config reload/status. |
| WP2.6 Host wiring | Done | Serilog, Kestrel streaming limits. |
| WP2.7 Integration & perf | Done | `V1Parity` integration tests; `perf/k6/scripts/smoke.js`; `perf/reports/phase2-baseline.md`. |

**Gaps / issues:**

| Gap | Severity | Evidence |
|-----|----------|----------|
| ~~Proxy coverage below 90% gate~~ | — | **Resolved** — Proxy 91.3% after QuotaMiddleware, bulkhead, router grant tests. |
| **k6 not in CI** | Low | Plan Phase 2 smoke locally; CI has no k6 job (acceptable per plan timing; GA expects CI smoke in P5). |

---

## Phase 3 — Security & resilience

**Plan:** Postgres identity, API keys, P3 error catalog, resilience, secure admin, key CRUD.

| Work package | Status | Notes |
|--------------|--------|-------|
| WP3.1 Persistence | Done | EF migrations, repos, Testcontainers integration tests. |
| WP3.2 Authentication | Done | Bearer / `X-API-Key`, grants, `TenantContext`. |
| WP3.3 SDK errors | Done | Golden tests P3; `docs/errors.md` for P3 codes. |
| WP3.4 Resilience | Done | Timeouts, breaker, bulkhead, drain, body limits. |
| WP3.5 Health | Done | `/health/ready`, aggregate `/health`. |
| WP3.6 Secure admin | Done | Admin policy on `/admin/api/**`. |
| WP3.7 CORS | Done | Environment-based registration. |
| WP3.8 Key CRUD | Done | `AdminKeyEndpoints`. |

**Gaps / issues:**

| Gap | Severity | Evidence |
|-----|----------|----------|
| **`IAuditLogger` is no-op** | Low (P5) | `NoOpAuditLogger` only — plan defers durable audit to P5; P4 says wire admin channel (partial: calls exist, no structured audit sink). |
| **P2 admin reload debt** | Fixed | Config reload under admin auth in Phase 3. |

---

## Phase 4 — Policy & observability

**Plan:** Rate limits, quotas, metrics, OTel, control plane APIs, deploy artifacts, usage metering, optional operator console.

| Work package | Status | Notes |
|--------------|--------|-------|
| WP4.1 Rate limiting | Partial | In-memory `IDistributedRateLimitStore`; no Redis provider impl (plan: optional). |
| WP4.2 Quotas | Partial | `InMemoryQuotaService` — plan mentions `QuotaAllocation` / `QuotaUsage` DB tables; `docs/finops.md` defers PG quota to P5. |
| WP4.3 Metrics | Done | `GatewayMeters`, `/metrics`, `/stats`. |
| WP4.4 OpenTelemetry | Done | `GatewayOpenTelemetryExtensions`, collector sample. |
| WP4.5 Logging++ | Partial | Serilog + request logging; audit channel not fully wired. |
| WP4.6 Control plane APIs | Partial | See API gaps table. |
| WP4.7 Observability artifacts | Done | Grafana JSON, `33pol.yml` alerts, `docs/observability.md`. |
| WP4.8 Usage recording | Partial | `ChannelUsageRecorder` commits to in-memory quota + metrics; **no DB persistence** of usage rows yet. |
| WP4.9 Operator console | Partial | MVP commands only; no `models add/edit/remove`. |

**API gaps (WP4.6 vs plan):**

| Planned endpoint | Repo | Severity |
|------------------|------|----------|
| `GET /admin/api/models` | **Implemented** | Returns full `ModelConfig[]` from registry |
| `GET /admin/api/events/stream` (SSE) | **Missing** | Low — optional per plan |
| OpenAPI “document all admin routes” | Dev-only `MapOpenApi()` | Low |

**Operator console gaps (WP4.9 vs [08-operator-console.md](./implementation-plan/08-operator-console.md)):**

| Planned command | Repo |
|-----------------|------|
| `models list` | Done |
| `models add`, `models edit`, `models remove` | **Not in parser** (`ConsoleCommandParser.cs`) |
| `keys list` | P5 (5.3b) — N/A |

**Coverage gap:**

| Assembly | Actual | Threshold | Phase claim |
|----------|--------|-----------|-------------|
| ~~`33pol.Observability`~~ | **95.4%** | 85% | **Resolved** — runtime, usage recorder, summary tests added |

**CI note:** `promtool check rules` skipped when tool not installed — plan exit prefers validation (#328).

---

## Phase 5 — FinOps, UI, ecosystem & GA

**Plan:** Billing APIs, admin UI, Helm, k6 GA suite, conformance, full docs, GA sign-off.  
**Repo:** Early foundation only.

| Work package | Status | Notes |
|--------------|--------|-------|
| WP5.1 FinOps & billing | **Started** | Schema + `RateCardCostCalculator` + migrations; **no** `/admin/api/usage`, export, forecast, webhooks |
| WP5.2 Usage writer hardening | **Partial** | Channel queue exists; no PG writer, no `33pol-writer.yml` alerts, no paginated history API |
| WP5.3 Admin UI | **Not started** | No `wwwroot/admin` |
| WP5.4 Integrations | **Partial** | `deploy/docker/docker-compose.yml` exists; **no** `deploy/helm/33pol/` |
| WP5.5 Perf GA gates | **Not started** | Only `smoke.js`; missing `inference-rps.js`, `streaming-concurrent.js`, `rate-limit-storm.js`, soak |
| WP5.6 Documentation | **Partial** | `errors.md`, `observability.md`, `finops.md` (quota stub), `operator-console.md`; missing `integrations.md`, `security.md`, `runbooks/` |
| WP5.7 Security review | **Not started** | No `docs/security.md` |
| WP5.8 Conformance suite | **Stub** | `33pol.Conformance.Tests` — `Assembly_Loads` only; no OpenAI shape/golden suite |

**Billing test gap:** 7 tests vs plan “90%+ Billing” and webhook/export golden requirements.

---

## Normative specs cross-check

| Spec | Key requirement | Repo |
|------|-----------------|------|
| [09-v1-parity-spec.md](./implementation-plan/09-v1-parity-spec.md) | Inference paths, streaming, models API | Covered by integration tests (`V1Parity`) |
| [10-identity-data-model.md](./implementation-plan/10-identity-data-model.md) | Tenants, keys, grants | Implemented in Persistence + Security |
| [11-ha-and-scaling.md](./implementation-plan/11-ha-and-scaling.md) | Redis rate limits, HPA | Redis **not** implemented; Helm **missing** |
| [12-metrics-and-runtime-contracts.md](./implementation-plan/12-metrics-and-runtime-contracts.md) | Metric catalog, quota commit, SSE | Metrics largely present; quota in-memory; SSE **missing** |
| [13-live-model-registry.md](./implementation-plan/13-live-model-registry.md) | Writer + watch/poll + admin CRUD | Writer + HTTP mutations OK; **GET list** gap |

---

## GA checklist snapshot

From `implementation-plan/GA-CHECKLIST.md` — representative blockers for 2.0.0:

| Category | Open items |
|----------|------------|
| Phase sign-off | P2–P5 unchecked in GA doc |
| Functional | Admin UI, FinOps export, full operator console CRUD, SSE (optional) |
| Quality | Coverage thresholds fail Proxy + Observability locally |
| Performance | GA k6 scripts, soak, overhead report |
| Deployment | Helm chart; full Compose verification |
| Documentation | `integrations.md`, `security.md`, complete `finops.md`, runbooks |

---

## Middleware order (architecture vs host)

Plan ([01-solution-architecture.md](./implementation-plan/01-solution-architecture.md)):

```text
Logging → Routing → CORS → RequestId → Auth → RateLimit → Quota → metrics branch → ModelRouter
```

Repo (`GatewayHostBuilderExtensions.cs`): admin and `/v1/models` minimal APIs are mapped **before** `UseGatewayRateLimiting` / `UseGatewayQuotas` / `UseModelRouter`. Inference POSTs pass through rate limit + quota + router — **consistent with intent**. Admin routes skip rate limit middleware (acceptable for control plane).

---

## Prioritized remediation list

### P0 — Correct tracking / CI truth

1. ~~Update `implementation-plan/README.md`~~ **Done**  
2. ~~Reconcile `GA-CHECKLIST.md`~~ **Done**  
3. ~~Coverage CI gates~~ **Done**

### P1 — Phase 4 completeness (remaining)

1. ~~Add `GET /admin/api/models`~~ **Done**  
2. Implement operator console `models add|edit|remove` or document explicit deferral in Taiga.  
3. Close WP4.9 manual smoke + appsettings sample verification.

### P2 — Phase 5 critical path

1. FinOps APIs + usage PG writer (WP5.1–5.2).  
2. Admin UI `wwwroot/admin` (WP5.3).  
3. Helm chart + k6 GA scripts + CI smoke (WP5.4–5.5).  
4. Expand `33pol.Conformance.Tests` (WP5.8).  
5. `docs/integrations.md`, `docs/security.md`, runbooks (WP5.6–5.7).

### P3 — Optional / HA

1. Redis `IDistributedRateLimitStore` implementation.  
2. SSE `/admin/api/events/stream`.  
3. Durable `IAuditLogger` + retention.

---

## Test & coverage evidence

```
dotnet test 33pol.sln -c Release  →  all passed (13 test projects)
build/check-coverage.sh           →  OK: Proxy 91.3%, Observability 95.4%, Registry 91.6%, Policy 93.4%, Security 89.4%
```

**Integration surface:** `AdminModelsIntegrationTests` covers `GET /admin/api/models` (auth + shape); writer CRUD still covered at registry layer.

---

## Related artifacts

- Prior audit matrix: [phase-audit-gap-matrix.md](./phase-audit-gap-matrix.md)  
- Plan review log: [implementation-plan/07-review-findings.md](./implementation-plan/07-review-findings.md)
