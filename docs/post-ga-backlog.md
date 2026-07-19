# Post-GA backlog

Items deferred from GA sign-off (audit 2026-05-27). See gap IDs in [implementation-plan-gap-report.md](./implementation-plan-gap-report.md).

## Taiga (project **sadeghhp-33pol**)

### GA sign-off sprint (blockers G-01–G-06, recommended G-07–G-09)

Sprint: **P5-Sprint-GA-signoff** (id 521193). Epic: **EPIC-P5-finops-ga** (357973).

| Gap | Story | Tasks (examples) |
|-----|-------|------------------|
| G-01 | #527 US-GA-01 staging k6 | #551–#554 |
| G-02 | #529 US-GA-02 4h soak | #558–#559 |
| G-03 | #530 US-GA-03 SDK smoke | #560–#561 |
| G-04 | #531 US-GA-04 Compose E2E | #562–#563 |
| G-05 | #532 US-GA-05 approvals + close P5 epic | #564–#566 |
| G-06 | #533 US-GA-06 OTel staging | #567–#568 |
| G-07 | #534 US-GA-07 registry poll | #569 |
| G-08 | #535 US-GA-08 FinOps export | #570 |
| G-09 | #536 US-GA-09 operator console | #571 |

Guide: [ga-signoff.md](./ga-signoff.md). Delete duplicate story **#550** (accidental re-create of US-GA-01).

### Post-GA epic (G-10–G-17)

Epic: **EPIC-post-ga** (358079).

| Gap | Story | Notes |
|-----|-------|-------|
| G-10 | #528 US-post-ga-01 Redis rate limits | **Superseded** — single-instance SQLite is a locked decision; no distributed rate-limit store. Close. |
| G-11 | #537 US-post-ga-02 multi-replica HA | **Superseded** — the gateway runs single-instance on embedded SQLite (Helm rejects >1 replica). Durability comes from backups, not replicas. Close. |
| G-12 | #538 US-post-ga-03 SSE admin stream | Duplicate **#522** |
| G-13 | #539 US-post-ga-04 durable audit | Duplicate **#523** |
| G-14 | #540 US-post-ga-05 Stripe | Duplicate **#524** |
| G-15 | #541 US-post-ga-06 Prometheus SLO | Duplicate **#525** |
| G-16 | #542 US-post-ga-07 chaos runbook | #581 |
| G-17 | #543 US-post-ga-08 OpenAPI prod | #582 |

### Admin UI enhancements

**US-P5-10 — error UX, env var validation, provider models POST** (2026-05-27)

| Item | Taiga | Notes |
|------|-------|--------|
| User story | **#624** `US-P5-10: Admin error UX, env var validation, provider models POST` | Created in backlog (verify in Taiga UI). Older dupes **#621–#623** may exist — close if empty. |
| Tasks (implementation order) | **#625–#630** on **#503** | MCP could not attach tasks to #624 without internal story id; **move tasks to #624** in Taiga when convenient. |
| Prior umbrella | **#613 US-admin-enhance** | Broader UX/navigation; US-P5-10 is a focused slice for provider discovery reliability. |

**Problem:** Operators paste API secrets into the “API key env var” field → `GET …/models?envVar=sk-…` → 400; errors only show in the top API-key card (far from Fetch models).

**Acceptance:** Visible errors on Models tab; client + server reject secret-like env var names; POST discovery (no secrets in URL); `dotnet test` green.

| Task | Ref | Layer |
|------|-----|--------|
| EnvVarNameValidator | #625 | Core + unit tests |
| POST provider models API | #626 | Api + integration tests |
| Global toast + ProblemDetails | #627 | admin-store, css, html |
| Inline provider errors + runApi | #628 | admin.js, html |
| Client validation + POST fetch | #629 | admin.js |
| docs/admin-ui.md | #630 | Docs |

**Implement in order:** #625 → #626 → #627 → #628 → #629 → #630 (one task In progress at a time per Taiga workflow).

**Status (2026-05-27):** Implemented in repo; `dotnet test` green. Story comment on #503 documents completion — **manually close tasks #625–#630** in Taiga (MCP has no `updateTask`; internal task IDs not exposed).

Story: **#613 US-admin-enhance** — UX, navigation, usage events, `admin-store.js`, docs (broader; separate from US-P5-10).

### Phase 6 — quality review (G-18–G-23 absorbed)

Epic: **EPIC-P6-quality-review** (see [phase-6-production-quality-review.md](./implementation-plan/phases/phase-6-production-quality-review.md)). Supersedes **EPIC-quality-hygiene** (358084) for tracking.

| Gap | Story | P6 status |
|-----|-------|-----------|
| G-18 | #544 US-hygiene-01 CI coverage gates | Documented exclusion (F-P6-018); expand gates = P2 |
| G-19 | #545 US-hygiene-02 BenchmarkDotNet (optional) | P2 open |
| G-20 | #546 US-hygiene-03 Playwright E2E (optional) | P2 open |
| G-21 | #547 US-hygiene-04 usage retention | P2 open |
| G-22 | #548 US-hygiene-05 RequestTracker cleanup | **Closed** in P6 (file removed) |
| G-23 | #549 US-hygiene-06 doc sync after GA | **Closed** — six-phase docs, findings register |

Findings: [16-phase6-findings.md](./implementation-plan/16-phase6-findings.md).

**Taiga (synced 2026-05-27):** US-P6-01…14 **#632–#645** → **Done** on epic **358106** (#631). Hygiene **#544, #548, #549** → **Done**; **#545–#547** remain **New** (P2). Sprints **521199** / **521200** / **521201**.
