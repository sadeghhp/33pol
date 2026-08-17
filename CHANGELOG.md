# Changelog

All notable changes to this project are documented here. Version tags follow [SemVer](https://semver.org/) (`vMAJOR.MINOR.PATCH`).

## [Unreleased]

### Changed — throughput and admission control

Investigation of "the gateway serves requests one at a time" reports. Measured on this branch with a deliberately concurrent slow mock (`perf/scripts/concurrent-mock-upstream.py`, 2 s per request): 64 simultaneous requests through the gateway complete in 2.05 s wall-clock, streaming and non-streaming alike, with the upstream observing all 64 at once — the request path itself does not serialize. What *did* make a busy gateway look serial were the admission limits and how rejections were reported:

- **Per-model bulkhead default raised from 64 to 256** (vLLM's own `--max-num-seqs` default) and given a **bounded wait queue** — `Gateway:Resilience:MaxQueuedForwardsPerModel` (256 in the shipped config, 0 = old refuse-immediately behaviour) with `BulkheadQueueTimeoutSeconds` (30). A burst above the ceiling now waits briefly for a slot instead of being answered 429 and left to the client SDK's exponential-backoff retry loop, which was slower for the caller and added load for everyone else. Queue depth is exported as `gateway_bulkhead_queued{model}`; a persistently non-zero value means the model server (or the ceiling) is the bottleneck.
- **Request-rate limiting is a token bucket, not a fixed per-minute window.** Capacity is still `Rpm + Burst` and a fresh partition still admits that as an instantaneous burst, but tokens refill continuously at `Rpm`/minute. Under the fixed window a partition that burst past its limit was told `Retry-After: <up to 59 s>` — and OpenAI-compatible SDKs honour that header by sleeping — so every further call from that tenant hung until the top of the next minute, which read as the gateway queueing them one by one. `Retry-After` is now `ceil(60 / Rpm)`, in practice 1 second.
- **`RateLimiting:Enabled=false` is honoured without a database.** The initial config snapshot copied every rate-limit field except `Enabled`, so a database-less gateway (and the window before the first DB load) enforced limits regardless.
- **Upstream connection pool configured explicitly** (`Gateway:Resilience:Upstream*`): connect timeout 10 s (was infinite — a down backend consumed the whole header allowance per request), pooled-connection lifetime 15 min / idle 5 min (was a full handler rotation every 2 min plus a 60 s idle reap, i.e. a burst of new TCP connections to the model server every two minutes under steady load), `MaxConnectionsPerServer` unlimited unless opted in, multiple HTTP/2 connections enabled.
- **Shipped default rate-limit tier raised** from 600 rpm / 100 burst / 50 concurrent streams to 3000 / 500 / 256 (streams now match the bulkhead). The default tier is applied per *partition*, and every API key issued from the admin console shares the operator tenant's partition, so the old numbers capped the whole deployment at 50 simultaneous streams and ~10 requests/s regardless of GPU. **Existing databases keep the values seeded on their first boot** — raise them under Admin → Rate limits; editing `appsettings.json` on an installed gateway changes nothing.
- **Startup logs the effective admission ceilings** — bulkhead, queue, rate-limit tier and where it was read from — and warns when the default tier's `MaxConcurrentStreams` is below the bulkhead, because every API key issued from the admin console shares one tenant partition and that number, not the GPU, then caps the whole deployment's simultaneous streams.

### Changed — hot-path efficiency

- Console logging is asynchronous (`Serilog.Sinks.Async`, bounded, drop-when-full). The console sink was writing inline on the request thread under one lock per completed request, so a slow log consumer stalled request completion.
- The in-flight request table no longer calls `ConcurrentDictionary.Count` per forwarded request; that call takes every internal lock of the dictionary and had become a global barrier between concurrent requests.
- `GatewayDbContext` is pooled. A context was built for every authenticated inference request — API-key validator, grant service and budget check all sit behind scoped repositories — even when all three answered from cache.
- The model-grant service is a singleton that opens a scope only on a cache miss, and coalesces concurrent misses for the same key into one query instead of a burst at every TTL expiry.
- Unknown API keys are remembered for 30 s in a bounded negative cache. Every request with an unrecognised key ran a database lookup — the normal case for `publicAccess` models, whose SDK callers must send *some* placeholder key.
- Billing events are inserted one batch per transaction (one existence probe, one `SaveChanges`) instead of one round trip and one WAL commit per event; the usage writer drained at ~900 events/s before, so this is capacity headroom rather than a fix for a current stall.
- The two latency histograms (`gateway_inference_duration_seconds`, `gateway_time_to_first_token_seconds`) get seconds-shaped bucket boundaries; the SDK defaults were millisecond-shaped and put every inference between 0.5 s and 60 s in two buckets, so `histogram_quantile()` could not distinguish a slow model from a queueing gateway.

### Added — perf tooling

- `perf/scripts/concurrent-mock-upstream.py`: asyncio mock backend with configurable latency that serves any number of requests at once and reports its peak observed concurrency at `/__stats`.
- `perf/scripts/concurrency-bench.py`: standard-library script that fires N requests simultaneously and prints whether the path is parallel or serialized, and which admission limit produced any 429s. Run it against the gateway and against the model server directly to attribute the bottleneck.
- `perf/scripts/mock-upstream.py` is now multi-threaded; the single-threaded server it used measured its own serialization and attributed it to the gateway.
- `perf/reports/concurrency-2026-08-16.md`: the measurements behind the changes above.

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

- Gateway-wide control-plane endpoints (model registry, upstream credentials, providers, CORS, rate limits, config reload, backups, `/stats`, and the cross-tenant request/log feeds) now require an Admin key belonging to the **operator tenant** (`Gateway:Security:OperatorTenantSlug`, defaulting to the bootstrap tenant) instead of any tenant's Admin key. The Admin role is per-tenant and any tenant admin can mint further admin keys for its own tenant, so role-only gating handed every tenant's admin the whole gateway. Per-tenant admin surfaces (own keys, grants, usage) are unchanged; single-tenant deployments are unaffected.
- A rejected duplicate "add model" no longer destroys the existing model's upstream credential. The secret store is keyed by model id, so the pre-add secret write was overwriting (or, with `clearApiKey`, deleting) the live credential of the model the add then collided with — and rollback deleted it outright. Rollback now restores the prior secret.
- Proxied upstream 5xx responses are recorded as backend failures for the circuit breaker, metrics, and the recent-requests feed. They previously counted as successes, so a backend that degraded into fast 500s closed a half-open breaker on its first probe and could never re-open it. 4xx responses still count as answered.
- Anonymous usage is committed to the same per-address quota partition the admission check reads. It previously accrued under a literal `anonymous` bucket that no check consulted, so keyless callers of `publicAccess` models were never held to the monthly token quota.
- Usage events accepted before shutdown are no longer lost: the batch persistence handler stops before the usage recorder drains its queue (reverse registration order), so the drain's final partial batch sat in a buffer with no flush loop. The recorder now explicitly flushes the handler after draining.
- Inference paths are matched exactly rather than by suffix. A prefixed path such as `/x/v1/chat/completions` was routable but matched no authorization policy, so key-role separation could be bypassed.
- Anonymous path matching is segment-anchored (`/metrics-internal` no longer counts as `/metrics`).
- A key the gateway recognises but will not honour — revoked, expired, or belonging to a deactivated tenant — now returns 401 on public models and `GET /v1/models` instead of silently downgrading to anonymous access. A key matching no stored record is still treated as no key at all on those routes: OpenAI-compatible SDKs refuse an empty `api_key`, so rejecting placeholder tokens left `publicAccess` reachable only by bare `curl`.
- The request body-size cap and drain check run before the middleware that buffers and parses the body, so an unauthenticated request is bounded before that work happens.
- Anonymous rate-limit, concurrency and quota buckets partition by client address instead of sharing one global bucket. Behind a proxy this needs the new `Gateway:ForwardedHeaders` section — off by default, since trusting `X-Forwarded-For` from an untrusted peer would let a caller mint unlimited partitions. Without it every anonymous caller shares the proxy's address, which is the shared bucket it was meant to remove.
- The upstream env-var policy is enforced where the credential is read, closing the file and database ingestion paths, not only the admin API.
- Provider discovery validates the address each connection is actually opened to (closing a DNS-rebinding gap), fails closed on unresolvable hosts, and blocks `100.64.0.0/10`.
- Explicit `Deny` model grants now deny. They were previously inert.
- Upstream secrets and SQLite backups are written to owner-only directories; backups are pruned to the last 7.
- The admin console's **Change key** field writes to a draft instead of the live credential. Bound straight to the session key, every keystroke replaced the key the 2-second poll and the connection watchdog were using, so typing a replacement 401'd the working session — and abandoning the panel left the in-memory key truncated until a reload.

### Fixed — admin console telemetry

- **A request in progress is now visible while it runs.** Every dashboard counter and every live-feed row was written at completion, and the only start-time signal — active streams — was recorded for streaming requests alone. A non-streaming completion or embedding taking a minute therefore moved nothing at all: the console reported an idle gateway for the whole call. `summary.activeRequests` and `activeRequestsPerModel` now count everything being forwarded (streams are the subset), the feed publishes an in-flight row when forwarding starts and retires it on every exit path, and Overview shows an **In flight** card, a top-bar chip, **Running now** chips per model, and tinted rows whose duration grows between polls. In-flight entries are in-memory only and never reach the durable stats snapshot. Exported as `gateway_active_requests`.
- **The "Recent requests" live tail actually refreshes.** The 2-second poll fetched only `/admin/api/summary`, so the feed was frozen until the tab was re-activated or **Refresh** clicked — while the page and the documentation both claimed it was live. The poll now refreshes the feed on Overview, and stops entirely while the admin key is rejected instead of retrying a 401 every two seconds into the audit trail.
- **Requests rejected at admission reach the dashboard.** An unhealthy backend, an open circuit, a full bulkhead or an exhausted stream slot answered the client with a 429/502/503 but reported only to Prometheus, so a saturated gateway turning every request away still rendered as "0 errors, 0.00% error rate". All four now count toward requests, errors and **Errors by model** and appear in the feed; the stream-slot case also counts as a rate-limit rejection, which the **Rate-limited** stat previously omitted. They contribute no latency — admission takes microseconds and would drag the mean toward zero.
- **A proxied upstream 4xx counts as an error on the dashboard**, while still not counting as a circuit-breaker failure. The two questions shared one outcome flag, so a model rejecting every call with 400 reported no errors beside a feed full of red rows. Recorded as `upstream_4xx`.
- Feed rows carry the status the gateway actually set. Admission rejections were reported as a generic 502 because the status was read only once the response had flushed.
- Overview and **Usage & cost** state which population they count: the former is gateway-wide across all tenants, the latter is tenant-scoped and derived from persisted billing events, so their request totals legitimately differ on a multi-tenant gateway.

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
