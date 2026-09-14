# 33pol Gateway — Remediation Plan, 2026-09-13

Follow-up to [the technical audit](audit-2026-09-13.md). Every finding below was re-verified
against the working checkout before being planned; the audit's filenames, line numbers, severities
and inferred causes were treated as claims to test, not as facts.

| | |
|---|---|
| **Checkout** | `main` @ `afeb6e0` |
| **Method** | Live boot · targeted executable probes · test runs |
| **Status** | Plan only — no source file was modified |
| **Outcome** | 4 confirmed · 3 partially confirmed · 4 disproved · 3 audit claims materially corrected |
| **Batches** | 7 PR-sized changes; 2 must deploy atomically |

Severity is `S0` blocker → `S4` low, carried over from the audit. Status is **Confirmed**,
**Partially confirmed**, **Disproved** or **Unable to verify**, and is claimed only where a
command, probe or live request demonstrates it.

---

## 1. Executive assessment

### Confirmed highest-risk

**GW-01 is real and worse than reported.** A Production host started with the shipped
`appsettings.json` — which ships `"GatewayDb": ""` — serves the entire admin control plane to
unauthenticated callers, including state-changing routes. The same early `return` that skips the
authentication initializer also skips `ValidateOnStart()` for `GatewaySecurityOptions`, so the
key-pepper validator that is supposed to refuse the published default *never runs in the one
configuration where it matters*. Both were reproduced live against this checkout.

**GW-07 is real, and the reported flaky test is a faithful reproduction, not noise.** There are two
distinct silent-loss paths, both reproduced deterministically with targeted probes. Accepted usage
events can be abandoned in the channel on a drain timeout, and a shutdown flush whose deadline
expires re-queues its batch into a buffer that is then discarded at process exit. Neither path
increments `gateway_usage_events_dropped_total`; neither logs above `Debug`. Separately,
`HostOptions.ShutdownTimeout` is configured nowhere in the repository, so the shutdown budget is the
30 s framework default against a drain ladder that needs exactly 30 s under the Helm defaults — zero
margin.

**GW-10 is real and mechanically proven.** `33pol.Api` depends on `33pol.Security` through both a
project reference and source, the rule forbidding it passes, and a probe shows the selector matches
nothing: `NotHaveDependencyOn("33pol.Security")` succeeds while `NotHaveDependencyOn("Pol33.Security")`
fails on `Pol33.Api.Endpoints.ModelsEndpoints`.

**GW-02 is partially confirmed and should be downgraded.** The false-healthy state is a startup
window, not a steady state. Measured on a live boot with two `localhost:8080` demo routes and
nothing listening: `/health/ready` returned 200 with `healthyBackends: 2`, then flipped to 503 with
`healthyBackends: 0` and `gateway_backend_health = 0` within the sweep budget. `GatewayNoHealthyBackends`
therefore *does* fire in a steady outage — the audit's central claim about that alert is disproved.
What survives is different and still worth fixing: readiness passes before anything has been probed,
an empty registry reports ready, and the alert expression is blind when the gauge has no series at
all.

### Systemic patterns

1. **Conditional registration omits the guard written for that exact condition.** The fail-closed
   check lives inside `GatewayAuthenticationInitializer`; the initializer is registered on line 98,
   past the line-84 `return` taken by the blank-connection-string branch. The guard exists, is
   correct, and is unreachable.
2. **Permissive defaults with no "not yet known" state.** `GatewayAuthenticationState.IsAuthenticationRequired`
   is a `bool` defaulting to `false`; `BackendHealthStore.IsBackendHealthy` answers `!strictMode` —
   i.e. healthy — for a model never probed. In both cases "uninitialized" is indistinguishable from
   "verified permissive".
3. **Cancellation treated as benign deferral at the last moment it can be one.** Requeue-on-cancel is
   correct during steady-state flushes and is silent data loss during the final one; the code cannot
   tell the two apart.
4. **Assertions written against assembly names while the code carries root namespaces.**
   `Directory.Build.props` sets `RootNamespace` to `Pol33$(…)` while assemblies are `33pol.*`; every
   architecture predicate naming a first-party module matches zero types.
5. **Tests that pin the defect or stub away the subject.** `SecurityServiceCollectionExtensionsTests`
   asserts the initializer is *not* registered; every integration factory substitutes
   `AlwaysHealthyBackendHealthStore`, so no integration test has ever exercised real readiness.

### Severity changes justified by evidence

| Finding | Audit | Revised | Why |
|---|---|---|---|
| GW-01 | S0 | S0 held | Reproduced live; blast radius widened by the skipped pepper validation |
| GW-07 | S2 | **S1 raised** | Two deterministic silent-loss paths, zero telemetry, affects every deploy under traffic |
| GW-02 | S1 | **S2 lowered** | Self-corrects in ~1 sweep budget; the alert-suppression claim is disproved |
| GW-10 | S2 | S2 held | Proven vacuous; no production impact, but it is why GW-10-class drift is invisible |
| `RetainOnly` | no caller | **partly wrong** | `BackendHealthStore.RetainOnly` *is* called from `HealthCheckService.PruneRemovedModels` |
| `TokenSource` | not persisted | **partly wrong** | It *is* persisted — to `recent_requests`. The gap is `billing_events` |
| `UsageRetentionDays` | unused | **partly wrong** | Read by reconciliation to clamp its window. It performs no pruning; two comments say it does |

### Recommended sequence

Security first, then durability, then the cheap proof-restoring change, then availability semantics,
then cleanup: **1** fail-closed auth → **2** shutdown loss accounting → **3** shutdown timeout
hierarchy → **4** non-vacuous architecture rules → **5** readiness semantics → **6** demo config out
of the image → **7** disconnected functionality. Batches 2 and 3 must deploy together. Everything
else merges independently.

---

## 2. Verification matrix

| ID | Status | Sev | Evidence | Root cause | Production impact |
|---|---|---|---|---|---|
| GW-01 | Confirmed | S0 | Live boot, `ASPNETCORE_ENVIRONMENT=Production`, shipped config: six protected routes answered 200 with no credential, including `POST /admin/api/config/reload` | `SecurityServiceCollectionExtensions.cs:84` returns before `AddHostedService<GatewayAuthenticationInitializer>()` on line 98 | Full anonymous read/write of the control plane whenever `ConnectionStrings:GatewayDb` is unset — which is the shipped default |
| GW-01b *(new)* | Confirmed | S1 | Same boot started successfully in Production with `KeyPepper = dev-pepper-change-me`; only an `ERR` log was emitted, not a startup failure | `IValidateOptions<GatewaySecurityOptions>` + `ValidateOnStart()` are registered on lines 88–89, also past the line-84 return | The published default pepper is accepted in Production, and the cache-TTL revocation-SLA bound is unenforced, in exactly the DB-less mode |
| GW-07a | Confirmed | S1 | Probe against the built `ChannelUsageRecorder`: 500 accepted, 1 persisted, 499 abandoned, `RecordUsageEventsDropped` called 0 times | `ChannelUsageRecorder.StopAsync` cancels `_stopping` on drain timeout; `ProcessAsync` rethrows and the channel remainder is never counted | Every shutdown that outruns its budget loses billing events silently; ledger, rollups, quotas and budgets all under-count with no signal |
| GW-07b | Confirmed | S1 | Probe: `FlushPendingAsync` with an expired token returned normally; 7 events sat in `_pending`; `DroppedEventCount` 0, metric 0 | `BillingUsageBatchPersistenceHandler.FlushBatchAsync` catches `OperationCanceledException` → `Requeue` + `LogDebug`, with no notion of "this was the last chance" | The reported `ResolvedHandler_Shutdown_PersistsPartialBatch` failure is this path. In production the requeued batch dies with the process |
| GW-07c | Confirmed | S2 | `grep -rn ShutdownTimeout src/ config/ deploy/ .github/` → no matches (same for `ConfigureHostOptions`). Probe: `new HostOptions().ShutdownTimeout` = `00:00:30` | Never configured; `ShutdownDrainSeconds` defaults to `0` in `appsettings.json` and is set only by Helm (15) | Helm ladder needs 15 + drain + 5 + 5 ≈ 30 s against a 30 s budget. Docker Compose gets no drain window at all |
| GW-02a | Partially confirmed | S2 | Live: `/health/ready` → 200 `healthyBackends: 2` at boot; → 503 `healthyBackends: 0` after the sweeps | `BackendHealthStore.IsBackendHealthy` returns `!_strictMode` for unprobed models; `HealthCheckStrictMode` defaults `false` | A pod passes its readiness gate and takes traffic before a single upstream has been proven reachable — on every rolling restart |
| GW-02b | Confirmed | S3 | `33pol.App.csproj:22` copies `config/models.json` to output; `Dockerfile:13` copies `config/`; compose and Helm both default `ModelsConfigPath` to it | The demo registry is a build artefact of the release image, not a sample | A fresh deployment starts with two fictional routes rather than an empty registry, so "nothing configured" is indistinguishable from "misconfigured" |
| GW-02c *(new)* | Confirmed | S2 | `GatewayReadinessService.cs:26-28` — `modelCount == 0 \|\| healthyCount > 0`. `GatewayBackendHealthMetricsExporter` emits one series per registered model, so an empty registry emits none | Zero configured models is treated as "healthy idle" for both readiness and the alert expression `max(gateway_backend_health) == 0` | A gateway that can serve nothing reports ready and raises no alert. This is the genuine alert-blindness, not the demo routes |
| GW-10 | Confirmed | S2 | 14/14 pass. Probe on the built `33pol.Api.dll`: `"33pol.Security"` → success; `"Pol33.Security"` → fails on `Pol33.Api.Endpoints.ModelsEndpoints` | NetArchTest matches type full names (namespaces). Rules name assemblies. `RootNamespace` is `Pol33.*` | No runtime impact. Six rules prove nothing, so layering drift lands in `main` green |
| `RetainOnly`/`Forget` | Partially confirmed | S3 | `HealthCheckService.cs:151` calls `BackendHealthStore.RetainOnly`. `ModelCircuitBreakerRegistry.Forget`/`.RetainOnly` and `RollingWindowStats.RetainOnly` have no callers outside tests | Only the health store was wired to the sweep; the registry has no change notification the other two could hang off | Breaker and window state accumulate across add/rename/delete cycles until the breaker cardinality cap forces every model onto the shared overflow breaker |
| `TokenSource` | Partially confirmed | S3 | Persisted via `RecentRequestUsageMapper` → `RecentRequestSnapshotEntity.TokenSource`. Absent from `BillingEventEntity` and `BillingEventRecord` | The pricing decision (`PriceEvent`, line 280) consumes it; the ledger row it writes does not carry it | `Estimated` usage — counts approximated after a client disconnect — is indistinguishable from authoritative usage in the ledger, against its own doc comment. `recent_requests` holds only 500 rows |
| `UsageRetentionDays` | Partially confirmed | S3 | Only consumer is `BillingReconciliationHostedService.cs:86` (window clamp). No delete touches `BillingEvents` or the rollups anywhere in `src/` | Retention was designed, documented in two XML comments, and never implemented | `billing_events` grows without bound on an embedded SQLite file. [`finops.md`](finops.md) already admits it; the code comments contradict the docs |
| Kestrel before migrations | Disproved | — | Prior audit measurement; not re-litigated | — | Order is migrations → auth init → listening |
| Quadratic fingerprint parser | Disproved | — | Prior audit analysis; not re-litigated | — | Linear |
| `USER $APP_UID` | Disproved | — | Prior audit; `Dockerfile:53` resolves via the aspnet base image | — | None |
| Demo routes suppress the outage alert | Disproved | — | Live: `gateway_backend_health{model="local-mock"} 0` after sweeps; `max(...) == 0` satisfied | — | The alert fires. GW-02c is the real blindness |

---

## 3. Root-cause findings

### GW-01 / GW-01b — authentication fails open

**Violated invariant.** Authorization must never depend on an uninitialized state whose default is
permissive, and incomplete authentication initialization must never downgrade protected endpoints to
anonymous access.

**Exact failure path**

1. `src/33pol.App/appsettings.json:3` ships `"GatewayDb": ""`.
2. `SecurityServiceCollectionExtensions.AddGatewaySecurity` reads it at line 26, enters the blank
   branch at line 78, registers the four `Null*` services, and returns at line 84.
3. Lines 88–89 (`GatewaySecurityOptionsValidator` + `ValidateOnStart`) and line 98
   (`AddHostedService<GatewayAuthenticationInitializer>`) are never reached.
4. `GatewayAuthenticationState.IsAuthenticationRequired` (`Hosting/GatewayAuthenticationState.cs:7`)
   keeps its default `false`. Nothing ever writes it.
5. `GatewayAuthorizationHandler.HandleRequirementAsync` (lines 33–37) calls
   `context.Succeed(requirement)` for `Inference`, `Admin` *and* `Operator` alike.
6. `UseGatewaySecurity` (lines 109–113) additionally skips registering `GatewayAuthorizationMiddleware`
   in the same branch, so the second, path-based gate is also absent.
7. Every `.RequireAuthorization(...)` group across the 14 endpoint files therefore admits anonymous
   callers.

```
$ ASPNETCORE_ENVIRONMENT=Production dotnet 33pol.App.dll   # shipped appsettings.json, blank GatewayDb

GET  /admin/api/rate-limits    -> 200  {"enabled":true,"adaptiveEnabled":false,"default":{"rpm":3000,…
GET  /admin/api/config/status  -> 200  {"hotReloadEnabled":true,"modelCount":2,"models":[{"id":"local-mock",…
GET  /admin/api/cors           -> 200  {"allowedOrigins":["https://*.github.io","http://localhost:3000"]}
GET  /admin/api/errors         -> 200  {"occurrences":[{"id":"err_afc058f7…","level":"Error",…
GET  /stats                    -> 200  {"uptime":"00.00:00:46","totalRequests":0,…
POST /admin/api/config/reload  -> 200  {"status":"success","message":"Model registry reloaded.",…
```

No credential was sent on any request. The reload is state-changing. The same run also started with
`KeyPepper = dev-pepper-change-me` in Production — the validator that forbids it was never registered.

**Why the tests did not catch it.** `tests/33pol.Security.Tests/DependencyInjection/SecurityServiceCollectionExtensionsTests.cs:26-28`
asserts the bug as the specification:

```csharp
services.Any(d => d.ServiceType == typeof(IHostedService)
               && d.ImplementationType == typeof(GatewayAuthenticationInitializer))
    .Should().BeFalse();
```

And `GatewayWebApplicationFactory.Create` (`tests/33pol.Integration.Tests/Support/`, line 50) forces
`ConnectionStrings:GatewayDb = ""` and defaults the environment to `Development` — so the entire
integration suite runs in the fail-open mode and treats it as normal. No test boots Production with a
blank connection string.

**Why monitoring did not catch it.** Anonymous admin access produces 200s. There is no metric for
"authentication disabled", no alert on it, and the single `LogWarning` that would announce it lives
inside the initializer that never runs. `/metrics` is likewise served without the scrape token in
this mode, because `MetricsScrapeAuthorization` defers to the same state.

### GW-07 — accepted usage events lost at shutdown

**Violated invariant.** No event may silently disappear after being considered accepted.

**Lifecycle as built.** `received` → `InferenceUsageCapture` builds a `UsageEvent` →
`ChannelUsageRecorder.Enqueue` → `_channel.Writer.TryWrite`. **Acceptance is that `TryWrite`
returning `true`**, and it is load-bearing: on `true` the recorder immediately increments
`gateway_tokens_total` (lines 70–78) and `ModelRouterMiddleware` settles the request's budget
reservation (`settledByUsage`, line 520). From that instant the event is billed in Prometheus and
released from the budget ledger, whether or not it is ever written.

**Loss path A — drain abandon.** `ChannelUsageRecorder.StopAsync` (lines 119–128) races `_worker`
against the host token. On timeout it cancels `_stopping`; `ProcessAsync` (line 172) rethrows; the
channel's remaining items are dropped. `_worker`'s exception is never observed. Nothing counts the
remainder.

```
$ scratch probe · ChannelUsageRecorder + a persistence handler that blocks · 1 s host budget

accepted (Enqueue==true)      : 500
queue depth before shutdown   : 499
handed to persistence         : 1
FlushPendingAsync calls       : 1
queue depth after StopAsync   : 499
RecordUsageEventsDropped calls: 0
=> silently lost              : 499 events accounted for by nothing
```

**Loss path B — final flush requeue.** `BillingUsageBatchPersistenceHandler.FlushBatchAsync` catches
`OperationCanceledException` (lines 195–199) and calls `Requeue(batch)` with a `LogDebug`. That is
right for a steady-state flush and wrong for the two shutdown callers — `StopAsync` line 102 and
`ChannelUsageRecorder.StopAsync` line 141 — both of which pass a 5 s `ShutdownFlushTimeout` token and
treat a normal return as success.

```
$ scratch probe · 7 buffered events · FlushPendingAsync with an already-expired deadline

DroppedEventCount reported   : 0
RecordUsageEventsDropped     : 0
FlushPendingAsync returned normally (no throw) -> caller treats shutdown flush as successful
events still in _pending     : 7  <- discarded at process exit, unlogged above Debug
```

This is the mechanism behind `ResolvedHandler_Shutdown_PersistsPartialBatch`: it passes in 2 s
isolated and fails ~28 s into the full suite because CPU contention pushes the final flush past the
5 s deadline. The test is correct; the code is not.

**Timeout hierarchy as it stands**

```
terminationGracePeriodSeconds   = 60 s   deploy/helm/33pol/values.yaml:142
HostOptions.ShutdownTimeout     = 30 s   ← framework default; configured NOWHERE
  ├ ShutdownDrainSeconds        = 15 s   values.yaml:91 · appsettings.json default 0 · compose unset
  ├ ChannelUsageRecorder drain  = remainder of the host budget
  ├ recorder final flush        =  5 s   ChannelUsageRecorder.ShutdownFlushTimeout
  └ batch handler final flush   =  5 s   BillingUsageBatchPersistenceHandler.ShutdownFlushTimeout
                                  ──────
                                   15 + drain + 5 + 5  ≥  25 s of fixed cost against a 30 s budget
```

Under the Helm defaults the drain loop gets at most 5 s before the host token trips and loss path A
opens. Under Compose, `ShutdownDrainSeconds` is 0, so readiness never goes unhealthy before Kestrel
closes and in-flight requests are cut — the exact failure `GatewayShutdownHostedService` was written
to prevent.

**Why monitoring did not catch it.** `gateway_usage_events_dropped_total` is the only signal, and
neither path increments it. `gateway_usage_writer_queue_depth` is left non-zero, but the process is
exiting and the value is never scraped. Reconciliation compares the ledger against rollups — both of
which miss the same events — so the drift is invisible there too.

### GW-02 — readiness derived from unproven state

**Violated invariant.** Readiness must represent actual ability to serve production traffic.

`GatewayReadinessService.GetReadiness` (lines 23–28) computes `healthyCount` from
`IBackendHealthStore.IsBackendHealthy`, which for a never-probed model returns `!_strictMode` —
`true` by default (`BackendHealthStore.cs:25-31`, `GatewayOptions.HealthCheckStrictMode` line 62,
default `false`). With `HealthCheckIntervalSeconds = 30` and `HealthCheckUnhealthyThreshold = 2`,
every model is reported healthy for roughly the first 60–90 s of process life. Readiness additionally
passes when `modelCount == 0`.

```
$ curl /health/ready — same live boot, two demo routes, nothing on :8080

t+46s   200  {"status":"ready",    "registryLoaded":true,"modelCount":2,"healthyBackends":2,…}
t+~2m   503  {"status":"not_ready","registryLoaded":true,"modelCount":2,"healthyBackends":0,…}

gateway_backend_health{model="local-mock"} 0
gateway_backend_health{model="other-mock"} 0
```

Steady state is correct, so `max(gateway_backend_health) == 0` fires. The defect is the window, the
empty-registry case, and the absent-series case — not permanent false health.

**Why tests did not catch it.** `GatewayWebApplicationFactory` removes the health sweep and
substitutes `AlwaysHealthyBackendHealthStore` for every integration test, so readiness has never been
exercised against a real store.

### GW-10 — architecture rules that select nothing

**Violated invariant.** A rule whose target set is unexpectedly empty must fail.

```
$ scratch probe · NetArchTest 1.3.2 against src/33pol.Api/bin/Debug/net10.0/33pol.Api.dll

Api types scanned: 99
NotHaveDependencyOn("33pol.Security") -> IsSuccessful=True,  failing=null
NotHaveDependencyOn("Pol33.Security") -> IsSuccessful=False, failing=Pol33.Api.Endpoints.ModelsEndpoints
NotHaveDependencyOn("33pol.Proxy")    -> IsSuccessful=True,  failing=null
NotHaveDependencyOn("Pol33.Proxy")    -> IsSuccessful=True,  failing=null
```

The real violation is a single `using Pol33.Security.Identity;` at
`src/33pol.Api/Endpoints/ModelsEndpoints.cs:9`, plus the `ProjectReference` in `33pol.Api.csproj`.

**Per-rule audit**

| Rule | Proves anything? | Detail |
|---|---|---|
| `Core_ShouldNotReferenceAspNetEfOrYarp` | yes | All three prefixes are real namespaces |
| `Registry_ShouldNotReferenceHttpPipelineTypes` | yes | Same |
| `Proxy_ShouldNotReferencePersistence` | **no** | Sole predicate is `"33pol.Persistence"` → 0 matches |
| `Api_ShouldOnlyReferenceCoreAmongFeatureLibraries` | **no** | All nine predicates are `33pol.*`; a live violation passes |
| `OperatorConsole_ShouldNotReferenceAspNetYarpProxyOrApi` | partly | `Microsoft.AspNetCore`/`Yarp` real; the two `33pol.*` entries vacuous |
| `Policy_ShouldNotReferenceProxyOrRegistry` | **no** | Both predicates vacuous |
| `Persistence_ShouldOnlyReferenceCore` | **no** | All nine vacuous |
| `Observability_ShouldOnlyReferenceCore` | **no** | All nine vacuous |
| `FeatureAssemblies_ShouldNotHaveCircularReferences` | yes | Uses real `Assembly.GetName()` values, not namespaces |
| `SolutionLayout` · `YarpRegistration` · `SingletonInstanceIdentity` | yes | File-system and descriptor scans; unaffected |

Six of nine dependency rules prove nothing, and `AssertArchitectureRule` reads
`result.FailingTypes ?? []` — so an empty target set and a clean target set are the same green.

---

## 4. Remediation batches

### Batch 1 — Fail-closed authentication (S0)

- **Objective:** make it impossible for a missing, empty or unvalidated security configuration to
  serve a protected endpoint anonymously.
- **Issues:** GW-01, GW-01b.
- **Invariant:** before authentication state has been *successfully established*, every protected
  request is denied; a Production host with no database and no explicit opt-in does not start.
- **Files:**
  - `src/33pol.Core/Abstractions/IGatewayAuthenticationState.cs` — add `GatewayAuthenticationMode Mode { get; }`; keep `IsAuthenticationRequired` as a default-implemented alias.
  - `src/33pol.Security/Hosting/GatewayAuthenticationState.cs` — replace the `bool` with the enum.
  - `src/33pol.Security/DependencyInjection/SecurityServiceCollectionExtensions.cs` — hoist lines 88–89 and 98 above the line-84 return; register `GatewayAuthorizationMiddleware` unconditionally in `UseGatewaySecurity`.
  - `src/33pol.App/appsettings.json` — delete the `"GatewayDb": ""` entry.
  - `tests/33pol.Security.Tests/DependencyInjection/SecurityServiceCollectionExtensionsTests.cs:26-28` — invert the assertion.
  - [`deploy/docker/README.md`](../deploy/docker/README.md), [`security.md`](security.md), [`release.md`](release.md) — document the `AllowAnonymous` escape hatch.
- **Behaviour:** the initializer always runs. Production + blank connection string + `AllowAnonymous`
  unset → `InvalidOperationException` at `StartAsync`, host does not start, Kestrel never binds.
  Production + blank + `AllowAnonymous=true` → starts, logs the existing warning, mode
  `AnonymousAllowed`. Development + blank → unchanged.
- **Test that should fail before the fix:** integration test —
  `GatewayWebApplicationFactory.Create(environmentName: Production)` with a blank connection string
  currently boots and returns 200 from `/admin/api/rate-limits`; assert it throws on build.
- **Test that should pass after:** same test asserts `InvalidOperationException` mentioning
  `ConnectionStrings:GatewayDb`. A second test with `Gateway:Security:AllowAnonymous=true` boots and
  serves anonymously — the escape hatch pinned deliberately.
- **Negative tests:** Development + blank still boots. Production + real connection string + zero keys
  still throws the existing "at least one API key" error. A handler resolved with `Mode = Uninitialized`
  denies `Admin`, `Operator` *and* `Inference`. Production + `KeyPepper = dev-pepper-change-me` + blank
  connection string now fails `ValidateOnStart`.
- **Configuration:** breaking for anyone deliberately running DB-less outside Development — they must
  set `Gateway:Security:AllowAnonymous=true`. No new keys.
- **Deployment:** none, but see §8 — any environment currently relying on the fail-open default will
  refuse to start. Stage this.
- **Back-compat:** `IsAuthenticationRequired` keeps its shape, so all nine read sites compile unchanged.
- **Observability:** add `gateway_authentication_required` (1/0) so a fail-open deployment is visible
  on a dashboard, and alert on `== 0`.
- **Rollback:** revert the commit; no data or schema change.
- **Ships:** independently.

### Batch 2 — Shutdown durability accounting (S1)

- **Objective:** make it impossible for an accepted usage event to vanish without being counted and
  logged.
- **Issues:** GW-07a, GW-07b.
- **Invariant:** every event for which `Enqueue` returned `true` ends *persisted* or *counted as lost*
  via `RecordUsageEventsDropped` plus an `Error` log.
- **Files:**
  - `src/33pol.Observability/Usage/ChannelUsageRecorder.cs` — after the drain race, drain the channel remainder with `TryRead`, attempt persistence under the flush deadline, count and log whatever is left.
  - `src/33pol.Core/Abstractions/IUsagePersistenceHandler.cs` — change `FlushPendingAsync` to return the count it could not write (`ValueTask<int>`), or add a `lastChance` flag.
  - `src/33pol.Billing/Usage/BillingUsageBatchPersistenceHandler.cs` — in the cancellation branch of `FlushBatchAsync`, drop-and-count instead of requeue when the flush is a last chance; have `FlushPendingAsync` report the unwritten count.
  - `src/33pol.Billing/Usage/NoOpUsagePersistenceHandler.cs` — signature follow-through.
- **Behaviour:** shutdown start — the writer is completed, so later `Enqueue` calls return `false` and
  the router keeps its reservation (already correct). Drain timeout — the remainder is persisted if
  possible, otherwise counted. Flush deadline exceeded on the last chance — the batch is counted as
  dropped, not requeued. The process still exits 0.
- **Test that should fail before the fix:** enqueue N events against a blocking persistence handler,
  stop with a 1 s budget, assert `RecordUsageEventsDropped` was called with the abandoned count. Fails
  today with 0 (probe output in §3).
- **Test that should pass after:** same assertion passes; a second test asserts `FlushPendingAsync`
  under an expired token reports its unwritten count and the batch handler's `DroppedEventCount` advances.
- **Negative tests:** a steady-state flush cancelled by the flush *gate* still requeues and is *not*
  counted as dropped. A successful shutdown persists everything and counts zero.
  `ResolvedHandler_Shutdown_PersistsPartialBatch` still passes on a fast machine.
- **Configuration:** none.
- **Deployment:** none. Must land before or with Batch 3 so the new counter is available to validate
  the new budget.
- **Back-compat:** `IUsagePersistenceHandler.FlushPendingAsync` is a public interface with a default
  implementation — keep the default so external implementors still compile.
- **Observability:** `gateway_usage_events_dropped_total` gains a shutdown reason; add an alert on any
  increase. Log at `Error` with the count and a capped sample of request ids.
- **Rollback:** revert; the change only adds accounting.
- **Ships:** independently, but deploy together with Batch 3.

### Batch 3 — Shutdown timeout hierarchy (S2)

- **Objective:** give the drain ladder a budget that fits, and refuse to start with one that cannot.
- **Issues:** GW-07c.
- **Invariant:** `terminationGracePeriod > ShutdownTimeout ≥ ShutdownDrainSeconds + drainBudget + 2 × ShutdownFlushTimeout + margin`, validated at startup.
- **Files:**
  - `src/33pol.App/GatewayHostBuilderExtensions.cs` — `builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = …)` from a new `Gateway:Resilience:HostShutdownTimeoutSeconds`.
  - `src/33pol.Core/Configuration/GatewayResilienceOptions.cs` — the new option, default 45.
  - `src/33pol.Core/Configuration/GatewayOptionsValidation.cs` — fail when the budget cannot cover the ladder.
  - `src/33pol.App/appsettings.json` — set `ShutdownDrainSeconds` to a real value rather than 0.
  - [`deploy/docker/docker-compose.yml`](../deploy/docker/docker-compose.yml) — `stop_grace_period` plus `Gateway__Resilience__ShutdownDrainSeconds`.
  - [`deploy/helm/33pol/values.yaml`](../deploy/helm/33pol/values.yaml) + `templates/deployment.yaml` — surface the host timeout and keep `terminationGracePeriodSeconds` above it.
  - [`docs/runbooks/`](runbooks/) — the ladder.
- **Behaviour:** shutdown gets 45 s by default against a 60 s grace period, leaving ≈20 s of drain
  after the 15 s readiness window and the two 5 s flushes. A configuration where the arithmetic fails
  is rejected at startup with the numbers in the message.
- **Test that should fail before the fix:** startup-validation test — `ShutdownDrainSeconds = 30` with
  `HostShutdownTimeoutSeconds = 30` currently starts; assert it throws.
- **Test that should pass after:** throws with a message naming both values. A Helm template test
  asserts `terminationGracePeriodSeconds > HostShutdownTimeoutSeconds` for the default values.
- **Negative tests:** defaults validate. `ShutdownDrainSeconds = 0` (drain disabled) still validates.
- **Configuration:** one new key with a safe default. Operators who set `ShutdownDrainSeconds` high
  must raise the host timeout — that is the point.
- **Deployment:** Helm values change; Compose gains `stop_grace_period`. Roll out after Batch 2 so the
  drop counter proves the new budget is sufficient.
- **Back-compat:** a deployment with a long custom drain may now fail validation. Call it out in
  release notes.
- **Observability:** log the resolved ladder once at startup.
- **Rollback:** revert; nothing persisted.
- **Ships:** **atomically with Batch 2** — alone it changes timing without surfacing what the old
  timing was losing.

### Batch 4 — Non-vacuous architecture rules (S2)

- **Objective:** make every architecture rule prove it inspected real code, and fix the one real
  violation it was hiding.
- **Issues:** GW-10.
- **Invariant:** a rule whose scanned set or whose probed namespace matches zero types fails.
- **Files:**
  - `tests/33pol.Architecture.Tests/DependencyRulesTests.cs` — replace every `"33pol.X"` literal with a namespace resolved from the assembly itself; extend `AssertArchitectureRule`.
  - New `tests/33pol.Architecture.Tests/ArchitectureNamespaces.cs` — assembly-name → root-namespace map built at runtime.
  - `src/33pol.Api/Endpoints/ModelsEndpoints.cs` — drop `using Pol33.Security.Identity`, read `TenantContext` from `HttpContext.Items[TenantContextKeys.HttpContextItemKey]` (the key already lives in `Pol33.Core.Identity`).
  - `src/33pol.Api/33pol.Api.csproj` — remove the `33pol.Security` `ProjectReference`.
- **Behaviour:** rules match. `Api_ShouldOnlyReferenceCoreAmongFeatureLibraries` becomes true rather
  than vacuous.
- **Test that should fail before the fix:** swap the literals first and run —
  `Api_ShouldOnlyReferenceCoreAmongFeatureLibraries` fails on `Pol33.Api.Endpoints.ModelsEndpoints`
  (proven in §3). Also add a deliberately misspelled probe namespace and confirm the new guard fails it.
- **Test that should pass after:** after decoupling `ModelsEndpoints`, all rules pass on real target
  sets. `Types.InAssembly(x).GetTypes()` is asserted non-empty for each scanned assembly.
- **Negative tests:** a rule naming a namespace absent from the whole solution fails rather than
  passing. Removing the `ProjectReference` must still compile — verify; if anything else in
  `33pol.Api` needs `Pol33.Security`, keep the reference and record the allowance explicitly instead
  of silently.
- **Configuration / deployment:** none.
- **Back-compat:** none — test project plus one `using`.
- **Observability:** none.
- **Rollback:** trivial.
- **Ships:** independently. Cheapest batch; land it early so later batches are checked by rules that
  work.

### Batch 5 — Production readiness semantics (S2)

- **Objective:** make `/health/ready` mean "a real upstream has been proven reachable", not "nothing
  has said otherwise yet".
- **Issues:** GW-02a, GW-02c.
- **Invariant:** ready requires at least one enabled backend with an affirmative probe result.
  Unprobed is not ready. Empty is not ready.
- **Files:**
  - `src/33pol.Api/Services/GatewayReadinessService.cs` — count from `healthStore.GetHealth(id)?.IsHealthy == true`, and require `enabledCount > 0 && healthyCount > 0`.
  - `src/33pol.Core/Models/GatewayReadinessResponse.cs` — add `configuredBackends` / `probedBackends` so an operator can tell warm-up from outage.
  - `src/33pol.Observability/Metrics/GatewayBackendHealthMetricsExporter.cs` — also publish `gateway_models_configured` so an empty registry has a series.
  - [`deploy/prometheus/alerts/33pol.yml`](../deploy/prometheus/alerts/33pol.yml) — `max(gateway_backend_health) == 0 or absent(gateway_backend_health)`, and a rule on `gateway_models_configured == 0`.
  - `tests/33pol.Integration.Tests/Support/GatewayWebApplicationFactory.cs` — keep `AlwaysHealthyBackendHealthStore` for routing tests, but readiness tests must use a real `BackendHealthStore`.
  - [`deploy/helm/33pol/values.yaml`](../deploy/helm/33pol/values.yaml) — readiness probe `initialDelaySeconds`/`failureThreshold` must tolerate the first sweep.
- **Behaviour:** `/health/ready` returns 503 from boot until the first successful probe, and 503
  whenever no enabled backend is healthy or the registry is empty. `/health/live` is unchanged —
  process liveness stays separate. Identical in Development, test and Production; no
  environment-conditional readiness.
- **Test that should fail before the fix:** integration — real `BackendHealthStore`, two routes, no
  probe run → currently 200; assert 503. Second: empty registry → currently 200; assert 503.
- **Test that should pass after:** both return 503; after seeding one healthy `BackendHealth`, 200.
- **Negative tests:** one healthy of two is ready. A stopped (non-serving) route does not count toward
  `enabledCount`. Draining is 503 regardless.
- **Configuration:** `HealthCheckStrictMode` keeps governing *routing* only; readiness no longer
  consults it. Document the split.
- **Deployment:** **real risk** — pods now stay unready for up to one probe interval at start. Raise
  `initialDelaySeconds` and `failureThreshold` in the same PR or rollouts will stall.
- **Back-compat:** behavioural change to a public endpoint. Release-note it.
- **Observability:** new gauge plus two alert-rule changes.
- **Rollback:** revert; readiness returns to optimistic.
- **Ships:** independently, but the chart change must be in the same PR.

### Batch 6 — Demo registry out of the release image (S3)

- **Objective:** a fresh deployment starts with no routes, not with two fictional ones.
- **Issues:** GW-02b.
- **Invariant:** demo, sample and placeholder backends are never present in a production artefact.
- **Files:**
  - `config/models.json` → `config/models.example.json`.
  - `src/33pol.App/33pol.App.csproj:22` — stop copying it to output.
  - `Dockerfile:13` — copy the example only.
  - [`deploy/docker/docker-compose.yml`](../deploy/docker/docker-compose.yml), [`deploy/helm/33pol/values.yaml`](../deploy/helm/33pol/values.yaml) — unchanged paths, but the file is now operator-supplied.
  - `src/33pol.Registry/Services/ModelRegistryLoader.cs` — log a clear warning when the configured path is missing, rather than treating it as an error.
  - [`deploy/docker/README.md`](../deploy/docker/README.md), [`README.md`](../README.md) — "copy the example and edit".
- **Behaviour:** no models file → empty registry → with Batch 5, `/health/ready` is 503 and
  `gateway_models_configured` is 0, so a half-configured install announces itself immediately.
- **Test that should fail before the fix:** build test asserting `models.json` is absent from the
  publish output.
- **Test that should pass after:** passes; a second test asserts `models.example.json` is present and
  parses.
- **Negative tests:** the Compose dev stack still works after `cp models.example.json models.json`.
  CI's `models.ci.json` path is unaffected.
- **Configuration:** existing deployments already bind-mount their own `config/` and are unaffected.
- **Deployment:** anyone relying on the baked-in demo file loses it — intended. Release-note it.
- **Back-compat:** breaking for the "just run the image" path; that path was never production-valid.
- **Observability:** covered by Batch 5's gauge.
- **Rollback:** trivial.
- **Ships:** independently, but lands *after* Batch 5 so the empty registry is visible rather than
  silently ready.

### Batch 7 — Disconnected functionality (S3)

- **Objective:** every retention- and attribution-related API either does what it says or says it does
  not.
- **Issues:** `RetainOnly`/`Forget`, `TokenSource`, `UsageRetentionDays`.
- **Invariant:** runtime behaviour and documentation agree; no API is tested but unreachable from
  production.
- **Files:**
  - `src/33pol.Registry/Services/ModelRegistryConfigReload.cs` (or `Hosting/ModelRegistryRouteReconcileService.cs`) — call `ModelCircuitBreakerRegistry.RetainOnly` and `RollingWindowStats.RetainOnly` with the post-reload id set.
  - `src/33pol.Proxy/Resilience/ModelCircuitBreakerRegistry.cs` — remove `Forget` if `RetainOnly` subsumes it.
  - `src/33pol.Persistence/Entities/BillingEventEntity.cs`, `src/33pol.Core/Billing/BillingEventRecord.cs`, `BillingEntityMapper`, plus one EF migration — add `TokenSource`.
  - `src/33pol.Core/Abstractions/IBillingReconciliationService.cs:15` and `src/33pol.Core/Configuration/BillingOptions.cs:105` — correct the two comments that claim pruning happens.
  - Either a new `BillingRetentionHostedService` (mirroring `GatewayErrorRetentionService`, which already does exactly this for `gateway_errors`) **or** rename the option and document it as a reconciliation-window bound only.
- **Behaviour:** registry churn stops leaking breaker and window state. The ledger records how each
  row's tokens were obtained. Retention either prunes or stops claiming to.
- **Test that should fail before the fix:** reload a registry from `{a,b}` to `{a}`; assert the breaker
  registry no longer tracks `b`. Persist an `Estimated` event and read the ledger row back; assert the
  source survives.
- **Test that should pass after:** both pass. If retention is implemented — seed events older than
  `UsageRetentionDays`, run the job, assert they are gone and the rollups are not.
- **Negative tests:** reload must not evict state for models still present. Retention must never delete
  inside the reconciliation window.
- **Configuration:** none new, unless retention gains an interval key.
- **Deployment:** one EF migration. On a large existing `billing_events` the column add is cheap; the
  first retention run is not — gate it behind an explicit enable for one release.
- **Back-compat:** existing ledger rows get a null `TokenSource`; readers must treat null as "unknown",
  not "split".
- **Observability:** retention logs rows deleted per run, like `GatewayErrorRetentionService` does.
- **Rollback:** the migration is additive and reversible. Retention is a separate hosted service that
  can be disabled.
- **Ships:** independently. Split into three PRs if the retention decision needs its own discussion.

---

## 5. Required design decisions

### GW-01 — the authentication state model

**Recommendation: a three-valued mode whose zero value is the safe one, plus unconditional
registration.**

```csharp
public enum GatewayAuthenticationMode
{
    Uninitialized    = 0,   // default(T) — nothing has established the posture yet
    Required         = 1,   // credentials enforced
    AnonymousAllowed = 2,   // explicitly opted into, logged loudly at startup
}

// IGatewayAuthenticationState keeps its existing member as a derived alias, so all nine
// existing read sites keep compiling and Uninitialized behaves exactly like Required:
bool IsAuthenticationRequired => Mode != GatewayAuthenticationMode.AnonymousAllowed;
```

A `bool` cannot express "not yet known", and the C# default for it is the permissive answer. Making
`Uninitialized` the zero value inverts that: a field nobody wrote denies. The alias property means
this is a two-file change, not a nine-site refactor — the smallest boundary that establishes the
invariant.

- **Startup.** `GatewayAuthenticationInitializer` is registered unconditionally. It already contains
  the correct policy (lines 39–63): outside Development, a blank connection string without
  `Gateway:Security:AllowAnonymous=true` throws. Hosted-service `StartAsync` failures abort
  `Host.StartAsync`, and the previously measured order is migrations → auth init → Kestrel listening,
  so the listener never binds.
- **Configuration failure.** `IValidateOptions<GatewaySecurityOptions>` and `ValidateOnStart()` move
  above the branch. The key pepper, the cache-TTL revocation bound, and any future security option are
  then validated in *every* configuration, not only the one that already has a database.
- **Initialization failure.** If the initializer throws for any reason, the mode stays `Uninitialized`
  and the host does not start. There is no path where a partially initialized host serves traffic.
- **Authorization before initialization.** `GatewayAuthorizationHandler` line 33 reads the alias, so
  `Uninitialized` takes the enforcing path and an unauthenticated request falls through to line 49 and
  is denied. `GatewayAuthorizationMiddleware` is registered unconditionally and no-ops only for the
  genuinely anonymous mode.
- **Readiness gating.** Not required. Readiness is for traffic admission, not for security; a host
  that cannot establish its authentication posture must not exist, not merely report unready.
- **Unsafe defaults removed.** Delete `"GatewayDb": ""` from `appsettings.json`. An absent key reads
  identically but stops presenting the fail-open configuration as a supported default.

**Alternatives considered.** Readiness gating alone leaves the process serving while unready — the
endpoints are still reachable. A startup `IValidateOptions` check on the connection string duplicates
policy the initializer already implements correctly. Neither is smaller, and neither denies a request
that arrives before initialization.

### GW-02 — production readiness semantics

| Term | Definition | Source of truth |
|---|---|---|
| configured | a route present in the registry | `IModelRegistry.GetAllModels()` |
| enabled | configured and `IsServing()` | `ModelConfig.IsServing()` |
| probed | enabled and `GetHealth(id)` is non-null | `BackendHealthStore` |
| healthy | probed and `IsHealthy` | same |
| reachable | last probe got an affirmative HTTP answer on one of the four probe paths | `HealthCheckService` |
| demo / sample | ships as `config/models.example.json` and is never copied to the publish output | build |
| ready | `registryLoaded && !reloadInProgress && !draining && enabled > 0 && healthy > 0` | `GatewayReadinessService` |

**`/health/ready` returns 503 exactly when** the registry failed to load, a reload is in progress, the
gateway is draining, zero routes are enabled, or zero enabled routes have an affirmative probe result
— including before the first sweep completes. Otherwise 200.

**The single mechanical change** is that readiness stops calling `IsBackendHealthy` (which answers
`!strictMode` for unknown models) and reads `GetHealth(id)?.IsHealthy == true`. That separates the two
questions the store currently conflates: *may I route to this backend right now* (optimistic by
design, so traffic is not refused during warm-up) versus *has this backend been proven usable* (never
optimistic). Routing behaviour and `HealthCheckStrictMode` are untouched.

**Isolation of demo configuration** is structural, not conditional: the sample file is not in the
artefact, so there is no environment check to get wrong. Development, test and Production all evaluate
readiness identically — the difference is what the operator mounted, not what the code believes about
its environment.

### GW-07 — event states and shutdown behaviour

| State | Entered when | Obligation |
|---|---|---|
| created | `InferenceUsageCapture` builds the `UsageEvent` | none |
| rejected | `Enqueue` returns `false` (channel full or writer completed) | counted, logged, reservation retained by the router |
| **accepted** | `_channel.Writer.TryWrite` returns `true` | **must reach persisted or lost**; tokens are metered and the budget reservation is settled at this instant |
| buffered | handed to `BillingUsageBatchPersistenceHandler._pending` | same |
| persisted | row committed to `billing_events` | terminal |
| lost | every attempt exhausted, or shutdown budget expired | `RecordUsageEventsDropped` + `Error` log with count and sample ids |

**"Accepted" stays at `TryWrite`.** The router needs a synchronous yes/no to decide whether to settle
the reservation, and moving acceptance to "durably written" would put a SQLite write on the request
path. What changes is not the boundary but the obligation attached to it.

- **Shutdown start.** `_channel.Writer.TryComplete()` — already the first thing `StopAsync` does.
  Subsequent `Enqueue` calls return `false`, so new events are explicitly rejected and the router keeps
  its reservation. Already correct; give it its own counter reason so rejection-at-shutdown is
  distinguishable from saturation.
- **Queued events.** Drained by `ProcessAsync` on `_stopping`, which is independent of the host's
  startup token.
- **Persistence failure.** Unchanged — retried with back-off up to `UsageWriterMaxFlushRetries`, then
  dropped, counted and logged. This path is already correct and is the model the shutdown paths should
  copy.
- **Drain timeout.** Today: cancel and abandon. New: cancel the loop, then `TryRead` the channel
  remainder, attempt one final `PersistAsync` pass under the flush deadline, and count plus log
  whatever is still unwritten.
- **Flush deadline exceeded.** Today: requeue into a buffer nobody will read again. New: on a
  last-chance flush, drop-and-count rather than requeue, and have `FlushPendingAsync` return the
  unwritten count so its caller can report it.
- **Forced termination.** SIGKILL cannot be accounted for in-process; the mitigation is Batch 3's
  budget arithmetic plus a `terminationGracePeriod` that exceeds it.
- **Process exit code.** **Stays 0.** A non-zero exit on shutdown makes orchestrators treat a clean
  rollout as a crash-loop and does not recover a single event. The failure belongs in a counter and an
  `Error` log, which an alert can act on without breaking deploys.

**Required relationship:** `terminationGracePeriod > HostOptions.ShutdownTimeout ≥ ShutdownDrainSeconds + drainBudget + 2 × ShutdownFlushTimeout + margin`,
enforced by startup validation so a deployment cannot configure a ladder that does not fit.

### GW-10 — architecture tests that prove their own reach

Two assertions per rule, not one.

1. **The rule.** As today, but with predicates resolved from the assemblies themselves rather than
   literals: build an assembly-name → root-namespace map at runtime
   (`asm.GetTypes().First().Namespace.Split('.')[0]` gives `Pol33`) so a future `RootNamespace` change
   cannot silently re-vacuate every rule.
2. **The reach.** `AssertArchitectureRule` additionally asserts that the scanned assembly yielded at
   least one type, and that every probed namespace prefix matches at least one type *somewhere in the
   solution* — a prefix that names nothing is a broken rule, not a satisfied one.

```csharp
private static void AssertArchitectureRule(
    TestResult result, Assembly scanned, params string[] probedNamespaces)
{
    Types.InAssembly(scanned).GetTypes().Should().NotBeEmpty(
        $"{scanned.GetName().Name} must contain types for this rule to mean anything");

    foreach (var ns in probedNamespaces)
    {
        Types.InAssemblies(AllSolutionAssemblies)
            .That().ResideInNamespaceStartingWith(ns).GetTypes()
            .Should().NotBeEmpty($"'{ns}' matches no type in the solution — the rule is vacuous");
    }

    (result.FailingTypeNames ?? []).Should().BeEmpty();
}
```

The self-test for the mechanism is a rule probing a deliberately nonexistent namespace, asserted to
fail. Framework-owned prefixes (`Microsoft.AspNetCore`, `Yarp`) are exempt from the reach check when
the solution legitimately contains none — pass them through a separate parameter so the exemption is
explicit rather than accidental.

---

## 6. Disconnected functionality

### `RetainOnly` / `Forget`

**Intended.** Drop per-model state for routes that no longer exist, so registry churn cannot grow the
maps without bound.

**Actual.** Three implementations, one wired. `BackendHealthStore.RetainOnly` *is* called every sweep
from `HealthCheckService.PruneRemovedModels` (`HealthCheckService.cs:145-151`) — the audit's "no
production caller" claim is wrong for this one. `ModelCircuitBreakerRegistry.Forget`,
`ModelCircuitBreakerRegistry.RetainOnly` and `RollingWindowStats.RetainOnly` have no caller outside
tests.

**Classification.** Missing integration — there is no registry-change hook for the two unwired maps to
attach to.

**Recommendation: wire.** `ModelRegistryConfigReload.ReloadAsync` already knows the before/after model
sets (lines 23–33); call both `RetainOnly` methods there with the post-reload id set. Delete `Forget`
if `RetainOnly` subsumes it, rather than leaving a second unused entry point. The consequence of
leaving it is concrete: the breaker registry has a cardinality cap that, once reached, pushes every
model onto a shared overflow breaker, so long-lived gateways with route churn eventually lose
per-model isolation.

**Tests.** Reload `{a,b}` → `{a}`; assert `b` is gone from both maps and `a` is not. Assert breaker
state for a surviving model is preserved across a reload.

### `TokenSource`

**Intended.** Record how each event's token counts were obtained — `Split`, `TotalOnly`, `Estimated` —
so estimated usage stays "distinct so it can be reconciled or excluded" (its own doc comment,
`UsageEvent.cs:20-27`).

**Actual.** Computed by `InferenceUsageCapture`, consumed by `BillingUsagePersistenceHandler.PriceEvent`
(line 280) to pick the pricing policy, and persisted — but only to
`RecentRequestSnapshotEntity.TokenSource`, a rolling buffer capped at `MaxRecentRequests = 500`. It is
absent from `BillingEventRecord` and `BillingEventEntity`.

**Classification.** Missing persistence, in the one table that is the source of billing truth.

**Recommendation: persist.** Add the column to `BillingEventEntity`/`BillingEventRecord` plus a
migration. What breaks without it: a ledger row cannot be re-priced or audited, because which rate
policy was applied is unrecoverable; estimated usage from client disconnects cannot be excluded from
an invoice; and a billing dispute cannot be answered from the ledger. `gateway_estimated_usage_total`
exists as an aggregate but is not attributable to a row.

**Tests.** Persist one event of each enum value and read the ledger row back. Assert a pre-migration
row with a null value is read as "unknown", never as `Split`.

### `UsageRetentionDays`

**Intended.** Per `BillingOptions.cs:105` and `IBillingReconciliationService.cs:15`, a TTL after which
`billing_events` is pruned.

**Actual.** Validated (`BillingOptionsValidation.cs:53`) and read exactly once —
`BillingReconciliationHostedService.cs:86`, to clamp the reconciliation window. No delete touches
`BillingEvents` or `DailyUsageRollups` anywhere in `src/`. [`finops.md`](finops.md) already states the
purge is not implemented, so the code comments contradict the shipped documentation.

**Classification.** Partially implemented — the option is real and load-bearing for reconciliation, but
performs no retention.

**Recommendation: schedule, with an explicit fallback.** `GatewayErrorRetentionService`
(`src/33pol.App/DependencyInjection/`) is a working template for exactly this shape against
`gateway_errors`; mirror it for `billing_events`. If retention is deferred, the fallback is not "leave
it" — it is to correct both XML comments and rename the option to state what it bounds. On an embedded
SQLite file, unbounded ledger growth is a real operational limit, not a tidiness concern.

**Tests.** Seed events on both sides of the cutoff, run the job, assert old rows are gone, recent rows
and all rollups survive, and the deleted count is logged. Assert the job never deletes inside the
reconciliation window.

---

## 7. Test strategy

| Layer | Required coverage |
|---|---|
| Unit | Authorization handler denies all three policies under `Mode = Uninitialized`. Recorder counts abandoned events on drain timeout. Batch handler distinguishes a steady-state requeue from a last-chance drop. Readiness computes from probe results, not from `IsBackendHealthy` |
| Integration | Production + blank connection string fails to build the host. Production + `AllowAnonymous=true` serves anonymously. Development + blank still boots. Readiness against a real `BackendHealthStore` in each of: unprobed, empty, all-down, one-up, draining |
| Startup / config | `ValidateOnStart` rejects the default pepper in Production with *no* database. Shutdown ladder validation rejects `drain ≥ timeout`. Missing models file logs a warning and yields an empty registry rather than failing |
| AuthN / AuthZ | All six audit-named routes return 401 with no credential against a configured Production instance. An Admin key from a non-operator tenant still gets 403 on Operator routes. `/metrics` requires its token whenever authentication is required |
| Deployment / container | Publish output contains no `models.json`. Helm renders `terminationGracePeriodSeconds > HostShutdownTimeoutSeconds`. Compose sets `stop_grace_period` above the same. Readiness probe delays tolerate the first sweep |
| Readiness | 503 before the first probe; 200 after one healthy probe; 503 on empty registry; 503 while draining; `/health/live` stays 200 throughout |
| Failure injection | Persistence throwing on every attempt exhausts retries and counts the drop. A blocking persistence handler at shutdown produces a non-zero drop count. A models file that fails to parse leaves `IsLoaded` false and readiness 503 |
| Shutdown under load | Enqueue continuously, stop the host, assert `persisted + dropped == accepted` exactly. Repeat with a shutdown budget too small to finish and assert the identity still holds |
| Persistence | Ledger round-trips `TokenSource`. Retention deletes only outside the window. Idempotent append still rejects duplicates (existing protection — do not regress) |
| Architecture | Every rule asserts a non-empty scanned set and non-empty probed namespaces. A rule naming a nonexistent namespace fails. The `Api → Security` rule fails if the coupling returns |

### The eight invariants the finished work must prove

1. Missing or empty authentication/database configuration cannot expose protected admin endpoints.
2. Authentication initialization failure cannot authorize protected requests.
3. Protected requests are denied before authentication state is successfully initialized.
4. Production readiness is non-ready when no usable real upstream exists.
5. Demo/sample backends cannot satisfy production availability metrics.
6. Every accepted billing/usage event is either persisted or explicitly accounted for through a
   surfaced failure state.
7. Architecture tests fail if their expected selector matches zero real targets.
8. Retention-related configuration either performs the documented behaviour or is removed/marked
   unsupported.

Invariant 6 is best expressed as one property test rather than several examples:
`persisted + dropped == accepted`, asserted after every shutdown scenario. It is the only formulation
that cannot be satisfied by a path nobody thought to enumerate.

---

## 8. Rollout and implementation order

### Merge and deploy shape

- **Merge independently:** 1, 2, 4, 5, 6, 7.
- **Deploy atomically:** 2 + 3. Batch 3 changes shutdown timing; without Batch 2's counter there is no
  way to tell whether the new budget is sufficient.
- **Ordering constraint:** 5 before 6 — an empty registry must be visibly unready before the demo file
  is removed, or an operator sees a silent 200 with nothing configured.

### Configuration migration

Batch 1 is the only breaking one. Any environment currently running DB-less outside Development will
refuse to start. Before merging, inventory those environments: check for deployments where
`ConnectionStrings__GatewayDb` is unset and `ASPNETCORE_ENVIRONMENT != Development`. Each must either
be given a database or have `Gateway__Security__AllowAnonymous=true` set *ahead of* the upgrade. The
Compose stack already sets a connection string and is unaffected; the Helm chart templates one at
`deployment.yaml:115` and is unaffected. The exposure is bespoke `docker run` deployments following the
README.

> **Treat any environment that is currently fail-open as compromised until proven otherwise.** Before
> deploying Batch 1, review `config/audit-log.jsonl` and the error store for admin mutations with no
> `ApiKeyId` — under the current build the audit trail is still written in anonymous mode, so anonymous
> control-plane actions are recorded and reviewable. Rotate any upstream provider credentials that were
> readable through `/admin/api/providers` while the gateway was reachable beyond loopback.

### Staging validation

1. Deploy Batch 1 to staging with a database configured — confirm normal operation, then confirm all
   six audit-named routes return 401 with no credential.
2. Deliberately blank the connection string in staging — confirm the pod fails to start with the
   documented message rather than serving anonymously.
3. Deploy 2 + 3 — run a rolling restart under synthetic load and assert
   `persisted + dropped == accepted` across the restart.
4. Deploy 5 — confirm pods go ready only after the first sweep and that rollouts do not stall on the
   new delay.
5. Deploy 6 — confirm a pod with no mounted models file reports 503 and raises the new empty-registry
   alert.

### Canary validation

One replica, one hour, watching: `gateway_authentication_required` stays 1;
`gateway_usage_events_dropped_total` flat except during the deliberate restart test;
`gateway_backend_health` series present and 1 for every enabled model; `gateway_models_configured`
non-zero; ledger row count advancing at the pre-change rate.

### Metrics and logs to monitor

| Signal | Expected | Rollback trigger |
|---|---|---|
| `gateway_authentication_required` | 1 | 0 on any replica |
| `gateway_usage_events_dropped_total` | flat | any sustained increase after Batch 3 |
| `gateway_backend_health` | 1 per enabled model | series absent, or 0 outside a real outage |
| `gateway_models_configured` | > 0 | 0 |
| `/health/ready` 503 rate | brief at pod start only | steady-state 503 with healthy upstreams |
| `billing_events` insert rate | unchanged | drop > 5% against the pre-change baseline |
| Host startup failures | zero | any, after the config inventory is complete |

### Rollback conditions

Batch 1: any environment fails to start that the inventory said was configured — revert, complete the
inventory, re-apply. Batches 2+3: dropped-event counter climbs during normal rolling restarts, meaning
the new budget is still too small — raise `HostShutdownTimeoutSeconds` first, revert only if that does
not settle it. Batch 5: rollouts stall on readiness — raise the probe delay first; revert only if the
first sweep genuinely cannot complete in time. Batches 4, 6, 7 are individually revertible with no data
implications; Batch 7's migration is additive and reversible.

### Recommended implementation order

1. **Batch 1 — Fail-closed authentication.** Unconditional initializer and options validation,
   tri-state auth mode, unconditional authorization middleware, remove the empty connection-string
   default, invert the test that pins the bug. `S0` · ships alone.
2. **Batch 2 — Shutdown durability accounting.** Count and log every accepted usage event abandoned at
   shutdown, on both the drain-timeout and last-chance-flush paths. `S1`.
3. **Batch 3 — Shutdown timeout hierarchy.** Configure `HostOptions.ShutdownTimeout`, validate the
   ladder at startup, fix the Compose and Helm grace periods. `S2` · deploy with 2.
4. **Batch 4 — Non-vacuous architecture rules.** Resolve namespaces from assemblies, assert reach,
   decouple `ModelsEndpoints` from `Pol33.Security.Identity`. `S2` · cheapest.
5. **Batch 5 — Production readiness semantics.** Readiness from probe results only, empty registry is
   not ready, new gauge and alert rules, probe delays in the chart. `S2`.
6. **Batch 6 — Demo registry out of the release image.** `models.example.json`, stop copying to publish
   output, docs. `S3` · after 5.
7. **Batch 7 — Disconnected functionality.** Wire the two unwired `RetainOnly` calls, persist
   `TokenSource` to the ledger, and either implement retention or correct the two comments that claim it
   exists. `S3` · splittable.

---

## Investigation provenance

Every claim above was checked against `main` @ `afeb6e0`, not against the audit's description of it.
Reproductions ran in an isolated scratch directory against copies of the build output; the gateway
instance started for the GW-01 and GW-02 reproductions was stopped and left no artefacts in the
repository. The NetArchTest, `ChannelUsageRecorder` and `BillingUsageBatchPersistenceHandler` probes
were standalone console projects referencing the built assemblies — no test project or source file was
modified. `HostOptions.ShutdownTimeout` was read from the runtime rather than assumed.

Four audit claims were disproved and are recorded as such in §2 rather than dropped, and three more
were materially corrected. The plan targets what the code does today.
