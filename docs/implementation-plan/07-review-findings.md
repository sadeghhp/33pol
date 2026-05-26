# Implementation Plan — Review Findings

| | |
|---|---|
| **Last updated** | 2026-05-26 |
| **Scope** | `implementation-plan/`, `.cursor/rules/unit-test-coverage.mdc`, `perf/k6/thresholds.json`, `docs/old-version/` |
| **Verdict** | **Ready to start Phase 1.** Earlier review items (24) are remediated in plan docs; only **open items** below remain. |

---

## Open items (not blocking Phase 1)

Track during implementation; close in the owning phase or `docs/architecture.md`.

| ID | Topic | Current plan state | Recommended action |
|----|-------|------------------|-------------------|
| D1 | **`RequestEventService`** / ring buffer | WP4.6 endpoint listed; no named service or assembly owner | **Phase 4:** Add task under WP4.6 or `33pol.Observability` — `IRecentRequestStore` in-memory ring buffer |
| D2 | **`33pol.Persistence.Tests`** | Coverage target in `02-testing-strategy.md`; not in solution layout tree | **Phase 1:** Add to `01-solution-architecture.md` test projects list when scaffolding solution |
| D3 | Phase 3 unit checklist wording | Checklist says “Every `GatewayErrorCode`” but P4 codes are Phase 4 | **Phase 3 kickoff:** Align checklist with `06-sdk-error-catalog.md` (P3 rows + P4 rows separately) |
| D4 | WP order in `phase-5` | WP5.8 appears before WP5.7 in file | Renumber or reorder for readability (cosmetic) |

---

## v1 parity — open only

| v1 feature | Status | Linked item |
|------------|--------|-------------|
| Real-time request ring buffer | WP4.6 endpoint only; no service WP | D1 |

**Closed (v2 decision):** v1 PostgreSQL `logs` table / `LogsDb` — **not implemented.** Application logs via Serilog + OTel only; no `GET /admin/api/logs`.

---

## Recommended order before coding

1. **Start Phase 1** — solution scaffold; include `33pol.Persistence.Tests` (D2).  
2. **Phase 3** — align error-code test checklist with catalog phases (D3).  
3. **Phase 4** — ring buffer service for recent requests (D1).  
4. **Phase 5** — optional WP renumber (D4).  

---

## Changelog

| Date | Action |
|------|--------|
| 2026-05-26 | Initial review: 24 findings; remediation in plan docs |
| 2026-05-26 | Re-review: ready for Phase 1; deferred items documented |
| 2026-05-26 | Removed resolved issues; file tracks open items only |
| 2026-05-26 | Dropped PostgreSQL log persistence from v2 plan; removed D1 (logs table decision) |
