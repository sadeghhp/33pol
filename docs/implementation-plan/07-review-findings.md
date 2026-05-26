# Implementation Plan — Review Findings

| | |
|---|---|
| **Last updated** | 2026-05-26 |
| **Scope** | `implementation-plan/`, `.cursor/rules/unit-test-coverage.mdc`, `perf/k6/thresholds.json`, `docs/old-version/` |
| **Verdict** | **Ready to start Phase 1.** Doc review items below are **remediated** in plan files. |

---

## Remediated (2026-05-26 doc pass)

| ID | Topic | Resolution |
|----|-------|------------|
| D1 | Ring buffer / recent requests | `IRecentRequestStore` in `33pol.Observability`; WP4.6 |
| D2 | `33pol.Persistence.Tests` | Added to `01-solution-architecture.md` solution tree |
| D3 | Phase 3 error-code checklist | P3 vs P4 split per `06-sdk-error-catalog.md`; phase-3 checklist updated |
| D4 | WP5.7 / WP5.8 order | Renumbered in `phase-5` (security review before conformance) |
| — | Public probe paths | `/health/live`, `/health/ready` on Phase 3 allowlist |
| — | Grant errors | 403 `insufficient_scope` for grant failures; `model_not_allowed` documented in catalog |
| — | Budget vs Quota | Clarified in `01-solution-architecture.md` and WP5.1 |
| — | SignalR → SSE | v1 admin transport migration in matrix + executive proposal §10 |
| — | Broken doc links | `implementation-plan/README.md`, `unit-test-coverage.mdc` |
| — | Conformance project | `33pol.Conformance.Tests` in solution layout |
| D5 | Operator console (Spectre) | [08-operator-console.md](./08-operator-console.md); WP4.9; shared `IControlPlaneCommands`; production/Docker default off |
| D6 | `IControlPlaneCommands` impl location | **Not** in `33pol.Api` — `ControlPlaneCommands` in `33pol.Observability`; HTTP mapping table in `08` §7; P6 clarified; GA/README updates |
| — | Deep review P0/P1 | `09-v1-parity-spec`, `10-identity-data-model`, `11-ha-and-scaling`, `12-metrics-and-runtime-contracts` |

---

## v1 parity — closed decisions

| v1 feature | v2 decision |
|------------|-------------|
| PostgreSQL `logs` table / `LogsDb` | **Not implemented** — Serilog + OTel only |
| SignalR `/hubs/admin` | **Replaced** by optional SSE `GET /admin/api/events/stream` (P4/P5) |
| Real-time request ring buffer | In-memory `IRecentRequestStore` (P4), not PostgreSQL |

---

## Changelog

| Date | Action |
|------|--------|
| 2026-05-26 | Initial review: 24 findings; remediation in plan docs |
| 2026-05-26 | Re-review: ready for Phase 1; deferred items documented |
| 2026-05-26 | Second doc pass: closed D1–D4 and cross-doc consistency fixes |
| 2026-05-26 | Operator console: `08-operator-console.md`; plan v1.1; architecture, matrix, phase-4 WP4.9 |
| 2026-05-26 | Doc review remediations (D6): impl in Observability, HTTP equivalence, P6, Serilog mitigation, mermaid, harness |
| 2026-05-26 | Deep review P0/P1: normative specs 09–12; phase cross-links |
