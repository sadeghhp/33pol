# Implementation Plan vs Repository — Gap & Issue Report

**Date:** 2026-05-26  
**Last verified:** 2026-05-26 (P5-Sprint-3 + GA hardening)  
**Scope:** `docs/implementation-plan/` (phases 1–5, GA checklist) vs current repo  
**Test run:** `dotnet test 33pol.sln -c Release` — **all passed**  
**Coverage run:** `build/check-coverage.sh` — **gated assemblies pass**

---

## Executive summary

| Area | Plan status (docs) | Repo reality | Verdict |
|------|-------------------|--------------|---------|
| Phase 1 — Platform | Exit criteria checked | Matches | **Complete** |
| Phase 2 — Data plane | Exit criteria checked | Matches | **Complete** |
| Phase 3 — Security | Exit criteria checked | Matches | **Complete** |
| Phase 4 — Policy & obs | Exit criteria checked | SSE stream deferred | **Complete** |
| Phase 5 — FinOps & GA | Stories #336–#509 done in code | All WPs implemented | **Code complete** |
| GA readiness | Checklist open items | Staging perf + manual SDK + approvals | **Pre-GA** |

Phases **1–5** are **implemented in code**. GA release requires **staging k6**, **soak**, **SDK smoke script run**, and **checklist approvals** — see [ga-signoff.md](../ga-signoff.md).

---

## Phase 5 delivery (complete in repo)

| Work package | Status | Evidence |
|--------------|--------|----------|
| WP5.1 FinOps | Done | Billing, exports, forecast, webhooks, budgets |
| WP5.2 Usage writer | Done | Batched writer, alerts, drop metrics |
| WP5.3 Admin UI | Done | `wwwroot/admin/index.html` |
| WP5.3b Console | Done | `keys list` |
| WP5.4 Integrations | Done | Compose, Helm, docs, docker-image CI |
| WP5.5 Perf GA | Done | k6 suite, staging workflow, overhead compare |
| WP5.6 Docs | Done | architecture, runbooks, GA checklist |
| WP5.7 Security | Done | OWASP mapping, CI vuln audit, package pins |
| WP5.8 Conformance | Done | Chat, stream, models, completions, embeddings, error goldens |

---

## Remaining gaps (GA sign-off only)

| Gap | Owner | Notes |
|-----|-------|-------|
| k6 GA on **staging** | Ops | `.github/workflows/k6-ga-staging.yml` |
| **4h soak** on staging | Ops | `perf/k6/scripts/soak.js` |
| `perf/scripts/sdk-smoke.py` execution | Ops | After deploy |
| Compose E2E with gateway profile | Ops | `verify-compose-health.sh` + gateway profile |
| GA checklist approvals | Product/Eng | [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md) |
| Close epic EPIC-P5 | Taiga | After checklist |

---

## Optional / post-GA

| Item | Notes |
|------|-------|
| SSE `GET /admin/api/events/stream` | Deferred P4 |
| Redis rate limit store | In-memory per pod |
| Durable audit logger | Structured logs today |
| Stripe adapter | Backlog |

---

## CI

| Job | Purpose |
|-----|---------|
| `build-test` | Tests, coverage, promtool, vulnerability audit |
| `k6-smoke` | Mock + gateway + smoke.js |
| `k6-nightly` | Extended smoke schedule |
| `docker-image` | GHCR publish on `main` |
| `k6-ga-staging` | Manual full GA suite |

---

## Related artifacts

- [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md)  
- [ga-signoff.md](../ga-signoff.md)  
- [perf/reports/ga-2026-05-26.md](../perf/reports/ga-2026-05-26.md)
