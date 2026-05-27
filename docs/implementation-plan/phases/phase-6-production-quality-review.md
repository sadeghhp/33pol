# Phase 6 — Production Quality Review & Remediation

**Epic:** `EPIC-P6-quality-review` (Taiga id **358106**; consolidates **EPIC-quality-hygiene** G-18–G-23)  
**Duration (guide):** 2–4 weeks (audit 1–2 weeks + remediation waves)  
**Prerequisite:** Phase 5 **code complete** in repo (GA ops G-01–G-06 may run in parallel)  
**Blocks:** Production confidence sign-off beyond GA checklist  

---

## Objective

Systematically **review all `src/` code** and normative plan/docs to find correctness bugs, security gaps, performance bottlenecks, code smells, duplication, and test/doc drift. **Remediate** findings in priority waves; only **P0** findings may delay GA approvals.

Runs **in parallel** with P5 staging k6, soak, SDK smoke, and checklist approvals — see [implementation-plan-gap-report.md](../../implementation-plan-gap-report.md) G-01–G-06.

---

## Outcomes

- Per-assembly review checklists completed ([17-phase6-review-rubric.md](../17-phase6-review-rubric.md))  
- Living findings register ([16-phase6-findings.md](../16-phase6-findings.md)) with severity triage  
- Traceability matrix: feature → code → test → doc  
- Zero **Open P0** findings (or explicit GA waiver) before Phase 6 close  
- P1 fixes merged or documented risk; P2 transferred to post-GA backlog  
- Refreshed gap report and doc sync (G-23)  

---

## Severity triage

| Level | Definition | GA impact |
|-------|------------|-----------|
| **P0** | Correctness, security, data loss, silent mis-routing, unbounded resources | Blocks GA until fixed or waived |
| **P1** | Performance, missing metrics, ops-affecting test/doc gaps | Fix before prod; GA may proceed with documented risk |
| **P2** | Style, optional benchmarks, cosmetic UX | Post-GA backlog |

---

## Work packages

### WP6.1 — Audit kickoff & traceability matrix

| Task | Details |
|------|---------|
| Baseline | `dotnet test 33pol.sln -c Release`, `build/check-coverage.sh`, `dotnet list package --vulnerable` |
| Traceability | Map normative docs 06, 08–13 + feature matrix → code paths → tests |
| Gap refresh | Update [implementation-plan-gap-report.md](../../implementation-plan-gap-report.md) |
| Output | Traceability section in [16-phase6-findings.md](../16-phase6-findings.md) |

### WP6.2 — Per-assembly static review

Review each project using [17-phase6-review-rubric.md](../17-phase6-review-rubric.md):

| Assembly | Normative refs |
|----------|----------------|
| `33pol.Core` | 06, 01 |
| `33pol.Registry` | 13, 09 |
| `33pol.Proxy` | 09, 03 |
| `33pol.Security` | 10, security.md |
| `33pol.Policy` | 10, 12 |
| `33pol.Observability` | 12, 08 |
| `33pol.Billing` | 01, finops.md |
| `33pol.Persistence` | 10 |
| `33pol.Api` | 08, 13 |
| `33pol.App` | 01 |
| `33pol.OperatorConsole` | 08 |
| `wwwroot/admin` | admin-ui.md |

### WP6.3 — Data plane performance & correctness

| Check | Method |
|-------|--------|
| SSE not fully buffered | Code review + integration tests |
| Model JSON rewrite | Conformance + unit matrix |
| Registry swap under load | Doc 13 R5; concurrent read tests |
| Saturation | k6 + `dotnet-counters` |
| Gateway overhead | `perf/scripts/overhead-compare.js` |

### WP6.4 — Security & reliability (code paths)

Beyond WP5.7 dependency/OWASP doc pass:

- Admin vs inference on every `/admin/api/*`  
- Secrets in logs/enrichers  
- TLS upstream, circuit breaker + bulkhead, graceful shutdown  
- CORS, body size limits  

### WP6.5 — Observability contract audit

Verify against [12-metrics-and-runtime-contracts.md](../12-metrics-and-runtime-contracts.md):

- Metric names, labels, buckets  
- Grafana dashboards vs emitted metrics  
- `perf/ci/verify-observability-local.sh`, `verify-grafana-dashboards.sh`  

### WP6.6 — Admin UI / UX review

Navigation, errors, polling cost, API parity, basic accessibility. Gaps feed P1/P2 tasks (see US-admin-enhance / US-P5-10 in post-GA backlog).

### WP6.7 — Duplication, smells, architecture

NetArchTest, manual duplication scan, analyzer warnings, streaming `IDisposable`/`async` patterns.

### WP6.8 — Test & coverage gap analysis

Extend [02-testing-strategy.md](../02-testing-strategy.md): gated assemblies (G-18), integration negatives, error golden completeness.

### WP6.9 — Documentation sync

README, observability, integrations, admin-ui; six-phase wording; HA limitations; chaos runbook (G-16).

### WP6.10 — Remediation waves

1. **Wave A (P0)** — correctness/security/hot path  
2. **Wave B (P1)** — performance, metrics, coverage, docs  
3. **Wave C (P2)** — hygiene, BenchmarkDotNet, Playwright, cosmetic UI  

### WP6.11 — Phase 6 exit criteria

**Audit complete** when all assemblies reviewed and zero Open P0 (or waiver).

**Phase closed** when committed P0 + agreed P1 closed in Taiga; P2 in post-GA backlog.

---

## Phase 6 exit checklist

- [x] All rubric checklists signed in `17-phase6-review-rubric.md` (2026-05-27)  
- [x] `16-phase6-findings.md`: zero Open P0  
- [x] `dotnet test` green; coverage policy documented (G-18 / F-P6-018)  
- [x] Gap report refreshed  
- [ ] Epic `EPIC-P6-quality-review` Done in Taiga (close #632–#636 after sprint sign-off)  

---

## What Phase 6 does not include

- Staging k6/soak execution (P5 G-01–G-02)  
- New product features (Redis G-10, SSE G-12, Stripe G-14) unless elevated to P0  
- Replacing manual GA approvals (G-05)  

---

## Taiga mapping

| Milestone | Focus |
|-----------|--------|
| `P6-Sprint-1-audit` | WP6.1–6.9 |
| `P6-Sprint-2-remediation-P0` | WP6.10 Wave A |
| `P6-Sprint-3-remediation-P1` | WP6.10 Wave B |

### User stories (Taiga 2026-05-27)

| Ref | Story | WP | Sprint |
|-----|-------|-----|--------|
| #632 | US-P6-01: Traceability & gap refresh | 6.1 | P6-Sprint-1-audit (521199) |
| #633 | US-P6-02: Core + Registry review | 6.2 | 521199 |
| #634 | US-P6-03: Proxy data plane review | 6.2, 6.3 | 521199 |
| #637 | US-P6-04: Security + Policy review | 6.2, 6.4 | 521199 |
| #639 | US-P6-05: Observability + Billing review | 6.2, 6.5 | 521199 |
| #638 | US-P6-06: Persistence + Api + App host | 6.2 | 521199 |
| #640 | US-P6-07: Operator console + Admin UI | 6.2, 6.6 | 521199 |
| #641 | US-P6-08: Performance profiling & k6 deep dive | 6.3 | 521199 |
| #642 | US-P6-09: Duplication & architecture smells | 6.7 | 521199 |
| #643 | US-P6-10: Test & coverage expansion | 6.8 | 521199 |
| #644 | US-P6-11: Documentation sync | 6.9 | 521199 |
| #645 | US-P6-12: Remediation wave A (P0) | 6.10 | 521200 |
| #635 | US-P6-13: Remediation wave B (P1) | 6.10 | 521200 |
| #636 | US-P6-14: Phase 6 sign-off | 6.11 | 521201 |

Link stories to epic **358106** in Taiga UI if MCP `linkStoryToEpic` lacks story internal ids.

---

## Related documents

- [16-phase6-findings.md](../16-phase6-findings.md) — findings register  
- [17-phase6-review-rubric.md](../17-phase6-review-rubric.md) — per-assembly checklist  
- [07-review-findings.md](../07-review-findings.md) — **plan** review (not code)  
- [post-ga-backlog.md](../../post-ga-backlog.md) — G-10–G-17 features; G-18–G-23 → P6  
