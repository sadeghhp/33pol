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
| G-10 | #528 US-post-ga-01 Redis rate limits | Tasks #555–#557. Older duplicate **#521** — close or merge |
| G-11 | #537 US-post-ga-02 multi-replica HA | #572–#573 |
| G-12 | #538 US-post-ga-03 SSE admin stream | Duplicate **#522** |
| G-13 | #539 US-post-ga-04 durable audit | Duplicate **#523** |
| G-14 | #540 US-post-ga-05 Stripe | Duplicate **#524** |
| G-15 | #541 US-post-ga-06 Prometheus SLO | Duplicate **#525** |
| G-16 | #542 US-post-ga-07 chaos runbook | #581 |
| G-17 | #543 US-post-ga-08 OpenAPI prod | #582 |

### Admin UI enhancements

Story: **#613 US-admin-enhance** — UX, navigation, usage events, `admin-store.js`, docs. Tasks: create/close in Taiga when MCP sync works (8 tasks: UX, navigation, usage, dashboard, models/backends, keys modal, modular JS, tests/docs).

### Quality / hygiene epic (G-18–G-23)

Epic: **EPIC-quality-hygiene** (358084).

| Gap | Story |
|-----|-------|
| G-18 | #544 US-hygiene-01 CI coverage gates |
| G-19 | #545 US-hygiene-02 BenchmarkDotNet (optional) |
| G-20 | #546 US-hygiene-03 Playwright E2E (optional) |
| G-21 | #547 US-hygiene-04 usage retention |
| G-22 | #548 US-hygiene-05 RequestTracker cleanup |
| G-23 | #549 US-hygiene-06 doc sync after GA |
