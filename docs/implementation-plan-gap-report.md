# Implementation Plan vs Repository — Gap & Issue Report

**Date:** 2026-05-26  
**Last verified:** 2026-05-26 (k6 CI + FinOps batch)  
**Scope:** `docs/implementation-plan/` (phases 1–5, GA checklist, normative specs 09–13) vs current repo  
**Test run:** `dotnet test 33pol.sln -c Release` — **all passed**  
**Coverage run:** `build/check-coverage.sh` — **gated assemblies pass** (Proxy, Observability, Registry, Policy, Security)

---

## Executive summary

| Area | Plan status (docs) | Repo reality | Verdict |
|------|-------------------|--------------|---------|
| Phase 1 — Platform | Exit criteria checked | Matches | **Complete** |
| Phase 2 — Data plane | Exit criteria checked | Matches | **Complete** |
| Phase 3 — Security | Exit criteria checked | Matches | **Complete** |
| Phase 4 — Policy & obs | Exit criteria checked | Mostly complete; SSE optional | **Mostly complete** |
| Phase 5 — FinOps & GA | In progress on checklist | Core FinOps, admin UI, Helm, k6 suite, CI smoke | **In progress** |
| Documentation hygiene | README updated | Phase index aligned | **OK** |
| GA readiness | P5 open on checklist | Staging soak + sign-off remain | **Pre-GA** |

Phases **1–4** are implemented. **Phase 5** has substantial code (billing persistence, admin UI, webhooks, forecast, Helm, k6 scripts, **k6 smoke in CI**); GA sign-off still needs staging perf runs and checklist approvals.

---

## Resolved since original audit (2026-05-26)

| Item | Status |
|------|--------|
| Stale README / GA P2–P4 | **Resolved** |
| Coverage CI (Proxy, Observability) | **Resolved** |
| `GET /admin/api/models` | **Resolved** |
| Usage PG writer + batching + metrics | **Resolved** |
| Admin UI `/admin` (dashboard, usage, models, keys) | **Resolved** |
| Helm `deploy/helm/33pol/` | **Resolved** |
| k6 GA scripts (`inference-rps`, `streaming-concurrent`, `rate-limit-storm`) | **Resolved** |
| k6 `smoke.js` in GitHub Actions | **Resolved** (`perf/ci/run-smoke.sh`, job `k6-smoke`) |
| Operator console `models add\|edit\|remove` | **Resolved** |
| FinOps APIs (usage, export, forecast, events) | **Resolved** |
| Rate-card cost on persist, `quota.warning`, `usage.daily` webhooks | **Resolved** |
| Budget hard stop (`HardStopEnabled`) | **Resolved** |
| Conformance error goldens (16 codes) | **Resolved** |
| `docs/integrations.md`, `docs/security.md`, `docs/finops.md` | **Resolved** |

---

## Remaining gaps (prioritized)

### GA / staging (P2)

| Gap | Severity | Notes |
|-----|----------|-------|
| k6 GA scripts on **staging** | High | Run `inference-rps.js`, `streaming-concurrent.js`, `rate-limit-storm.js` against real stack; record in `perf/reports/` |
| **4h soak** | High | `perf/k6/scripts/soak.js` — manual on staging ([k6-smoke-ci.md](../perf/reports/k6-smoke-ci.md)) |
| Gateway overhead report | Medium | Document in `perf/reports/` per plan |
| P5 phase sign-off on GA checklist | Medium | Engineering approval row |

### Optional / post-GA (P3)

| Gap | Severity | Notes |
|-----|----------|-------|
| SSE `GET /admin/api/events/stream` | Low | Deferred in P4 checklist |
| Redis `IDistributedRateLimitStore` | Low | In-memory store in use |
| Durable `IAuditLogger` | Low | `NoOpAuditLogger` |
| Usage retention background job | Low | Documented TTL only |
| `33pol.Billing` 90% coverage gate | Low | **85% in CI** now; raise toward 90% with more tests |
| Broader conformance (OpenAI response shape) | Medium | Beyond error catalog goldens |
| WP4.9 manual smoke + appsettings samples | Low | Operator-console exit bullets |

---

## Phase 5 snapshot (code present)

| Work package | Status | Evidence |
|--------------|--------|----------|
| WP5.1 FinOps | Mostly done | Billing schema, rate cards, rollups, usage/export/forecast/events APIs, webhooks |
| WP5.2 Usage writer | Mostly done | Batched writer, queue/drop metrics, `33pol-writer.yml` alerts |
| WP5.3 Admin UI | Minimal viable | `wwwroot/admin/index.html` — dashboard, usage, models, keys |
| WP5.4–5.5 Deploy & perf | Partial | Helm chart; k6 scripts; **CI smoke**; staging GA runs open |
| WP5.6–5.7 Docs | Done | integrations, security, finops, observability, runbooks |
| WP5.8 Conformance | Partial | Error goldens + chat/models list shape tests; embeddings optional |

---

## CI

| Job | Purpose |
|-----|---------|
| `build-test` | `dotnet test`, coverage thresholds, promtool (optional) |
| `k6-smoke` | Mock upstream + gateway + `smoke.js` (30s in CI) |

---

## Test & coverage evidence

```bash
dotnet test 33pol.sln -c Release
build/check-coverage.sh TestResults
bash perf/ci/run-smoke.sh   # local; requires k6
```

---

## Related artifacts

- [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md)  
- [phase-audit-gap-matrix.md](./phase-audit-gap-matrix.md)  
- [perf/reports/k6-smoke-ci.md](../perf/reports/k6-smoke-ci.md)
