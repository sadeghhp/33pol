# Post-GA backlog

Items deferred from GA sign-off (audit 2026-05-27). See gap IDs in [implementation-plan-gap-report.md](./implementation-plan-gap-report.md).

## Taiga

| Item | Ref | Epic |
|------|-----|------|
| Redis `IDistributedRateLimitStore` | #521 US-post-ga-01 (G-10) | EPIC-post-ga (id 358079) |
| SSE admin event stream | #522 (G-12) | EPIC-post-ga |
| Durable audit logger | #523 (G-13) | EPIC-post-ga |
| Stripe adapter | #524 (G-14) | EPIC-post-ga |
| Prometheus recording rules / SLO | #525 (G-15) | EPIC-post-ga |

Project: **sadeghhp-33pol**. Link stories to epic manually in Taiga if MCP `linkStoryToEpic` did not apply.

## Additional gaps (no Taiga story yet)

| ID | Item |
|----|------|
| G-11 | Multi-replica HA ops (shared registry volume, per-pod limits doc) |
| G-16 | Chaos engineering runbook |
| G-17 | OpenAPI control plane publish (non-Development) |
| G-18 | Extend CI coverage gates (Persistence, Core, Api, Console) |
