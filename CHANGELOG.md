# Changelog

All notable changes to this project are documented here. Version tags follow [SemVer](https://semver.org/) (`vMAJOR.MINOR.PATCH`).

## [Unreleased]

### Fixed — billing correctness

- Non-streaming responses larger than the capture buffer are now billed. The response tail is retained for non-streaming bodies too, and usage is recovered from it by fragment scan when the body outgrew the head. Previously any such response failed to parse and recorded **no usage at all** — which included essentially every batch embeddings response.
- `usage.daily` is now sent only by the scheduled end-of-day publisher, with the day's totals. It was also being fired inline on a tenant's first request of the day, reporting near-zero usage and consuming the shared dedup slot so the real end-of-day summary was never delivered.
- Webhook delivery is retried with backoff and hands its once-per-period dedup reservation back when it permanently fails. Delivery was previously a single un-retried attempt whose failure was logged and discarded, after the slot had already been consumed.
- Per-API-key usage totals are summed in memory. SQLite has no decimal type, so the previous server-side `SUM()` coerced every value to a double before adding.
- Dropped usage events no longer report as persisted, so a saturated usage queue can no longer leak budget reservations into phantom spend that hard-stops tenants.
- Per-request cost is stored at 10 decimal places instead of 6; small requests previously rounded to exactly zero.
- A rollup write failure now logs explicitly that the affected spend is in `billing_events` but not in `daily_usage_rollups`, and releases its reservations.
- Usage is attributed to the period it occurred in rather than the period it was drained in.

### Fixed — security

- Inference paths are matched exactly rather than by suffix. A prefixed path such as `/x/v1/chat/completions` was routable but matched no authorization policy, so key-role separation could be bypassed.
- Anonymous path matching is segment-anchored (`/metrics-internal` no longer counts as `/metrics`).
- A presented-but-invalid API key now returns 401 on public models and `GET /v1/models` instead of silently downgrading to anonymous access.
- The request body-size cap and drain check run before the middleware that buffers and parses the body, so an unauthenticated request is bounded before that work happens.
- Anonymous rate-limit, concurrency and quota buckets partition by client address instead of sharing one global bucket. Configure `UseForwardedHeaders` with known proxies when running behind one.
- The upstream env-var policy is enforced where the credential is read, closing the file and database ingestion paths, not only the admin API.
- Provider discovery validates the address each connection is actually opened to (closing a DNS-rebinding gap), fails closed on unresolvable hosts, and blocks `100.64.0.0/10`.
- Explicit `Deny` model grants now deny. They were previously inert.
- Upstream secrets and SQLite backups are written to owner-only directories; backups are pruned to the last 7.
- The admin console's **Change key** field writes to a draft instead of the live credential. Bound straight to the session key, every keystroke replaced the key the 2-second poll and the connection watchdog were using, so typing a replacement 401'd the working session — and abandoning the panel left the in-memory key truncated until a reload.

### Fixed — resilience

- Backend health probes send the model's upstream credential and probe `/v1/models` first, and treat 401/403 as reachable. An upstream requiring authentication was previously marked permanently unhealthy, returning 502 for every request to it. Probe status codes and errors are retained instead of collapsed into a fixed string.
- The circuit breaker trips on failure rate over a rolling window instead of requiring consecutive failures, so a backend failing intermittently is now caught.
- Past `MaxTrackedResilienceModels` the breaker degrades to one shared breaker rather than silently becoming a no-op.
- Rollup increments use a genuinely immediate SQLite transaction.
- Bulkhead rejection returns 429 with `Retry-After` rather than 502 `backend_error`.

### Changed — breaking

- **Webhook signature format.** `X-33pol-Signature` is now `t=<unix>,v1=<hmac-sha256 of "<unix>.<body>">` instead of a bare body HMAC, and `X-33pol-Timestamp` / `X-33pol-Event` are sent alongside. Receivers must be updated. The previous format signed only the body, so every delivery was replayable indefinitely.
- **Clearing tenant model grants requires `allowAllModels: true`.** An empty tenant list removes the tenant ceiling (allowing every registered model) rather than revoking access; the confirmation flag makes that deliberate.
- Client request headers are forwarded upstream from an allowlist (`Accept`, `User-Agent`, `OpenAI-Beta`, `OpenAI-Organization`, provider version headers). Previously none were forwarded at all.
- `GET /` reports `documentation.readme` instead of `documentation.implementationPlan`, and `documentation.architecture` now points at `docs/architecture.md`. Both previous paths pointed at planning documents that have been removed.
- **`GET /stats` now requires an Admin API key.** The snapshot carries per-model request and error counts, average latency and active stream counts — the model inventory and traffic profile — which the console gates behind an Admin key at `/admin/api/summary` but this endpoint served anonymously. Monitoring that scrapes `/stats` must send `X-API-Key`; probes needing only up/down should use `/health`, `/health/live` or `/health/ready`, which stay anonymous, and `/metrics` is unchanged.

### Added

- **Billing reconciliation.** A background sweep compares the `billing_events` ledger against the `daily_usage_rollups` derived from it, reporting divergence to logs and to `gateway_billing_reconciliation_discrepancies` / `_cost_drift` / `_runs_total`, with Prometheus alerts for both drift and the sweep stalling. Everything an operator reads comes from the rollups while the ledger is what records a request, so every defect between the two — several of which are fixed above — produced plausible wrong numbers and no error. The sweep reports and never repairs: a discrepancy means one side is wrong and the job cannot tell which. Configured by `Billing:ReconciliationEnabled` (default on), `ReconciliationIntervalMinutes` (60) and `ReconciliationLookbackDays` (3, ending yesterday UTC, clamped inside retention).
- API keys support an optional `expiresAt` on create and update. The expiry column and `expired_api_key` error existed but were unreachable.
- `Gateway:Resilience:ShutdownDrainSeconds` keeps the gateway serving after readiness reports unhealthy, so load balancers can deregister before Kestrel stops. Defaults to 0; the Helm chart sets 15.
- `Gateway:Resilience:CircuitBreakerSamplingWindowSeconds` and `CircuitBreakerFailureRatioThreshold`.
- `Billing:BudgetSpendCacheTtlSeconds` caches persisted period spend off the inference hot path (in-flight cost is still tracked exactly by the reservation ledger, so hard stops cannot overshoot).
- Startup verification that stored upstream credentials decrypt with the configured pepper, so a rotated pepper is reported at boot rather than as opaque per-request failures.
- **Durable admin audit trail.** `FileAuditLogger` appends one JSON Lines record per admin mutation — key create/update/revoke, model and tenant grants, CORS, rate limits, config reload, database backup, upstream-secret lifecycle — alongside the structured log event that was previously the only record. Configured by `Gateway:Security:AuditLogPath` (default `config/audit-log.jsonl`, same writable volume as `models.json`) and `AuditLogMaxBytes` (8 MB, rolls to `.1`). Retention previously depended entirely on the deployed Serilog configuration, which ships a console sink and nothing else, and the console's Logs tab is an in-memory diagnostics ring rather than an audit trail. A write failure is warned once and never fails the admin action that produced it.

## [2.0.0] — 2026-05-28

### Added

- Multi-project gateway (Phases 1–5): OpenAI-compatible proxy, embedded SQLite persistence, rate limits, quotas, FinOps, operator admin UI.
- Docker Compose stack, Helm chart, host install script (`install-33pol.sh`), Prometheus/Grafana provisioning.
- CI: Release build, coverage gate, k6 smoke; GHCR images; tag-gated GitHub Releases with gateway tarball.

### Security

- Operator registry and upstream secrets are gitignored; see [docs/security.md](docs/security.md).

### Notes

- Verified on local Docker Compose (E2E + k6). Sustained-load validation against a production-like upstream is still recommended before a capacity commitment — see [perf/README.md](perf/README.md).
