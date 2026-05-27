# Phase 6 — Findings Register

**Last updated:** 2026-05-27  
**Epic:** `EPIC-P6-quality-review`  
**Baseline:** `dotnet test 33pol.sln -c Release` — all passed; coverage gates OK for 6 assemblies

**Severity:** **P0** blocks GA | **P1** fix before prod / document risk | **P2** post-GA

**Status:** `Open` | `In progress` | `Verified` | `Closed` | `Waived`

---

## Traceability matrix (sample — normative → code → test)

| Feature | Normative doc | Code | Test |
|---------|---------------|------|------|
| OpenAI chat forward + SSE | 09 | `ModelRouterMiddleware`, `StreamingHttpTransformer` | Integration + Conformance |
| Live registry CRUD | 13 | `ModelRegistryWriter`, admin endpoints | Integration |
| API key auth | 10 | `ApiKeyValidator`, middleware | Security + Integration |
| Rate limit 429 | 12 | `RateLimitMiddleware` | Integration |
| Usage batch writer | 05 WP5.2 | `ChannelUsageRecorder` | Observability tests |
| Error codes | 06 | `GatewayErrorCode`, `ErrorResponseWriter` | Conformance goldens |
| Circuit breaker metrics | 12 | `GatewayCircuitBreakerMetricsExporter` | Unit tests |
| Provider models discovery | admin-ui | `AdminProviderEndpoints` | Integration |

Full matrix: extend per feature row in [05-feature-to-phase-matrix.md](./05-feature-to-phase-matrix.md) during ongoing P6 reviews.

---

## Findings

| ID | Sev | Assembly | Finding | Status | Action |
|----|-----|----------|---------|--------|--------|
| F-P6-010 | P1 | Policy / HA | Per-pod in-memory rate limits; unfair RPM under multi-replica | Closed | Documented in [integrations.md](../integrations.md), [11-ha-and-scaling.md](./11-ha-and-scaling.md); fix in post-GA G-10 |
| F-P6-012 | P2 | Feature | SSE `GET /admin/api/events/stream` not implemented | Open | Post-GA G-12; UI polls summary |
| F-P6-013 | P2 | Security | Durable audit retention/export | Open | Post-GA G-13; `NoOpAuditLogger` + Serilog |
| F-P6-015 | P1 | Api / Security | Admin endpoints lacked explicit `RequireAuthorization`; `UseAuthorization` skipped without DB | Closed | `RequireAuthorization(Admin)` on keys/config; `UseGatewaySecurity` always registers auth middleware |
| F-P6-016 | P2 | Docs | Chaos runbook missing | Open | Post-GA G-16 |
| F-P6-017 | P2 | Api | OpenAPI control plane Development-only | Open | Post-GA G-17 |
| F-P6-018 | P1 | Testing | CI coverage omits Core, Api, Persistence, App, Console | Closed | Documented in [02-testing-strategy.md](./02-testing-strategy.md) § Coverage gates |
| F-P6-019 | P2 | Testing | BenchmarkDotNet `perf/benchmarks` absent | Open | G-19 optional |
| F-P6-020 | P2 | Testing | Playwright admin E2E absent | Open | G-20; manual checklist OK |
| F-P6-021 | P2 | FinOps | Usage retention background job doc-only | Open | G-21 |
| F-P6-022 | P1 | Proxy | Dead `RequestTracker` NoOp stub in Proxy | Closed | File removed; DI uses `GatewayRequestTracker` only |
| F-P6-023 | P2 | Docs | Plan README said “five phases” only | Closed | Six-phase wording in README + 04-phase-overview |
| F-P6-024 | P1 | Docs | Metric doc 12 lists circuit breaker as post-GA but code ships gauge | Closed | Doc 12 updated to Phase 4/5 implemented |
| F-P6-025 | P2 | Observability | Local `verify-observability-local.sh` requires running Compose | Open | P5 G-06 staging; script documented |
| F-P6-026 | P0 | Proxy | `InferenceHttpForwarder` buffered full SSE body (`ResponseContentRead`); no incremental TTFT | Closed | US-P6-15 #656: `ResponseHeadersRead` + flush pipe; `DelayedChunkStreamingHandler` integration test; HttpClient timeout = `ForwardTimeoutSeconds` |

---

## Open P0 summary

**None.** (2026-05-27 audit; F-P6-026 closed 2026-05-27)

---

## Remediation waves

| Wave | IDs | Status |
|------|-----|--------|
| A (P0) | — | Complete (no P0) |
| B (P1) | F-P6-010, F-P6-015, F-P6-018, F-P6-022, F-P6-024 | Complete |
| C (P2) | F-P6-012, 013, 016–017, 019–021, 025 | Transferred to [post-ga-backlog.md](../post-ga-backlog.md) |

---

## Baseline commands (WP6.1)

```bash
dotnet test 33pol.sln -c Release
dotnet test 33pol.sln -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
build/check-coverage.sh TestResults
dotnet list 33pol.sln package --vulnerable
```

**Coverage (2026-05-27):** Registry 91.8%, Proxy 90.3%, Security 90.0%, Policy 93.4%, Observability 94.1%, Billing 98.9% — all thresholds met.

---

## Taiga sync (2026-05-27)

| Ref | Internal ID | Status | Sprint | Epic |
|-----|-------------|--------|--------|------|
| #632–#644 | 9292058–9292077 | Done | 521199 | 358106 |
| #645, #635 | 9292078, 9292068 | Done | 521200 | 358106 |
| #636 | 9292069 | Done | 521201 | 358106 |
| #544, #548, #549 | 9291730, 9291734, 9291735 | Done | 521199 | 358106 + 358084 |
| Tasks #583, #587, #588 | 9087255, 9087259, 9087260 | **Closed** | 521199 | Hygiene child tasks (were New; closed 2026-05-27 after P6 audit) |
| #545–#547 | 9291731–9291733 | **New** (P2) | — | 358084 only |
| Tasks #584–#586 | 9087256–9087258 | **New** (P2) | — | Post-GA G-19–G-21; not in audit sprint |
| #251 | 9289800 | Done | — | P4 (usage metering closed) |

Hygiene **#545–#547** (BenchmarkDotNet, Playwright, retention job) remain **New** — correct for post-GA P2.

---

## Changelog

| Date | Action |
|------|--------|
| 2026-05-27 | Phase 6 kickoff; rubric signed; findings seeded from gap report + assembly audit |
| 2026-05-27 | Wave B: removed Proxy `RequestTracker`, admin endpoint auth attributes, doc updates |
| 2026-05-27 | Taiga board synced: US-P6-01…14 Done, epic 358106 linked, sprints assigned |
| 2026-05-27 | Epic 358106 → Done; sprints 521199–521201 closed; sign-off comment on #636 |
| 2026-05-27 | Hygiene tasks #583/#587/#588 Closed; P6 sprints 521199–521201 closed; epic #631 Done |
| 2026-05-27 | F-P6-026 closed: true SSE forwarding (US-P6-15 #656, issue #663) |
