# Implementation Plan vs Repository — Gap & Issue Report

**Date:** 2026-05-27  
**Last verified:** 2026-05-27 (full audit vs `docs/implementation-plan/` + phases 1–5)  
**Scope:** `docs/implementation-plan/` (phases 1–5, GA checklist) vs current repo  
**Test run:** `dotnet test 33pol.sln -c Release` — **all passed** (583 tests across 13 projects)  
**Coverage run:** `build/check-coverage.sh` — **gated assemblies pass** (Registry, Proxy, Security, Policy, Observability, Billing)

Full phase-by-phase audit: see plan artifact *33pol Implementation Audit* (2026-05-27) or sections below.

---

## Executive summary

| Area | Plan status (docs) | Repo reality | Verdict |
|------|-------------------|--------------|---------|
| Phase 1 — Platform | Exit criteria checked | Matches | **Complete** |
| Phase 2 — Data plane | Exit criteria checked | Matches | **Complete** |
| Phase 3 — Security | Exit criteria checked | Matches | **Complete** |
| Phase 4 — Policy & obs | Exit criteria checked | SSE stream deferred | **Complete** |
| Phase 5 — FinOps & GA | All WPs in code | All WPs implemented | **Code complete** |
| GA readiness | Checklist open items | Staging perf + manual SDK + approvals | **Pre-GA** |

Phases **1–5** are **implemented in code**. GA release requires **staging k6**, **soak**, **SDK smoke execution**, **Compose E2E sign-off**, and **checklist approvals** — see [ga-signoff.md](./ga-signoff.md).

**Production multi-replica:** in-memory rate limits per pod; Redis `IDistributedRateLimitStore` not wired — see G-10/G-11.

---

## Phase 5 delivery (complete in repo)

| Work package | Status | Evidence |
|--------------|--------|----------|
| WP5.1 FinOps | Done | Billing, exports, forecast, webhooks, budgets |
| WP5.2 Usage writer | Done | Batched writer, alerts, drop metrics |
| WP5.3 Admin UI | Done | `wwwroot/admin/index.html` (dashboard, usage, backends, models add/edit/delete, keys) |
| WP5.3b Console | Done | `keys list` |
| WP5.4 Integrations | Done | Compose, Helm, ingress, docs, docker-image CI |
| WP5.5 Perf GA | Scripts + CI | k6 suite, `k6-ga-staging.yml`, overhead compare — **staging runs pending** |
| WP5.6 Docs | Done | architecture, finops, observability, runbooks (all-backends-down, writer-backlog) |
| WP5.7 Security | Done | OWASP mapping, CI vuln audit, package pins |
| WP5.8 Conformance | Done | Chat, completions, embeddings, models, error goldens |

---

## Enhanced gap & issue table

**Severity:** **Blocker** = GA sign-off | **High** = multi-replica / security posture | **Medium** = plan item or ops debt | **Low** = optional/post-GA/doc

| ID | Severity | Category | Gap / issue | Plan reference | Current state | Recommended action |
|----|----------|----------|-------------|----------------|-----------------|-------------------|
| G-01 | **Blocker** | GA / Perf | k6 `inference-rps.js` + `streaming-concurrent.js` not run on **staging** with signed thresholds | P5 WP5.5, GA §Performance | Workflow [`.github/workflows/k6-ga-staging.yml`](../.github/workflows/k6-ga-staging.yml); results pending | Run workflow; record in `perf/reports/ga-*.md` |
| G-02 | **Blocker** | GA / Perf | 4h soak (`soak.js`) not completed on staging | P5, GA §Performance | Script present | `SOAK_DURATION=4h` per [ga-signoff.md](./ga-signoff.md) |
| G-03 | **Blocker** | GA / Functional | OpenAI Python SDK smoke not executed | GA §Functional, WP5.8 | [`perf/scripts/sdk-smoke.py`](../perf/scripts/sdk-smoke.py) exists | Run after deploy; check GA boxes |
| G-04 | **Blocker** | GA / Ops | Compose stack E2E not verified (incl. gateway profile) | WP5.4, GA §Deployment | `perf/ci/verify-compose-health.sh` | Manual on test host per ga-signoff |
| G-05 | **Blocker** | GA / Process | Engineering/Ops/Product approvals empty | GA §Approvals | — | Fill [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md); close Taiga EPIC-P5 |
| G-06 | **Blocker** | GA / Observability | OTel traces end-to-end on staging | GA §Observability | OTel wired in host; collector sample exists | Staging trace smoke |
| G-07 | Medium | GA / Functional | Manual registry watch/poll (≤2s) on staging | GA §Functional | Unit/integration covered | One staging manual check |
| G-08 | Medium | GA / Functional | FinOps export validated by spreadsheet | P5 GA checklist | APIs + tests exist | Sample export review |
| G-09 | Medium | GA / Functional | Operator console manual smoke (optional) | GA §Functional | Code complete | Defer with sign-off note or run locally |
| G-10 | **High** | HA / Scale | Redis-backed `IDistributedRateLimitStore` not implemented | P4 WP4.1, [11-ha-and-scaling.md](./implementation-plan/11-ha-and-scaling.md) | `InMemoryDistributedRateLimitStore` per pod | Required before fair multi-replica RPM |
| G-11 | **High** | HA / Ops | Per-pod rate limits, recent-requests, admin state | 11-ha-and-scaling §B | Documented in [integrations.md](./integrations.md) | Redis + shared registry volume or accept limits |
| G-12 | Medium | Feature | SSE `GET /admin/api/events/stream` not implemented | P4 optional, matrix | Admin UI polls `/admin/api/summary` | Post-GA or polling-only GA |
| G-13 | Medium | Security | Durable audit retention/export | P3→P5 matrix, [security.md](./security.md) | `NoOpAuditLogger` → Serilog only | Post-GA backlog |
| G-14 | Low | Feature | Stripe metered billing adapter | Post-GA in P5 doc | Not started | Backlog |
| G-15 | Medium | Observability | Prometheus recording rules / SLO sign-off | P5 matrix, [12-metrics](./implementation-plan/12-metrics-and-runtime-contracts.md) §7 | Metric hooks only | Add rules or defer with GA note |
| G-16 | Medium | Docs / Ops | Chaos runbook missing | 05-feature-to-phase-matrix P5 | No `docs/runbooks/chaos*.md` | Add runbook or remove from matrix |
| G-17 | Medium | API / Docs | OpenAPI control plane publish (prod) | P4→P5 | `/openapi/v1.json` Development only | Expose behind auth or static artifact |
| G-18 | Medium | Testing | CI coverage gates omit Persistence, Core, Api, Console | [02-testing-strategy.md](./implementation-plan/02-testing-strategy.md) | `check-coverage.sh` — 6 assemblies | Extend gates or document exclusion |
| G-19 | Low | Testing | BenchmarkDotNet `perf/benchmarks` | P5 optional | Absent | Optional |
| G-20 | Low | Testing | Playwright admin UI E2E | P5 optional | Absent | Manual checklist OK for GA |
| G-21 | Low | FinOps | Usage retention background job | WP5.2 | Doc-only policy | Confirm in [finops.md](./finops.md) |
| G-22 | Low | Code hygiene | Unused `RequestTracker` stub in Proxy | — | DI uses Observability tracker | Remove or document |
| G-23 | Low | Docs | Plan README / phase-5 checkboxes stale | README, phase-5 | Synced 2026-05-27 | Keep aligned after GA |

---

## Remaining gaps (GA sign-off only) — summary

| Gap | Owner | ID |
|-----|-------|-----|
| k6 GA on **staging** | Ops | G-01 |
| **4h soak** on staging | Ops | G-02 |
| `perf/scripts/sdk-smoke.py` execution | Ops | G-03 |
| Compose E2E with gateway profile | Ops | G-04 |
| GA checklist approvals | Product/Eng | G-05 |
| OTel traces on staging | Ops | G-06 |
| Close epic EPIC-P5 | Taiga | G-05 |

---

## Post-GA backlog (Taiga)

Epics and stories planned in Taiga 2026-05-27. Full index: [post-ga-backlog.md](./post-ga-backlog.md).

| ID | Item | Taiga story |
|----|------|-------------|
| G-01–G-09 | GA sign-off (staging perf, SDK, approvals) | #527–#536, sprint **P5-Sprint-GA-signoff** |
| G-10 | Redis `IDistributedRateLimitStore` | #528 (supersedes duplicate #521) |
| G-11 | Multi-replica HA documentation + deployment patterns | #537 |
| G-12 | SSE admin event stream | #538 (supersedes #522) |
| G-13 | Durable audit logger | #539 (supersedes #523) |
| G-14 | Stripe adapter | #540 (supersedes #524) |
| G-15 | Prometheus recording rules / SLO dashboards | #541 (supersedes #525) |
| G-16 | Chaos engineering runbook | #542 |
| G-17 | OpenAPI control plane (non-Development) | #543 |
| G-18–G-23 | CI coverage, optional tests, hygiene, doc sync | #544–#549, epic **EPIC-quality-hygiene** |

---

## Optional / post-GA (quick reference)

| Item | Notes |
|------|-------|
| SSE `GET /admin/api/events/stream` | Deferred P4 (G-12) |
| Redis rate limit store | In-memory per pod (G-10) |
| Durable audit logger | Structured logs today (G-13) |
| Stripe adapter | Backlog (G-14) |

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

## Testing notes (2026-05-27)

- Fixed flaky `DailyUsageWebhookPublisherTests` (yesterday date aligned with test `utcNow`).
- Coverage gates: `33pol.Registry=90`, `Proxy=90`, `Security=85`, `Policy=85`, `Observability=85`, `Billing=90` — not gated: Core, Api, Persistence, OperatorConsole (see G-18).

---

## Phase 6 — quality review (2026-05-27)

| Item | Status |
|------|--------|
| Phase 6 plan docs | Added `phase-6`, `16-phase6-findings`, `17-phase6-review-rubric` |
| Assembly audit | All 11 `src` projects + admin UI — rubric signed |
| Open P0 findings | **None** |
| P1 remediations | F-P6-015 admin `RequireAuthorization`; F-P6-022 removed Proxy `RequestTracker`; F-P6-018/G-23 docs |
| P2 | Transferred to post-GA (G-12–G-21, G-19–G-20) |

See [implementation-plan/16-phase6-findings.md](./implementation-plan/16-phase6-findings.md).

---

## Related artifacts

- [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md)  
- [ga-signoff.md](./ga-signoff.md)  
- [phase-audit-gap-matrix.md](./phase-audit-gap-matrix.md)  
- [16-phase6-findings.md](./implementation-plan/16-phase6-findings.md)  
- [perf/reports/ga-2026-05-26.md](../perf/reports/ga-2026-05-26.md)  
- [perf/reports/ga-local-2026-05-27.md](../perf/reports/ga-local-2026-05-27.md) (local verification)
