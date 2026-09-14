# 33pol — Rate Limiting Review (Gateway + Admin Panel)

**Date:** 2026-09-14 · **Commit reviewed:** `0a2495d` (clean tree, branch `main`)
**Scope:** `33pol.Core/RateLimiting`, `33pol.Core/Configuration`, `33pol.Policy/RateLimiting`, `33pol.Policy/Admin`, `33pol.Proxy/Middleware` + `Routing`, `33pol.Api/Endpoints/AdminRateLimitEndpoints.cs`, `33pol.Persistence` (rate-limit tables, seed, config store), `33pol.Observability` (usage tracker, meters, exporter), the admin console (`wwwroot/admin`), tests, docs and deploy assets.

**Relationship to the 2026-09-13 audit.** `docs/audit-2026-09-13.md` covered this area and produced findings GW-04, GW-23, GW-24, GW-25 and GW-30. This review was conducted independently against the current tree; where a finding coincides with a prior one it is marked **[re-confirmed: GW-nn — still open]** and the current code evidence is given. The rest are new.

---

## 1. Executive Summary

### Overall assessment

The rate-limiting subsystem is **well-architected and unusually well-reasoned**. The design decisions that normally go wrong in a gateway are right here:

- Scopes **compose** rather than override, so adding a narrower rule can only tighten — the outcome is order-independent (`RateLimitScope`, `RateLimitPlanResolver.Build`).
- Admission is **two-staged**: identity scopes gate the body parse, so a caller already over budget never makes the gateway buffer and JSON-parse its payload ([RateLimitMiddleware.cs:119-199](src/33pol.Proxy/Middleware/RateLimitMiddleware.cs#L119-L199)).
- Stage-two refusals **refund** stage one, so a narrow per-model limit cannot drain a tenant's gateway-wide budget.
- The token bucket refills **continuously**, which keeps `Retry-After` at ~1s instead of the up-to-59s a fixed window produces — a real client-compatibility win, since every OpenAI-compatible SDK sleeps for that header.
- Anonymous traffic is partitioned by **address block** with IPv6 collapsed to /64, closing the "mint 2^64 partitions" hole.
- Ungranted callers are **not charged** the shared model bucket, closing a cross-tenant DoS.
- State is **bounded everywhere** — partitions (50k), plan cache (20k), backoff table (20k), usage report keys (500), pressure dimensions (200) — and maintenance runs off the request path on a timer.
- Test coverage is genuinely strong: ~250 rate-limit-specific tests including concurrency/race, eviction, peek, multi-scope, schedule evaluation, DST and admin endpoint tests.

The weaknesses are concentrated in **two places**: the *semantics* of a few configuration values (where the code does something materially different from what the runbook and the admin UI promise), and the **admin write/preview path** (missing concurrency control, an uncapped response, and an uncapped O(n²) computation).

### Key risks and highest-priority actions

| # | Risk | Action |
|---|---|---|
| 1 | A rule documented as "does not limit the rate" (`rpm: 0`) silently becomes **1 request per minute** for that scope. On a `model` rule this is a gateway-wide throughput collapse. | **RL-001** — make `Rpm == 0` mean *no rate control*, and extend the `rpm 0 ⇒ burst 0` refusal to every scope. |
| 2 | The auth-failure (credential-guessing) budget is **peek-then-debit-after-pipeline**, so the sustained guessing rate an address gets is `configured_rpm × client_concurrency`, not `configured_rpm`. | **RL-005** — take the token atomically on the way in and refund it when the credential authenticates. |
| 3 | A rule whose `scope` is spelled with any non-lowercase casing **bypasses the target-shape check**, is accepted, persisted, and displayed — and can never fire. | **RL-002** — normalise `scope` on ingest, or make `IsSingleton`/`IsPair` case-insensitive. |
| 4 | A **suspending window on a `tenant` rule removes that tenant's stream-concurrency cap entirely** instead of restoring the plan's. "Pause this rule" makes the tenant *looser* than if the rule did not exist. | **RL-003** — carry suspension explicitly instead of encoding it as `(0,0,0)`. |
| 5 | `POST /admin/api/rate-limits/windows/preview` runs an **uncapped O(n²)** overlap scan over a client-supplied window array; the validation error is computed and then ignored. | **RL-004** — return early on validation failure; cap `windows` at the endpoint. |
| 6 | Admin writes are a **read-modify-write with no version check** and replace the rule set wholesale. Two operators editing concurrently silently clobber each other. | **RL-006** — add an ETag / `If-Match` on the config version. |

### Most important findings, in one paragraph

The enforcement engine itself is sound — the bucket maths, the multi-scope acquire/refund, the eviction ordering and the partition keying all hold up under scrutiny, and the race tests exercise the parts that matter. What does not hold up is the **configuration contract**: four distinct ways exist to write a rule that the validator accepts, the console displays, and the engine then interprets as something other than what was asked for (`rpm: 0` → 1 rpm; a mis-cased scope → inert; a suspended tenant window → unlimited streams; an `auth_failure` rule with `rpm: 0` → the 3000-rpm default tier). Separately, the **auth-failure limiter's peek/debit ordering** weakens the one control that bounds credential guessing by a factor equal to the attacker's concurrency, and the **admin write path** lacks both concurrency control and input bounds on two endpoints. Observability is good at the metric level but has **no alert rules for rate limiting at all**, and the one condition that means *limits have silently stopped being enforced* — forced eviction at the partition ceiling — emits a log line and no metric.

---

## 2. Issues and Bugs

### Critical

---

#### RL-001 · `rpm: 0` on a scoped rule collapses that scope to one request per minute

| Field | Content |
|---|---|
| **ID** | RL-001 |
| **Title** | A rule documented as "does not limit the rate" enforces 1 rpm after its burst is spent |
| **Severity** | **Critical** (confirmed) |
| **Affected area** | [RateLimitPolicy.cs](src/33pol.Core/RateLimiting/RateLimitPolicy.cs) · [InMemoryDistributedRateLimitStore.cs:378](src/33pol.Policy/RateLimiting/InMemoryDistributedRateLimitStore.cs#L378) · [RateLimitConfigValidation.cs:135-170](src/33pol.Core/Configuration/RateLimitConfigValidation.cs#L135-L170) · `docs/runbooks/rate-limit-admin.md:143` · Admin → Settings → Rate limits |
| **Description** | The runbook and the type documentation both state that `rpm: 0` on a scoped rule means "this rule does not limit the request rate at all — it exists only to cap concurrency". The engine does not implement that. `RateLimitPolicy.Capacity` is `Rpm + Burst` and `EnforcesRate` is `Capacity > 0`, so a rule with `rpm: 0, burst: 500` has a **capacity of 500**, not zero. `RefillPerSecond` then floors the refill rate at one request per minute: `Math.Max(1, policy.Rpm) / 60.0`. The result is a 500-token bucket that refills at **1 rpm**. Validation only refuses the `rpm: 0, burst > 0` combination for the `tenant` scope; every other scope — `global`, `model`, `api_key`, `tenant_model`, `api_key_model`, `anonymous` — accepts it, and so does `TryValidateWindowTier` for a scheduled window on any scope. |
| **Evidence** | `RateLimitPolicy.cs`: `public int Capacity => Rpm + Burst;` / `public bool EnforcesRate => Capacity > 0;`. `InMemoryDistributedRateLimitStore.cs:378`: `private static double RefillPerSecond(RateLimitPolicy policy) => Math.Max(1, policy.Rpm) / 60.0;`. `RateLimitConfigValidation.TryValidateRules`: the `rule.Rpm == 0 && rule.Burst != 0` refusal is guarded by `string.Equals(rule.Scope, RateLimitScopeNames.Tenant, …)`. Verified arithmetic: `capacity = 500`, `refill = max(1,0)/60 = 0.01667/s` → **1.0 effective rpm**. |
| **Impact** | Availability / throughput. A `model` rule written as `{"scope":"model","target":"gpt-4o","rpm":0,"burst":500,"maxConcurrentStreams":50}` — a natural way to express "cap concurrency, don't cap the rate" — admits 500 requests and then throttles that model to **one request per minute across the whole gateway**, for every tenant. The symptom (a model that works for a minute then goes dead) is very hard to trace back to a configuration value the docs say is a no-op. |
| **Reproduction** | 1. `PUT /admin/api/rate-limits` with a rule `{"scope":"model","target":"<model>","rpm":0,"burst":5,"maxConcurrentStreams":0}`. 2. Send 6 inference requests for that model within a second. 3. The 6th returns `429 rate_limit_exceeded` with `X-33pol-RateLimit-Scope: model` and `X-33pol-RateLimit-Limit: 5`. 4. Subsequent requests are admitted at one per minute. |
| **Root cause** | Two independent floors that each look locally reasonable: `Capacity` treating burst as usable without a rate, and `RefillPerSecond` clamping to 1 to avoid a divide-by-zero in the `RetryAfter` computation (`(1.0 - _tokens) / refillPerSecond`). Neither knows that `Rpm == 0` is the documented "off" value. |
| **Recommendation** | (a) Make `EnforcesRate => Rpm > 0` and short-circuit `TryAcquireRequest`/`TryAcquireAll` on it, so a zero-rpm rule contributes no rate control regardless of burst. (b) Extend the `rpm == 0 ⇒ burst == 0` refusal in `TryValidateRules` **and** `TryValidateWindowTier` from the `tenant` scope to all scopes. (c) Add a startup sanity pass that logs and ignores any stored rule already in this shape rather than applying it. (d) Add a per-scope validation test matrix. |
| **Priority rationale** | Critical rather than High because it is reachable from a documented, UI-supported configuration; it degrades a *correctly configured* deployment rather than requiring an attacker; the blast radius on a `model` or `global` rule is the whole gateway; and the failure mode is silent and slow to diagnose. |

**[re-confirmed: GW-04 — still open at `0a2495d`]**

---

### High

---

#### RL-002 · A mis-cased `scope` bypasses target validation and produces a rule that can never fire

| Field | Content |
|---|---|
| **ID** | RL-002 |
| **Title** | Scope names are matched case-insensitively for recognition but case-sensitively for shape, so `"Anonymous"` / `"Tenant_Model"` are accepted and inert |
| **Severity** | **High** (confirmed) |
| **Affected area** | [RateLimitScopeNames.cs:38-46](src/33pol.Core/RateLimiting/RateLimitScopeNames.cs#L38-L46) · [RateLimitConfigValidation.TryValidateTarget](src/33pol.Core/Configuration/RateLimitConfigValidation.cs) · [GatewayConfigStore.cs:77-104](src/33pol.Persistence/Repositories/GatewayConfigStore.cs#L77-L104) · `PUT /admin/api/rate-limits` |
| **Description** | `RateLimitScopeNames.IsKnown` uses `StringComparer.OrdinalIgnoreCase`, so `"Anonymous"` is a recognised scope. `IsSingleton` and `IsPair` use C# constant patterns (`scope is Global or AuthFailure or Anonymous`), which compare **ordinally**. A rule submitted as `{"scope":"Anonymous","target":"acme"}` therefore passes `IsKnown`, is *not* recognised as a singleton, so the "target must be `*`" check never runs; the "must not contain `\|`" check passes; the rule is stored. On load, `GatewayConfigStore` buckets rules into an `OrdinalIgnoreCase` dictionary and `Single(byScope, "anonymous")` finds the map — then looks for target `"*"`, finds `"acme"`, and returns `RateLimitPolicy.Unlimited`. The same applies to the pair scopes: `{"scope":"Tenant_Model","target":"acme"}` (no separator) is accepted and lands in `TenantModels` under a key that can never match a `tenant\|model` lookup. |
| **Evidence** | `IsKnown`: `All.Contains(scope, StringComparer.OrdinalIgnoreCase)`. `IsSingleton`: `scope is Global or AuthFailure or Anonymous` — ordinal. `TryValidateTarget` gates the singleton and pair checks on those two methods. `GatewayConfigStore.Single` returns `RateLimitPolicy.Unlimited` when the singleton target is absent. |
| **Impact** | Security / operational. An operator can configure the anonymous tier or the auth-failure tier, see it persisted and rendered in the console, and have **no limit applied at all**. For `auth_failure` and `anonymous` this means the control silently falls back to the *default* tier (3000 rpm in the shipped `appsettings.json`). It is a "believed to be protected, is not" failure. |
| **Reproduction** | `curl -X PUT .../admin/api/rate-limits -d '{"enabled":true,"adaptiveEnabled":false,"default":{...},"plans":{},"rules":[{"scope":"Anonymous","target":"acme","rpm":10,"burst":0,"maxConcurrentStreams":0}]}'` → `200`. `GET` returns the rule. Anonymous traffic is still held to the default tier. |
| **Root cause** | Two comparison policies for the same value in the same validator. The scope string is persisted verbatim (`ToDefinition()` only trims), so the divergence survives into storage. |
| **Recommendation** | Normalise `rule.Scope` to its canonical lower-case spelling in `AdminRateLimitRuleDto.ToDefinition()` (and in `AdminRateLimitWindowPreviewDto`), **and** make `IsSingleton`/`IsPair` use `OrdinalIgnoreCase` as defence in depth. Add a migration/startup pass that canonicalises existing `rate_limit_rules.Scope` values. Test: every scope name in `All`, in three casings, round-trips to the same effective configuration. |
| **Priority rationale** | High: it silently disables a security control (`auth_failure`, `anonymous`), it is reachable through the public admin API, and there is no signal anywhere that the rule is inert. Not Critical only because the admin console's own `<select>` always emits lower-case, so it requires API use or an imported configuration. |

**[re-confirmed: GW-25 — still open at `0a2495d`]**

---

#### RL-003 · A suspending window on a `tenant` rule removes the tenant's stream-concurrency cap

| Field | Content |
|---|---|
| **ID** | RL-003 |
| **Title** | "Pause this rule" on a tenant override yields *unlimited* concurrent streams instead of the plan's cap |
| **Severity** | **High** (confirmed) |
| **Affected area** | [RateLimitWindowDefinition.cs:69](src/33pol.Core/RateLimiting/RateLimitWindowDefinition.cs#L69) · [RateLimitPolicyResolver.Compose](src/33pol.Policy/RateLimiting/RateLimitPolicyResolver.cs) · [RateLimitScheduleProjection.Map](src/33pol.Core/RateLimiting/RateLimitScheduleProjection.cs) · Admin → Rate limits → window editor ("Pause this rule instead") |
| **Description** | `RateLimitWindowDefinition.ToPolicy()` encodes a suspending window as `RateLimitPolicy.Unlimited`, i.e. `(0, 0, 0)`. The projection writes that into `TenantOverrides[target]`. `ResolveTenantTier` then finds an override *present* and calls `Compose(baseTier, (0,0,0))`. Because `overrideTier.Rpm == 0`, `Compose` takes the "keep the base rate, apply only the override's stream cap" branch: `baseTier with { MaxConcurrentStreams = Math.Max(0, 0) }` — and `0` means **unlimited** in `RateLimitPolicy`. The tenant therefore gets the plan's rate (correct) and *no stream cap at all* (wrong — it should inherit the plan's). The documented contract for `Suspend` is "the rule enforces nothing while the window is active, **as if it did not exist**"; the UI says "nothing is enforced by it while the window runs". As-if-it-did-not-exist would give the tenant the plan's stream cap. |
| **Evidence** | `ToPolicy() => Suspend ? RateLimitPolicy.Unlimited : new RateLimitPolicy(Rpm, Burst, MaxConcurrentStreams);`. `Compose(baseTier, overrideTier) => overrideTier.Rpm > 0 ? Clamp(overrideTier) : baseTier with { MaxConcurrentStreams = Math.Max(0, overrideTier.MaxConcurrentStreams) };`. `RateLimitPolicy.EnforcesConcurrency => MaxConcurrentStreams > 0`. Contrast with the other scopes: `global`, `model`, `api_key`, `tenant_model`, `api_key_model` all guard on `!tier.EnforcesNothing`, so a suspended rule there is correctly dropped. |
| **Impact** | Availability / fairness. During the suspension window one tenant may open unlimited concurrent streaming responses and occupy the entire per-model bulkhead (`MaxConcurrentForwardsPerModel: 256`), starving every other tenant. The operator's intent was the opposite — to *relax* a restriction temporarily, not to remove a safety cap they never touched. |
| **Reproduction** | 1. Configure `default` with `maxConcurrentStreams: 5`. 2. Add a `tenant` rule for tenant T with `rpm: 100, burst: 0, maxConcurrentStreams: 2` and a `once` window covering now, marked `suspend`. 3. `GET /admin/api/rate-limits/schedule` shows the rule paused. 4. Open 20 concurrent streaming requests as T — all are admitted (expected: 5, from the default tier). |
| **Root cause** | Suspension is encoded in the data (`(0,0,0)`) rather than carried as a flag, and `(0,0,0)` is ambiguous with the legitimate "rpm 0 = inherit the rate, apply only my stream cap" tenant-override shape that `Compose` exists to serve. |
| **Recommendation** | Carry suspension explicitly. Either (a) have the projection **omit** the entry from `TenantOverrides` when the active window suspends (which is literally "as if it did not exist"), or (b) add a `Suspended` flag to the projected policy that `Compose` honours by returning `baseTier` unchanged. (a) is smaller and needs no new type surface. Test: a suspended tenant window leaves the plan's `maxConcurrentStreams` intact, for both a plan-backed and a default-backed tenant. |
| **Priority rationale** | High: it produces the exact opposite of the documented and UI-promised behaviour, on a scheduled/automatic trigger, with a real availability consequence. It is not Critical because it requires a scheduled suspend window on a `tenant`-scope rule specifically. |

**[re-confirmed: GW-23 — still open at `0a2495d`]**

---

#### RL-004 · Uncapped O(n²) overlap scan on the window-preview endpoint

| Field | Content |
|---|---|
| **ID** | RL-004 |
| **Title** | `POST /admin/api/rate-limits/windows/preview` computes a validation error and then ignores it, running an uncapped quadratic scan over client-supplied windows |
| **Severity** | **High** (confirmed) |
| **Affected area** | [AdminRateLimitEndpoints.PreviewWindow](src/33pol.Api/Endpoints/AdminRateLimitEndpoints.cs) · [RateLimitWindowPreviewBuilder.Build:266-295](src/33pol.Core/RateLimiting/RateLimitScheduleReport.cs#L266-L295) · [RateLimitConfigValidation.FindWindowOverlaps](src/33pol.Core/Configuration/RateLimitConfigValidation.cs) |
| **Description** | The endpoint builds a `RateLimitRuleDefinition` directly from `request.Windows` with **no count cap** — the DTO's `List<AdminRateLimitWindowDto> Windows` is unbounded. `RateLimitWindowPreviewBuilder.Build` calls `TryValidateRules([rule], out var validationError)` and stores the result in a local `error`, but **does not return**; it proceeds to `FindWindowOverlaps(windows)`, which is a nested `for i / for j` over all windows, and for weekly windows the inner comparison enumerates `MinuteOfWeekSpans` for each side. The 16-window cap exists only inside `TryValidateSchedule`, whose result is discarded here. |
| **Evidence** | Endpoint: `Schedule = request.Windows.Where(static w => w is not null).Select(static w => w.ToDefinition()).ToArray()` — no cap. `Build`: `if (!RateLimitConfigValidation.TryValidateRules([rule], out var validationError)) { error = validationError; }` followed unconditionally by `var overlaps = RateLimitConfigValidation.FindWindowOverlaps(windows)…`. `FindWindowOverlaps`: `for (var i = 0; i < windows.Count; i++) for (var j = i + 1; …)`. |
| **Impact** | Denial of service. The request body limit is 26 MB (`Gateway:Resilience:MaxRequestBodyBytes`), which holds on the order of 10⁵ minimal window objects. 10⁵ windows → ~5×10⁹ pair comparisons with a `MinuteOfWeekSpans` enumeration inside — minutes to hours of CPU on a request thread. The control-plane budget permits 600 such requests per minute per operator tenant. One authenticated operator key (or a leaked one) can wedge the gateway process. |
| **Reproduction** | `POST /admin/api/rate-limits/windows/preview` with `{"scope":"model","target":"m","rpm":60,"burst":0,"maxConcurrentStreams":0,"candidate":"w0","windows":[ …50000 weekly windows… ]}` and observe CPU saturation with no response. |
| **Root cause** | A validation result computed for reporting rather than for control flow, combined with an endpoint that does not bound a collection it forwards into a quadratic algorithm. |
| **Recommendation** | (a) In `RateLimitWindowPreviewBuilder.Build`, return the invalid preview immediately when `TryValidateRules` fails — the overlap/rank fields are meaningless for an invalid rule anyway. (b) Independently, reject `request.Windows.Count > RateLimitConfigValidation.MaxWindowsPerRule` in the endpoint before constructing the definition. (c) Consider a `[FromBody]` size guard on the admin API generally. |
| **Priority rationale** | High: single-request process-level DoS, no special conditions, trivially reachable. Not Critical because it requires an authenticated **operator-tenant** key — the narrowest role in the system. |

*New finding — not present in the 2026-09-13 audit.*

---

#### RL-005 · Auth-failure and anonymous budgets are peeked, not taken — the enforced rate is multiplied by client concurrency

| Field | Content |
|---|---|
| **ID** | RL-005 |
| **Title** | Peek-then-debit-after-pipeline lets a whole round of concurrent requests through on a single token |
| **Severity** | **High** (confirmed) |
| **Affected area** | [AuthFailureRateLimitMiddleware.InvokeAsync](src/33pol.Proxy/Middleware/AuthFailureRateLimitMiddleware.cs) · [AnonymousAdmissionGuardMiddleware.InvokeAsync](src/33pol.Proxy/Middleware/AnonymousAdmissionGuardMiddleware.cs) · [InMemoryDistributedRateLimitStore.PeekRequest / DebitRequest](src/33pol.Policy/RateLimiting/InMemoryDistributedRateLimitStore.cs) |
| **Description** | Both middlewares decide admission with `PeekRequest` (which does **not** consume a token) and only charge with `DebitRequest` *after* `await _next(context)` has run the entire downstream pipeline. `PeekRequest` returns `IsAcquired` whenever `tokens >= 1`, so every request that peeks before any of them debits is admitted. The auth-failure bucket refills at one token per second at the shipped 60 rpm tier; each arriving token therefore admits **one full round of however many requests the attacker has in flight**, not one request. `DebitRequest` uses `TokenOperation.ForceTake`, which floors the bucket at zero rather than going negative, so the surplus is never repaid on the next window. |
| **Evidence** | `AuthFailureRateLimitMiddleware`: `var budget = _rateLimitStore.PeekRequest(partitionKey, policy, now); if (!budget.IsAcquired && !await ProvesCredential…) { reject } … await _next(context); if (WasCredentialRejected(context)) { _rateLimitStore.DebitRequest(partitionKey, policy, now); }`. `RequestWindowState.Apply`: `else if (!hasToken && operation == TokenOperation.ForceTake) { _tokens = 0; }`. Shipped tier: `AuthFailure { Rpm = 60, Burst = 20 }` → refill exactly 1.0 token/second. Contrast `RateLimitMiddleware`, which uses the atomic `TryAcquireAll` and is not affected. |
| **Impact** | Security. The one control that bounds credential guessing per address block is weakened by a factor equal to the attacker's concurrency. With 100 in-flight requests (one HTTP/2 connection at Kestrel's default stream limit) the sustained guessing rate becomes ~6,000/minute per address instead of the configured 60 — and each of those guesses that misses the validator's negative cache is a database read, which is the work the probe-budget mechanism was specifically designed to shed. The same pattern in `AnonymousAdmissionGuardMiddleware` amplifies uncredentialed 401-generating traffic against the anonymous tier. |
| **Reproduction** | Point 100 concurrent connections at `POST /v1/chat/completions` with distinct random `Authorization: Bearer sk-…` values from one address. Observe that far more than `AuthFailure.Rpm + Burst` requests per minute reach `ApiKeyAuthenticationHandler`; count `gateway_rate_limit_rejections_total{reason="auth_failure"}` against the number of requests actually validated. |
| **Root cause** | The peek/debit split exists for a good reason — the budget must be charged for the *outcome* (a 401), not the attempt, and only the security layer knows the outcome. But "decide on a peek" and "charge later" together remove atomicity from the admission decision. |
| **Recommendation** | Take the token atomically on the way in with `TryAcquireRequest`, then **refund** it in the `finally` when the request was *not* a credential rejection. This preserves the "charge only for refusals" semantics while making the decision atomic. The refund path already exists (`RefundRequest`) and is already capped at capacity, so an unmatched refund cannot inflate the bucket. Apply the same change to `AnonymousAdmissionGuardMiddleware`. Add a concurrency test: N concurrent bad-credential requests against a bucket with 1 token admit exactly 1. |
| **Priority rationale** | High: it materially weakens a security control by an attacker-chosen factor, on the unauthenticated edge of the system, and the remediation is small and uses machinery that already exists. Not Critical because the absolute ceiling is still bounded (the probe budget refuses everything past `AuthFailure × 10` with no validation at all), so it degrades rather than removes the protection. |

*New finding — not present in the 2026-09-13 audit.*

---

### Medium

---

#### RL-006 · Admin rate-limit writes have no optimistic concurrency — concurrent edits silently clobber

| Field | Content |
|---|---|
| **ID** | RL-006 |
| **Severity** | **Medium** (confirmed) |
| **Affected area** | [RateLimitConfigAdminService.UpdateAsync](src/33pol.Policy/Admin/RateLimitConfigAdminService.cs) · [RateLimitSettingsRepository.SaveAsync](src/33pol.Persistence/Repositories/RateLimitSettingsRepository.cs) · `PUT /admin/api/rate-limits` · admin console `saveRateLimits()` |
| **Description** | `UpdateAsync` reads `StoredRateLimits` (for schedule preservation), then `SaveAsync` deletes and re-inserts the entire plan and rule sets and does `version.Version += 1` as a read-modify-write. There is no `RowVersion`/concurrency token on `ConfigVersionEntity`, no `If-Match` on the endpoint, and the console's `buildRateLimitsPayload` always sends the **complete** rule array. Two operators who open Settings → Rate limits at the same time and both save will each write their own full rule set; the second write wins entirely and the first operator gets a success toast. |
| **Evidence** | `SaveAsync`: `dbContext.RateLimitRules.RemoveRange(existingRules); foreach (var rule in rules) { …Add… }` and `version.Version += 1;`. `PutAsync` takes no ETag. `buildRateLimitsPayload`: `rules` is always an array, `schedule` is always an array. |
| **Impact** | Data integrity / operational. Silent loss of a colleague's rate-limit configuration, including security-relevant rules (`auth_failure`, `anonymous`). There is also a narrower race inside `UpdateAsync` itself: the `stored.Schedules` lookup used to preserve unspecified windows is read before the save, so a concurrent write between the two reattaches stale windows. |
| **Recommendation** | Return the config version as an `ETag` on `GET /admin/api/rate-limits`; require `If-Match` on `PUT` and answer `409 Conflict` on mismatch. Add a `RowVersion` concurrency token to `ConfigVersionEntity` so the DB enforces it even if a caller omits the header. In the console, surface the 409 as "someone else changed this — reload and reapply" rather than a generic error. |
| **Priority rationale** | Medium: real data loss, but it needs two concurrent operators, and the blast radius is recoverable configuration rather than customer data. |

**[re-confirmed: GW-30 — still open at `0a2495d`]**

---

#### RL-007 · `GET /admin/api/rate-limits/schedule` returns an uncapped occurrence list

| Field | Content |
|---|---|
| **ID** | RL-007 |
| **Severity** | **Medium** (confirmed mechanism; impact requires validation at scale) |
| **Affected area** | [AdminRateLimitEndpoints.GetSchedule](src/33pol.Api/Endpoints/AdminRateLimitEndpoints.cs) · [RateLimitScheduleReportBuilder.Build:103-124](src/33pol.Core/RateLimiting/RateLimitScheduleReport.cs#L103-L124) |
| **Description** | The endpoint caps the calendar range at 62 days and clamps `take` (`Math.Clamp(take, 1, MaxTransitions)`) — but `take` only limits the **transitions** list. The `occurrences` list is built by enumerating every occurrence of every window of every rule across the whole range, with no cap and no paging, and is serialised in full. |
| **Evidence** | `Build`: `foreach (var rule in rules) foreach (var window in windows) foreach (var occurrence in OccurrencesBetween(window, from, to)) occurrences.Add(…)`. `var limit = Math.Clamp(take, 1, MaxTransitions);` is applied to `transitions` only; `occurrences` is returned whole. `OccurrencesBetween` for a weekly window yields one occurrence per matching day, so a 7-day window over 62 days yields 62. |
| **Impact** | Memory / availability. Worst case at the configured ceilings: `MaxRules` (2 000) × `MaxWindowsPerRule` (16) × 62 ≈ **1.98 million** `ScheduleOccurrence` records in a single JSON response. Well before that it is a large allocation on an operator-triggered path, repeatable at 600 rpm. At the scale most deployments run (tens of rules) this is harmless — which is why it is Medium and flagged for validation rather than asserted as an outage. |
| **Recommendation** | Cap `occurrences` the same way transitions are capped, with a `totalOccurrences` + `truncated` pair so the console can say so; or page it. Independently, narrow the default range from 7 days and reject ranges that would exceed a computed occurrence budget. |
| **Priority rationale** | Medium: bounded by operator authentication and only material at high rule counts, but the fix is a two-line symmetry with code that already exists next to it. |

*New finding.*

---

#### RL-008 · Cross-kind windows with equal explicit priority are never checked for overlap

| Field | Content |
|---|---|
| **ID** | RL-008 |
| **Severity** | **Medium** (confirmed) |
| **Affected area** | [RateLimitConfigValidation.FindWindowOverlaps](src/33pol.Core/Configuration/RateLimitConfigValidation.cs) |
| **Description** | `FindWindowOverlaps` short-circuits on `!string.Equals(a.Kind, b.Kind, …)` **before** it compares ranks, so a `once` window and a `weekly` window are never compared. With default ranks that is safe (`once` = 200 outranks `weekly` = 100). But `Priority` is operator-settable in `0…1000`, so two windows of different kinds can carry the **same** explicit rank, both be active at the same instant, and pass validation. The evaluator then resolves the tie by start time and name — which is precisely the arbitrary outcome the equal-rank refusal exists to prevent for same-kind windows. |
| **Evidence** | `FindWindowOverlaps`: the `Kind` test precedes the `a.Rank != b.Rank` test. `RateLimitWindowDefinition.Rank => Priority ?? (IsOnce ? 200 : 100)`. `RateLimitScheduleEvaluator` (`:360`): `if (candidate.Rank != incumbent.Rank) return candidate.Rank > incumbent.Rank;` then falls through to a start-time/name tie-break. |
| **Impact** | Correctness / predictability. Which tier is in force during the overlap depends on window naming, which nobody would guess. The admin console's preview shows no clash. |
| **Recommendation** | Move the `a.Rank != b.Rank` test above the `Kind` test and make the overlap computation handle a `once`/`weekly` pair (project the once window's span onto minutes-of-week for the comparison, or conservatively treat any temporal intersection as an overlap). Add a test for `once` + `weekly` at equal explicit priority. |
| **Priority rationale** | Medium: silent ambiguity in scheduled enforcement, but it needs an operator to set matching explicit priorities across kinds. |

**[re-confirmed: GW-24 — still open at `0a2495d`]**

---

#### RL-009 · The rate-limit audit record carries counts, not content

| Field | Content |
|---|---|
| **ID** | RL-009 |
| **Severity** | **Medium** (confirmed) |
| **Affected area** | [AdminRateLimitEndpoints.PutAsync](src/33pol.Api/Endpoints/AdminRateLimitEndpoints.cs) |
| **Description** | The audit entry for `rate_limits.update` records `Enabled`, `AdaptiveEnabled`, the default tier's three numbers, `PlanCount`, `RuleCount` and `WindowCount`. It does **not** record which rules changed, their targets, or their tiers — and it is written only on success, so refused attempts leave no trace. |
| **Evidence** | `audit.LogAdminAction("rate_limits.update", new AuditLogEntry(…, new { request.Enabled, request.AdaptiveEnabled, request.Default.Rpm, …, PlanCount = request.Plans.Count, RuleCount = rules?.Length, WindowCount = … }));` — placed after the `if (!result.Success) return …` guard. |
| **Impact** | Forensics / compliance. After an incident ("why was tenant X unlimited last Tuesday?") the audit trail can say a rate-limit write happened and how many rules there were, but not what changed. Because the write replaces the rule set wholesale, `RuleCount` staying the same does not mean nothing changed. Failed/unauthorised attempts are invisible. |
| **Recommendation** | Record a structured diff against the stored configuration (added / removed / changed rule identities with before-and-after tiers), which `RateLimitConfigAdminService` already has both sides of. Emit an audit entry on validation failure and on the `503`/`500` paths too, with the reason. Keep tier numbers but drop nothing else — the payload is small and bounded by `MaxRules`. |
| **Priority rationale** | Medium: no direct security or availability impact, but rate limits are a security-relevant control surface and an audit trail that cannot answer "what changed" is not doing its job. |

*New finding.*

---

#### RL-010 · The control-plane budget is per-tenant, shared by all operator sessions, and not runtime-adjustable

| Field | Content |
|---|---|
| **ID** | RL-010 |
| **Severity** | **Medium** (confirmed mechanism; threshold requires validation) |
| **Affected area** | [RateLimitMiddleware.AcquireControlPlane](src/33pol.Proxy/Middleware/RateLimitMiddleware.cs) · [RateLimitKeys.ControlPlane](src/33pol.Core/RateLimiting/RateLimitKeys.cs) · `RateLimitingOptions.ControlPlane` · admin console poll loop |
| **Description** | Control-plane requests are bucketed by `RateLimitKeys.ControlPlane(subject.PartitionKey)`, and `PartitionKey` for an authenticated caller is the **tenant id**. Every operator key belongs to the operator tenant, so *all* console sessions, wallboards and scripted admin clients share one 600 rpm + 120 burst bucket. The console's steady-state cost is roughly 68 rpm per open Overview tab (`loadSummary` + `loadRequests` every 2 s, `loadHealth` every 10 s, `loadOverviewSlow` every 30 s) and ~36 rpm elsewhere. Eight or nine Overview tabs saturate the budget. The tier is read **once** in the constructor from `appsettings` and is deliberately not admin-editable, so recovery requires a process restart. |
| **Evidence** | `ControlPlane(string partitionKey) => "cp:" + partitionKey;` with `partitionKey` = tenant id. `_controlPlanePolicy` assigned in the constructor with the comment "Read once: it is an appsettings guard rail rather than an admin-editable tier". `admin-app.js:1055-1079`: `setInterval(…, 2000)` issuing `loadSummary` + `loadRequests` per tick. Shipped `ControlPlane { Rpm = 600, Burst = 120 }`. |
| **Impact** | Operational. A busy NOC (a wallboard plus several operators, or a CI job polling admin endpoints) can lock every operator out of the console with `429`, exactly when they most need it, with no in-band remedy. The design note that it is "sized so that nothing a console does can reach it" holds for one console, not for N. |
| **Recommendation** | Key the control-plane bucket on the **API key id** rather than the tenant (falling back to the address block when anonymous) — that preserves the "console polling cannot spend inference budget" property while making one runaway session unable to lock out the others. Export a metric for control-plane refusals separately (the reason tag `rate_limit:control_plane` already exists — add an alert). Document the expected per-session cost in the runbook. |
| **Priority rationale** | Medium: availability of the control plane itself, self-inflicted rather than attacker-driven, and recoverable by restart — but it is the failure mode that removes the operator's ability to fix anything else. |

*New finding.*

---

#### RL-011 · Plan-cache ceiling check calls `ConcurrentDictionary.Count` on every cache miss

| Field | Content |
|---|---|
| **ID** | RL-011 |
| **Severity** | **Medium** (confirmed) |
| **Affected area** | [RateLimitPlanResolver.Resolve](src/33pol.Policy/RateLimiting/RateLimitPlanResolver.cs) |
| **Description** | On every cache miss the resolver evaluates `if (_cache.Count >= MaxCacheEntries) { _cache.Clear(); }`. `ConcurrentDictionary<K,V>.Count` acquires **all** bucket locks to produce an exact count; it is not a cheap read. For a steady workload misses are rare, but the cache key includes `subject.PartitionKey`, which for anonymous traffic is a client address block — so an IP-diverse flood makes *every request* a miss, and therefore every request takes all locks on a 20 000-entry dictionary and then rebuilds a plan (a `List<RateLimitRule>` plus an array). Past the ceiling the cache is dropped wholesale, so legitimate tenants' plans are rebuilt too, repeatedly. |
| **Evidence** | `if (_cache.TryGetValue(key, out var cached)) return cached; var plan = Build(…); if (_cache.Count >= MaxCacheEntries) { _cache.Clear(); } _cache[key] = plan;`. The same class of problem is already solved elsewhere in this subsystem with an `Interlocked` side-counter — see `InMemoryDistributedRateLimitStore._requestPartitionCount` ("`ConcurrentDictionary.Count` takes every bucket lock…") and `AdaptiveRateLimitGovernor._backoffCount`. |
| **Impact** | Performance / amplification. Turns a distributed anonymous flood into a lock-convoy on the admission path — the opposite of what a limiter should do under load. |
| **Reproduction** | Drive `POST /v1/chat/completions` from a large set of distinct source addresses (or with `ForwardedHeaders` trusted and varied `X-Forwarded-For`) against a public model and compare p99 admission latency with the same load from a single address. |
| **Recommendation** | Maintain an `Interlocked` counter next to the dictionary exactly as the store and the governor already do, and check that instead. Consider not clearing wholesale under churn — an "ignore new entries past the ceiling" policy (as the usage tracker uses) keeps the hot tenants cached. |
| **Priority rationale** | Medium: performance under adversarial load rather than a correctness defect, but the fix is mechanical and the pattern is already established two files away. |

*New finding.*

---

#### RL-012 · No alerting for rate limiting, and no metric for the "limits are not being enforced" condition

| Field | Content |
|---|---|
| **ID** | RL-012 |
| **Severity** | **Medium** (confirmed) |
| **Affected area** | `deploy/prometheus/alerts/33pol.yml`, `33pol-writer.yml` · [InMemoryDistributedRateLimitStore.Compact](src/33pol.Policy/RateLimiting/InMemoryDistributedRateLimitStore.cs) · `deploy/grafana/dashboards/33pol-gateway.json` |
| **Description** | There are **no** rate-limit alert rules. The eight existing alerts cover error rate, backend health, usage parse failures, billing reconciliation and circuit breakers. Nothing watches `gateway_rate_limit_partitions` against the `ceiling` series (which the exporter deliberately publishes precisely so an alert can be written on the ratio), nothing watches `gateway_rate_limit_rejections_total` by reason, and nothing watches the `auth_failure` reason — which is the credential-stuffing signal. Separately, the one state that means **limits have stopped being enforced** — forced eviction of partitions with budget already spent, so each starts full again on its next request — is reported *only* as an edge-triggered log line. There is no counter or gauge for it, so it cannot be alerted on at all. |
| **Evidence** | `grep alert: deploy/prometheus/alerts/` → 8 alerts, none rate-limit related. Grafana has one rate-limit panel (`sum by (reason) (rate(gateway_rate_limit_rejections_total[$__rate_interval]))`). `Compact`: `if (forced > 0) { if (!_forcingEviction) { _logger.LogWarning("…limits are not being fully enforced…"); } }` — no metric emitted. |
| **Impact** | Observability. A partition-table flood silently degrades enforcement and the only evidence is a single log line that is emitted once and never repeated for the duration of the flood. A credential-stuffing campaign produces a clear metric signal that nobody is watching. |
| **Recommendation** | (a) Add a `gateway_rate_limit_forced_evictions_total` counter (and/or a `gateway_rate_limit_enforcement_degraded` gauge) in `Compact`, and alert on it. (b) Add alert rules: partitions > 80 % of ceiling; `auth_failure` rejection rate above a baseline; `rate_limit:control_plane` rejections non-zero (see RL-010); sustained `stream_concurrency:*` rejections. (c) Give each a runbook entry in `docs/runbooks/rate-limit-admin.md`. |
| **Priority rationale** | Medium: no defect, but the subsystem's most dangerous silent failure has no machine-readable signal, which turns a detectable condition into an undetectable one. |

*New finding (the 2026-09-13 audit noted the missing partition-ceiling alert under item 12; the missing metric is new).*

---

#### RL-013 · An `anonymous` rule's `burst` is silently discarded when `rpm` is 0

| Field | Content |
|---|---|
| **ID** | RL-013 |
| **Severity** | **Medium** (confirmed) |
| **Affected area** | [RateLimitPolicyResolver.ResolveAnonymousTier / Compose](src/33pol.Policy/RateLimiting/RateLimitPolicyResolver.cs) · [RateLimitConfigValidation.TryValidateRules](src/33pol.Core/Configuration/RateLimitConfigValidation.cs) |
| **Description** | `ResolveAnonymousTier` composes through the same `Compose` helper the tenant scope uses, so an `anonymous` rule with `rpm: 0` keeps the default tier's rate and contributes only its stream cap — its `burst` is dropped. Validation refuses that shape for the `tenant` scope with an explicit message ("inherits the plan or default rate when rpm is 0; set burst to 0 as well") but **not** for `anonymous`, even though the composition is identical. |
| **Evidence** | `ResolveAnonymousTier(…) => !authenticationRequired \|\| rateLimits.Anonymous.EnforcesNothing ? Clamp(rateLimits.Default) : Compose(Clamp(rateLimits.Default), rateLimits.Anonymous);`. `TryValidateRules`: the refusal is guarded by `string.Equals(rule.Scope, RateLimitScopeNames.Tenant, StringComparison.OrdinalIgnoreCase)`. |
| **Impact** | Configuration fidelity. An operator writing `{"scope":"anonymous","target":"*","rpm":0,"burst":50,"maxConcurrentStreams":2}` believes they have granted anonymous callers a 50-request burst; they have granted them the **default tier's** rate and burst (3 000 + 500 as shipped). Strictly looser than intended, on the unauthenticated edge. |
| **Recommendation** | Extend the `rpm == 0 ⇒ burst == 0` refusal to every scope that goes through `Compose` — at minimum `tenant` and `anonymous`. This is the same edit RL-001 calls for; doing both at once is one change. |
| **Priority rationale** | Medium: a misconfiguration that loosens an unauthenticated-traffic control without saying so, but it needs the operator to write an unusual shape. |

*New finding.*

---

#### RL-014 · An `auth_failure` rule with `rpm: 0` is accepted, does nothing, and falls back to the default tier

| Field | Content |
|---|---|
| **ID** | RL-014 |
| **Severity** | **Medium** (confirmed) |
| **Affected area** | [RateLimitPolicyResolver.ResolveAuthFailureTier](src/33pol.Policy/RateLimiting/RateLimitPolicyResolver.cs) · [RateLimitConfigValidation.TryValidateRules](src/33pol.Core/Configuration/RateLimitConfigValidation.cs) · [AuthFailureRateLimitMiddleware](src/33pol.Proxy/Middleware/AuthFailureRateLimitMiddleware.cs) |
| **Description** | `auth_failure` is a rate-only control: the middleware only ever calls `PeekRequest`/`DebitRequest`, and `Widen` explicitly sets `MaxConcurrentStreams: 0`. A rule written as `{"scope":"auth_failure","target":"*","rpm":0,"burst":0,"maxConcurrentStreams":5}` passes validation (it is not `EnforcesNothing`, because the stream cap is non-zero, and the `rpm 0 ⇒ burst 0` rule only applies to `tenant`). It then produces `AuthFailure` with `EnforcesRate == false`, so `ResolveAuthFailureTier` falls back to `Clamp(rateLimits.Default)` — **3 000 rpm + 500 burst** as shipped. The stream cap is ignored entirely. |
| **Evidence** | `ResolveAuthFailureTier(…) => rateLimits.AuthFailure.EnforcesRate ? Clamp(rateLimits.AuthFailure) : Clamp(rateLimits.Default);`. `Widen(policy, multiplier) => new(Multiply(policy.Rpm, …), Multiply(policy.Burst, …), MaxConcurrentStreams: 0);`. |
| **Impact** | Security. An operator who configures an `auth_failure` rule and sees it saved gets **50× looser** credential-guessing protection than the shipped default, with no warning anywhere. The same mechanism applies when a scheduled window on the `auth_failure` rule is marked `suspend`. |
| **Recommendation** | Refuse `auth_failure` (and `anonymous`) rules whose only non-zero field is `maxConcurrentStreams`, with a message saying the scope is rate-only. Log a startup warning whenever `AuthFailure.EnforcesRate` is false while authentication is required (see RL-015). |
| **Priority rationale** | Medium rather than High: it requires the operator to write a rule the console's tier editor does not naturally produce, and the resulting rate is still finite. |

*New finding.*

---

### Low

---

#### RL-015 · No startup warning when the auth-failure tier is unconfigured

| Field | Content |
|---|---|
| **ID** | RL-015 · **Severity** Low (confirmed) |
| **Affected area** | [GatewayAdmissionLimitsStartupLogger](src/33pol.App/DependencyInjection/GatewayAdmissionLimitsStartupLogger.cs) · [GatewayDbBootstrap.SeedRateLimitSettingsAsync](src/33pol.Persistence/Bootstrap/GatewayDbBootstrap.cs) |
| **Description / Evidence** | The rule seed is one-shot, stamped by `defaults.RulesSeededAt`. A database seeded by a build that predates a scope never receives that scope's rule. The startup logger handles exactly this for `anonymous` — it warns when a `publicAccess` model exists and `rateLimits.Anonymous.EnforcesNothing`. There is no equivalent check for `AuthFailure` (`grep -i auth_failure` over the 116-line file returns nothing), even though the same one-shot seed applies and the fallback (the default tier) is far looser than the purpose-built one. |
| **Impact** | An upgraded deployment holds credential guessing to the default tier with no signal. |
| **Recommendation** | Add the symmetric warning: `rateLimits.Enabled && authState.IsAuthenticationRequired && !rateLimits.AuthFailure.EnforcesRate` → warn with the default tier's numbers and the fix (add an `auth_failure` rule). Consider making the seed additive for newly-introduced singleton scopes rather than strictly one-shot. |

---

#### RL-016 · The stage-two refund re-resolves the plan and can refund against a different rule set

| Field | Content |
|---|---|
| **ID** | RL-016 · **Severity** Low (confirmed, narrow window) |
| **Affected area** | [RateLimitMiddleware.AcquireModelScopes](src/33pol.Proxy/Middleware/RateLimitMiddleware.cs) |
| **Description / Evidence** | `AcquireModelScopes` refunds via `_rateLimitStore.RefundAll(_planResolver.Resolve(subject, modelId: null).IdentityRules, now)` — a **re-resolution**, not the rule set stage one actually charged. Between the two there is an `await` spanning a full body buffer-and-parse (up to 26 MB from the network). If an admin write or a schedule transition lands in that window, the plan cache key changes and the refund is computed against different partition keys and capacities. |
| **Impact** | A tenant may keep a debit it should have had refunded, or have a refund applied to a bucket it did not spend. Self-correcting within one refill window; no accumulation (refunds are capped at capacity). |
| **Recommendation** | Capture the stage-one `RateLimitPlan` reference (not the span) before the await and refund from it. The plan object is immutable and already cached, so holding the reference costs nothing. |

---

#### RL-017 · No standard `RateLimit-*` headers on the refusal path

| Field | Content |
|---|---|
| **ID** | RL-017 · **Severity** Low (confirmed, by design) |
| **Affected area** | [GatewayHeaders](src/33pol.Core/Errors/GatewayHeaders.cs) · [RateLimitResponseHeaders.Write](src/33pol.Proxy/Routing/RateLimitResponseHeaders.cs) |
| **Description / Evidence** | Budget headers are vendor-prefixed (`X-33pol-RateLimit-Limit/Remaining/Reset/Scope/Adaptive`). The documented reason is sound: upstream provider headers are copied onto the response *after* this middleware runs, so an unprefixed name would be silently overwritten by a number about a different limit. But that collision **cannot occur on a refusal** — a 429 written by the gateway never reaches an upstream. OpenAI-compatible SDKs read `x-ratelimit-*`, and generic clients read the IETF `RateLimit-Limit` / `RateLimit-Remaining` / `RateLimit-Reset` family; both get nothing. `Retry-After` is emitted correctly, so retry behaviour itself is fine. |
| **Impact** | Client-side pacing and dashboards built against standard header names see no gateway budget information. |
| **Recommendation** | On the rejection path only (`RejectAsync`, `RejectControlPlaneAsync`, the anonymous guard), additionally emit the standard `RateLimit-Limit` / `RateLimit-Remaining` / `RateLimit-Reset` names alongside the vendor-prefixed ones. Document the split in the runbook. |

---

#### RL-018 · The console silently drops draft rule rows with an empty target

| Field | Content |
|---|---|
| **ID** | RL-018 · **Severity** Low (confirmed) |
| **Affected area** | [admin-app.js `buildRateLimitsPayload`](src/33pol.App/wwwroot/admin/admin-app.js) |
| **Description / Evidence** | `const rules = (cfg.rules \|\| []).filter((row) => String(row.target ?? '').trim() !== '')` — rows with a blank target are removed from the payload rather than surfaced. Because the server replaces the rule set wholesale, a row that reaches this state is **deleted**, and the operator is told "Rate limits saved." |
| **Impact** | Silent loss of a half-edited rule. Narrow, since the rule drawer validates target before commit. |
| **Recommendation** | Block the save and highlight the offending row instead of filtering it, or at minimum include the count in the success toast ("saved; 1 incomplete rule was not included"). |

---

#### RL-019 · `RefillPerSecond` floors at 1 rpm

| Field | Content |
|---|---|
| **ID** | RL-019 · **Severity** Low on its own (the enabler for RL-001) |
| **Affected area** | [InMemoryDistributedRateLimitStore.RefillPerSecond](src/33pol.Policy/RateLimiting/InMemoryDistributedRateLimitStore.cs#L378) |
| **Description / Evidence** | `Math.Max(1, policy.Rpm) / 60.0`. The floor exists to keep `(1.0 - _tokens) / refillPerSecond` finite, but it silently converts "no rate limit" into "one per minute". |
| **Recommendation** | Once `EnforcesRate` requires `Rpm > 0` (RL-001), this method is only ever called with `Rpm >= 1` and the `Math.Max` can be replaced with an assertion. Keep a guard, but make it unreachable rather than load-bearing. |

---

#### RL-020 · The k6 rate-limit scenario does not validate enforcement

| Field | Content |
|---|---|
| **ID** | RL-020 · **Severity** Low (confirmed) |
| **Affected area** | `perf/k6/scripts/rate-limit-storm.js` |
| **Description / Evidence** | The only assertions are `status is 200 or 429` and, on a 429, that `X-33pol-Error-Code` contains `rate_limit` or `quota`. A gateway that admitted everything, or one that refused everything, both pass. Nothing measures the *enforced* rate against the configured tier, per-partition isolation, limiter overhead, or partition-table growth. |
| **Recommendation** | See §4 — add an accuracy scenario (observed admitted rate within a tolerance of configured rpm over a fixed window), an isolation scenario (two tenants, one over budget, assert the other is untouched), and a partition-churn scenario driving `gateway_rate_limit_partitions` toward the ceiling while asserting enforcement holds. |

---

### Informational

| ID | Title | Notes |
|---|---|---|
| **RL-021** | With authentication disabled, every authorization policy including `Operator` succeeds, so `PUT /admin/api/rate-limits` — including the `enabled` master switch — is reachable with no credential. | `GatewayAuthorizationHandler`: `if (!_authState.IsAuthenticationRequired) { context.Succeed(requirement); }`. This is an explicit, documented mode: `GatewayAuthenticationInitializer` **fails closed** outside Development unless `Gateway:Security:AllowAnonymous=true` is set deliberately. Recorded so the interaction with rate limiting is on the record, not as a defect. The control-plane budget and the anonymous guard still apply. |
| **RL-022** | The store is single-process in-memory; `IDistributedRateLimitStore` is a seam, not a distributed implementation. | Explicitly documented as intentional for a single-process gateway over an embedded database. Consequence worth stating plainly: **running N replicas multiplies every configured limit by N**, including `auth_failure` and `anonymous`. Nothing in the code or the Helm chart prevents or warns about a replica count > 1. |
| **RL-023** | `ForwardedHeaders:Enabled` is `false` by default and appears nowhere in `.env.example`, `docker-compose.yml`, or the Helm chart. | Behind docker's userland proxy or any ingress, `Connection.RemoteIpAddress` is the proxy, so **every** anonymous caller and every credential guesser shares one partition. The option's own documentation and two startup warnings cover this well; the gap is that the deployment assets never mention it. Recommend adding a commented `GATEWAY_FORWARDED_*` block to `.env.example` and a `forwardedHeaders` stanza to the chart values. |
| **RL-024** | Once any schedule window exists, `GatewayConfigState.Current` reads the clock on every request. | `if (projection.NextTransition == DateTimeOffset.MaxValue) return projection.Snapshot;` — the no-schedule path is one comparison, but with schedules it is a `TimeProvider.GetUtcNow()` per `Current` access, and `Current` is read several times per request across the resolvers. Measurable only at very high RPS; noted for the performance budget in `perf/`. |
| **RL-025** | When no model-scoped rule is configured anywhere, `HasModelScopedRules()` is false, `modelId` stays null, and admitted requests are recorded in the usage report with **no model attribution**. | Correct for the hot path (it is what makes per-model limits free when unused), but it means the usage report's per-model rows are empty on the default configuration. Worth stating in the runbook so an operator does not read "no model rows" as "no traffic". |

---

## 3. Improvement Opportunities

Ranked by effectiveness × efficiency. "Effort" is engineering time including tests.

### Quick wins — high effectiveness, high efficiency

| Improvement | Category | Effectiveness | Efficiency | Effort | Expected benefit | Implementation notes |
|---|---|---|---|---|---|---|
| **QW-1 · Make `Rpm == 0` mean no rate control** | Correctness / availability | **High** | **High** | **Small** | Removes the single worst failure mode: a documented no-op value throttling a model to 1 rpm gateway-wide. | `EnforcesRate => Rpm > 0`; skip zero-rpm rules in `TryAcquireAll`/`TryAcquireRequest`. Fixes RL-001 and defuses RL-019. Add the per-scope validation matrix in the same PR. |
| **QW-2 · Canonicalise `scope` on ingest** | Security / correctness | **High** | **High** | **Small** | Eliminates the whole class of "accepted, displayed, inert" rules, including silently-disabled `auth_failure` and `anonymous` tiers. | Lower-case in `AdminRateLimitRuleDto.ToDefinition()`; make `IsSingleton`/`IsPair` `OrdinalIgnoreCase`; one-off canonicalisation of existing rows. Fixes RL-002. |
| **QW-3 · Return early from the window preview on validation failure** | Security / availability | **High** | **High** | **Small** | Closes a one-request process-level DoS. | Two lines in `RateLimitWindowPreviewBuilder.Build`, plus a `MaxWindowsPerRule` guard in the endpoint. Fixes RL-004. |
| **QW-4 · Take-and-refund instead of peek-and-debit in the auth-failure limiter** | Security | **High** | **High** | **Small** | Restores the configured guessing rate as an actual ceiling instead of a per-round one. | `TryAcquireRequest` on the way in, `RefundRequest` in a `finally` when `!WasCredentialRejected`. Both operations already exist and refunds are capacity-capped. Fixes RL-005; apply the same shape to the anonymous guard. |
| **QW-5 · Extend the `rpm 0 ⇒ burst 0` refusal to every composing scope** | Correctness | Medium | **High** | **Small** | Removes two silent-discard configurations (RL-013) and reinforces QW-1. | One predicate change in `TryValidateRules` + `TryValidateWindowTier`. |
| **QW-6 · Interlocked counter for the plan cache** | Performance | Medium | **High** | **Small** | Removes an all-locks operation from the admission path under IP-diverse load. | Copy the pattern from `InMemoryDistributedRateLimitStore._requestPartitionCount`. Fixes RL-011. |
| **QW-7 · Auth-failure startup warning** | Security / operability | Medium | **High** | **Small** | An upgraded deployment learns that credential guessing is on the default tier. | Symmetric to the existing `anonymous` warning in `GatewayAdmissionLimitsStartupLogger`. Fixes RL-015. |
| **QW-8 · Forced-eviction metric + rate-limit alert rules** | Observability | **High** | Medium | **Small** | Makes "enforcement has silently degraded" and "credential stuffing in progress" machine-detectable. | New counter in `Compact`; four rules in `deploy/prometheus/alerts/33pol.yml`; runbook entries. Fixes RL-012. |

### Near-term

| Improvement | Category | Effectiveness | Efficiency | Effort | Expected benefit | Implementation notes |
|---|---|---|---|---|---|---|
| **IM-1 · Carry window suspension as a flag, not as `(0,0,0)`** | Correctness | **High** | Medium | **Small–Medium** | "Pause this rule" means what it says on every scope, including tenant stream caps. | Prefer omitting the entry from the projected map over adding a flag — smaller surface. Fixes RL-003; also removes the ambiguity behind RL-014's suspend variant. |
| **IM-2 · ETag / `If-Match` on the rate-limit admin API** | Data integrity | **High** | Medium | **Medium** | Concurrent operator edits conflict loudly instead of clobbering. | Version already exists (`ConfigVersionEntity`). Add a `RowVersion` token so the DB enforces it too; surface 409 in the console. Fixes RL-006. |
| **IM-3 · Structured diff in the rate-limit audit entry** | Security / compliance | **High** | Medium | **Small–Medium** | "What changed, by whom, when" becomes answerable. | `RateLimitConfigAdminService` already holds both the stored and submitted rule sets. Audit failures too. Fixes RL-009. |
| **IM-4 · Key the control-plane bucket on API key id** | Reliability | Medium | **High** | **Small** | One runaway console session can no longer lock every operator out. | `RateLimitKeys.ControlPlane(apiKeyId ?? partitionKey)`. Fixes RL-010. |
| **IM-5 · Cap and report the schedule report's occurrence list** | Reliability | Medium | **High** | **Small** | A large schedule cannot produce a multi-hundred-MB response. | Mirror the existing `transitions` truncation. Fixes RL-007. |
| **IM-6 · Cross-kind overlap detection at equal priority** | Correctness | Medium | Medium | **Small** | Scheduled tiers stop being name-order-dependent in an edge case. | Reorder the rank/kind tests and project a `once` span onto minutes-of-week. Fixes RL-008. |
| **IM-7 · Standard `RateLimit-*` headers on refusals** | UX / interop | Medium | **High** | **Small** | Generic clients and SDK middleware can pace against the gateway. | Refusal path only, where no upstream header can collide. Fixes RL-017. |
| **IM-8 · `ForwardedHeaders` in the deployment assets** | Security / operability | Medium | **High** | **Small** | Removes the most likely real-world cause of "every anonymous caller shares one bucket". | Commented block in `.env.example`, values stanza in the Helm chart, a paragraph in `deploy/docker/README.md`. Fixes RL-023. |
| **IM-9 · Replica-count guard** | Reliability | Medium | **High** | **Small** | A deployment cannot silently multiply every limit by N. | Log an error (or refuse to start) when an explicit `Gateway:InstanceCount`/replica signal exceeds 1 while the in-memory store is active; document it in the chart. Addresses RL-022. |

### Longer-term / strategic

| Improvement | Category | Effectiveness | Efficiency | Effort | Expected benefit | Implementation notes |
|---|---|---|---|---|---|---|
| **IM-10 · A Redis-backed `IDistributedRateLimitStore`** | Scalability | **High** | Low | **Large** | Horizontal scaling without multiplying every limit; limits survive restarts. | The interface is already shaped for it (one round trip per operation, no caller-side read-modify-write). The hard parts are `TryAcquireAll` atomicity across keys (a Lua script) and the stream-slot lease lifecycle. Needs a fallback-to-local policy when Redis is unreachable — fail *open* or *closed* is a deliberate operator choice. |
| **IM-11 · Per-key rate-limit partitioning as a first-class option** | Scalability / fairness | **High** | Medium | **Medium** | Ends the collapse of all console-issued keys into one tenant bucket, where one noisy key throttles every other. | Partially available today via `api_key`-scope rules, but those must be written per key id. A `RateLimiting:PartitionByApiKey` switch that puts the key id in `RateLimitSubject.PartitionKey` would make it structural. Interacts with the usage report's key dimension, which is already bounded. |
| **IM-12 · A dry-run / simulate mode for rate-limit configuration** | UX / safety | Medium | Medium | **Medium** | An operator can see "with this configuration, last hour's traffic would have produced N refusals for tenant X" before saving. | The usage tracker already holds 180 minutes of per-minute counters per tenant/model/key; replaying them against a candidate rule set is a pure function. Would have caught RL-001 and RL-003 at configuration time. |
| **IM-13 · Property-based tests for the schedule evaluator and overlap validator** | Maintainability | Medium | Medium | **Medium** | Replaces case-by-case DST and overlap tests with invariants ("no two windows the validator accepts are ever simultaneously active with equal rank"). | `FsCheck`/`CsCheck` over generated window sets. The evaluator is pure, so this is cheap to wire. |
| **IM-14 · Consolidate the four "is this request metered?" predicates** | Maintainability | Medium | Medium | **Small–Medium** | `InferenceRouteClassifier.IsRoutableInference`, `IsControlPlane`, and the two middlewares' private `IsGuardedPath`/`IsCredentialGuardedPath` (each with its own `/admin/api` constant) encode overlapping path policy in four places. One divergence produces an unmetered surface. | Single classifier returning a flags enum; the constants collapse to one. |

---

## 4. Test and Validation Gaps

Existing coverage is strong (~250 tests across `Core`, `Policy`, `Proxy`, `Integration`, `Persistence`, `Observability`). The gaps below are what the current suite cannot catch.

### Unit

| Gap | Scenario | Expected behaviour | Approach |
|---|---|---|---|
| Per-scope `rpm: 0` semantics | A rule with `rpm: 0, burst: N > 0` in each of the eight scopes | Rejected at validation; if somehow stored, enforces **no** rate control — never 1 rpm | Theory over `RateLimitScopeNames.All` in `RateLimitRuleValidationTests`, plus a store test asserting `TryAcquireRequest` on a `(0, 500)` policy admits indefinitely. **Would have caught RL-001.** |
| Scope-name casing | Each scope name in lower / Title / UPPER casing, with a valid and an invalid target | Same effective configuration in all three; invalid targets rejected identically | Theory in `RateLimitRuleValidationTests` + a round-trip through `GatewayConfigStore`. **Would have caught RL-002.** |
| Suspension across scopes | A suspending window on each scope, with a plan-backed and a default-backed subject | Effective tier is exactly what applies when the rule does not exist — including `maxConcurrentStreams` | Extend `RateLimitScheduleProjectionTests`. **Would have caught RL-003.** |
| Composing-scope burst | `anonymous` and `tenant` rules with `rpm: 0, burst > 0` | Rejected with the same message | `RateLimitRuleValidationTests`. **RL-013.** |
| Rate-only scopes | `auth_failure` with only `maxConcurrentStreams` set | Rejected as "this scope is rate-only" | `RateLimitRuleValidationTests`. **RL-014.** |
| Cross-kind overlap | `once` and `weekly` windows with equal explicit `priority`, temporally intersecting | Rejected as an ambiguous overlap | `RateLimitScheduleValidationTests`. **RL-008.** |
| Preview guard | Preview request with `MaxWindowsPerRule + 1` windows | `400` before any overlap computation | `AdminRateLimitEndpointTests` + a builder unit test asserting `FindWindowOverlaps` is not reached. **RL-004.** |

### Integration

| Gap | Scenario | Expected behaviour | Approach |
|---|---|---|---|
| Refund across a config change | Stage one charges, an admin write lands, stage two refuses | The refund lands on the buckets stage one charged | Drive `RateLimitMiddleware` with a `TimeProvider` and a config provider that flips version between stages. **RL-016.** |
| Control-plane lockout | Simulate M console sessions at the measured per-session rate | Operator endpoints stay reachable; or the refusal is attributable to one session only | Integration test over `/admin/api/rate-limits` with distinct key ids. **RL-010.** |
| Schedule report size | 500 rules × 8 weekly windows × 62-day range | Response is bounded and reports truncation | `AdminScheduledRateLimitEndpointTests`. **RL-007.** |
| Upgraded-database seed | A database with `RulesSeededAt` stamped but no `anonymous` / `auth_failure` rows | Startup emits both warnings | Extend `GatewayDbBootstrapRateLimitSeedTests` + a startup-logger test. **RL-015.** |

### Concurrency and distributed

| Gap | Scenario | Expected behaviour | Approach |
|---|---|---|---|
| **Peek/debit atomicity** | N concurrent credential-rejected requests against a bucket holding exactly 1 token | Exactly 1 is admitted; N−1 are refused | Parallel middleware test with a controlled `TimeProvider`. The store's own `TryAcquireRequest_Concurrent_AdmitsExactlyTheLimit` proves the store is atomic; nothing proves the *middleware* preserves that. **Highest-value missing test — would have caught RL-005.** |
| Concurrent admin writes | Two `PUT`s with disjoint rule sets, issued simultaneously | One succeeds, one conflicts — never a silent merge or clobber | Integration test with two scopes against one SQLite file. **RL-006.** |
| Multi-instance behaviour | Two gateway processes over one database, same tenant | Documented and asserted: limits are per-process (until IM-10) | An explicitly-named test that *pins the current limitation*, so a future change is deliberate. **RL-022.** |
| Plan-cache churn under contention | Sustained miss rate at the 20 000-entry ceiling from many threads | No lock convoy; admission latency stays flat | Benchmark (`BenchmarkDotNet`) rather than a unit test. **RL-011.** |

### Load and stress

| Gap | Scenario | Expected behaviour | Approach |
|---|---|---|---|
| Enforcement accuracy | Fixed configured rpm, sustained load well above it, 5-minute window | Admitted rate within ±5 % of configured rpm | New k6 scenario asserting on admitted count, not just status codes. **RL-020.** |
| Partition isolation | Tenant A over budget, tenant B at 10 % of budget, concurrently | B sees zero 429s | k6 with two API keys and per-key thresholds. |
| Partition-table exhaustion | Ramp distinct source addresses toward `InMemoryMaxPartitions` | `gateway_rate_limit_partitions` approaches the ceiling; the forced-eviction signal fires; enforcement for established tenants is unaffected | k6 + trusted `X-Forwarded-For`; assert on the new metric from QW-8. |
| Limiter overhead | Same load with rate limiting enabled and disabled | Added p99 within the documented budget | Extend `perf/k6/scripts/overhead-compare.js` with a limiter-on/off pair. |

### Security and bypass

| Gap | Scenario | Expected behaviour |
|---|---|---|
| Credential-guessing ceiling | 100 concurrent connections, random keys, one address, 60 s | Validated-credential count ≤ `AuthFailure.Rpm × AuthFailureProbeMultiplier` |
| Spoofed `X-Forwarded-For` | `ForwardedHeaders:Enabled=true` with a **non**-trusted peer | Header ignored; all requests share one partition |
| Path-shape bypass | `/v1/chat/completions/`, `//v1/chat/completions`, `/V1/Chat/Completions`, `/x/v1/chat/completions` | Metered or refused consistently with the exact path; no unmetered routable surface |
| Ungranted model, shared bucket | Key without a grant floods a `model`-scoped rule | The shared `model` bucket is untouched; the caller's own scopes are charged (partially covered by `RateLimitMiddlewareGrantScopeTests` — extend to assert the *bucket* directly) |
| Alias evasion | Request by alias for a model with a canonical-id rule | The canonical rule applies (covered — keep) |

### Admin-panel validation and authorization

| Gap | Scenario | Expected behaviour |
|---|---|---|
| Role matrix | `Inference`-only key, `Admin` key in a **non**-operator tenant, `Operator` key — against `GET`/`PUT`/`/usage`/`/schedule`/`/windows/preview` | Only the operator-tenant admin key succeeds on all five; each other combination is `403` |
| Audit completeness | A successful, a validation-refused and a `503` write | An audit entry for each, with the actor and a diff |
| Payload abuse | `rules: null` vs `[]`; `schedule: null` vs `[]`; 2 001 rules; a 17th window; a 300-char target | Each produces the documented outcome, not a 500 |
| Console dirty-state | Edit, navigate away, return; edit, reload; edit, save a conflicting change from another session | Draft preserved / discard confirmed / conflict surfaced |

### Configuration migration and rollback

| Gap | Scenario | Expected behaviour |
|---|---|---|
| Seed idempotence across versions | Seed with build N, upgrade to N+1 that adds a scope, restart | New singleton scopes are seeded or warned about; existing rules untouched |
| Schedule JSON forward-compat | A `ScheduleJson` written by a newer build | Base tier applies, warning logged, rule still enforced (covered by `GatewayConfigStore` — add an explicit test) |
| Rollback | Save configuration V2, roll the binary back to a build that predates a scope | Unknown-scope rows are ignored, not reinterpreted (the store comments claim this — assert it) |
| Disable / re-enable cycle | `enabled: false`, save, `enabled: true`, save | Every tier and rule survives byte-identically |

---

## 5. Recommended Remediation Roadmap

### Phase 1 — Immediate (this week): correctness and security defects with small, well-understood fixes

| # | Action | Findings | Dependencies | Validation criteria |
|---|---|---|---|---|
| 1.1 | `EnforcesRate => Rpm > 0`; zero-rpm rules contribute no rate control; extend the `rpm 0 ⇒ burst 0` refusal to every scope in both `TryValidateRules` and `TryValidateWindowTier`; add a startup pass that logs and ignores stored rules already in that shape | RL-001, RL-013, RL-019 | none | Per-scope validation theory green; a `(0, 500)` policy admits indefinitely in a store test; a `model` rule with `rpm: 0` no longer throttles after its burst; runbook updated |
| 1.2 | Canonicalise `rule.Scope` on ingest; make `IsSingleton`/`IsPair` case-insensitive; one-off canonicalisation of existing `rate_limit_rules.Scope` | RL-002 | 1.1 (same validator file) | `{"scope":"Anonymous","target":"acme"}` is rejected at save; all eight scopes round-trip identically in three casings |
| 1.3 | Return early from `RateLimitWindowPreviewBuilder.Build` on validation failure; cap `request.Windows` in the endpoint | RL-004 | none | A 50 000-window preview returns `400` in < 50 ms; `FindWindowOverlaps` is provably not reached |
| 1.4 | Take-and-refund in `AuthFailureRateLimitMiddleware` and `AnonymousAdmissionGuardMiddleware` | RL-005 | none | N concurrent credential-rejected requests against a 1-token bucket admit exactly 1; the existing 18 auth-failure tests stay green |
| 1.5 | Carry suspension by omitting the projected entry rather than encoding `(0,0,0)`; refuse `auth_failure`/`anonymous` rules whose only non-zero field is a stream cap | RL-003, RL-014 | 1.1 | A suspended tenant window leaves the plan's `maxConcurrentStreams` intact; a suspended `auth_failure` rule is documented and asserted |
| 1.6 | Add the `auth_failure` startup warning; add the forced-eviction counter and four Prometheus alert rules with runbook entries | RL-012, RL-015 | none | Warning fires on a database with no `auth_failure` row; the counter increments under a synthetic partition flood; alerts render in the Grafana dashboard |

**Phase 1 exit criterion:** every finding above has a regression test, and `docs/runbooks/rate-limit-admin.md` matches the implemented semantics line for line — in particular the `rpm: 0` and `suspend` paragraphs.

### Phase 2 — Near-term (next 2–4 weeks): admin-panel integrity, observability and performance

| # | Action | Findings | Dependencies | Validation criteria |
|---|---|---|---|---|
| 2.1 | ETag on `GET`, `If-Match` on `PUT`, `RowVersion` on `ConfigVersionEntity`, 409 handling in the console | RL-006 | 1.1–1.5 landed (so the conflict path is tested against correct semantics) | Two concurrent `PUT`s: one `200`, one `409`; the console offers reload-and-reapply |
| 2.2 | Structured before/after diff in the `rate_limits.update` audit entry; audit failed attempts | RL-009 | 2.1 (the diff and the version check read the same stored snapshot) | An audit entry names every changed rule identity with old and new tiers; a 400 produces an entry |
| 2.3 | Key the control-plane bucket on API key id; alert on `rate_limit:control_plane` rejections | RL-010 | 1.6 (alert plumbing) | M simulated sessions do not refuse each other; the alert fires when one does |
| 2.4 | Interlocked counter for the plan cache; consider ignore-past-ceiling instead of clear-all | RL-011 | none | Benchmark: flat admission latency at a sustained miss rate with 20 000 entries |
| 2.5 | Cap and report the schedule report's occurrence list; cross-kind overlap detection | RL-007, RL-008 | none | 500 × 8 × 62 d returns a bounded, `truncated: true` payload; equal-priority cross-kind windows are rejected |
| 2.6 | Standard `RateLimit-*` headers on the refusal path; `ForwardedHeaders` in `.env.example`, the Helm chart and `deploy/docker/README.md`; replica-count guard | RL-017, RL-022, RL-023 | none | A 429 carries both header families; a fresh compose deployment documents the proxy decision; `replicas: 2` logs an explicit error |
| 2.7 | Build out the load suite: enforcement accuracy, partition isolation, partition exhaustion, limiter overhead | RL-020 | 1.1 (so accuracy is measured against fixed semantics) | Accuracy within ±5 %; isolation shows zero cross-tenant 429s; overhead within the documented budget |

**Phase 2 exit criterion:** the admin panel cannot silently lose a change, every rate-limit failure mode has a metric and an alert, and the load suite can distinguish a working limiter from a broken one.

### Phase 3 — Long-term (next quarter): architecture and scalability

| # | Action | Findings / improvements | Dependencies | Validation criteria |
|---|---|---|---|---|
| 3.1 | Redis-backed `IDistributedRateLimitStore` behind the existing interface, with an explicit fail-open/fail-closed policy on unavailability | IM-10, RL-022 | 2.7 (the load suite is the acceptance harness); 1.1 (stable semantics to port) | Two replicas enforce one shared limit within ±5 % of configured rpm; a Redis outage degrades per the configured policy and is alerted; per-decision latency budget met |
| 3.2 | First-class per-API-key partitioning (`RateLimiting:PartitionByApiKey`) | IM-11 | 3.1 (partition count grows; the shared store makes that affordable) | One noisy key is bounded without bounding its siblings; usage-report key dimension stays within `UsageReportMaxKeys` |
| 3.3 | Dry-run / simulate mode: replay the usage tracker's 180-minute counters against a candidate rule set before saving | IM-12 | 2.1 (the candidate configuration already round-trips through the API) | The console shows "this change would have refused N requests for tenant X last hour" before Save |
| 3.4 | Property-based tests for the schedule evaluator and overlap validator | IM-13 | 1.5, 2.5 | The invariant "no accepted window set has two simultaneously-active windows of equal rank" holds over generated inputs, including DST boundaries in at least two zones |
| 3.5 | Consolidate the four request-classification predicates into one flags-returning classifier | IM-14 | none | One definition of `/admin/api`, one of the routable inference set; an architecture test forbids re-introducing a second |

**Phase 3 exit criterion:** the gateway can be scaled horizontally without multiplying its limits, operators can see the effect of a configuration change before applying it, and the classification and schedule logic are protected by invariants rather than examples.

---

## Appendix — What was verified and found sound

Recorded so a future reviewer does not re-derive it.

- **Multi-scope acquire/refund.** `TryAcquireAll` takes in a fixed scope order, refunds `rules[..i]` on the first refusal, and returns the tightest admitting rule by remaining-fraction. Refunds are capped at capacity, so an unmatched or racing refund cannot inflate a bucket past its tier.
- **Bucket arithmetic.** The refill anchor moves forward only, so a backwards clock step or an out-of-order concurrent read pauses refilling rather than granting a windfall. `ForceTake` floors at zero rather than going negative.
- **Eviction ordering.** `Compact` evicts at-capacity partitions first (nothing to win), orders by last-seen so an actively-rejected partition cannot be evicted from under itself, and value-matches on removal so a freshly-created replacement is not dropped. Stream-slot states are tombstoned before removal, and `AcquireSlot` retries on a tombstone — the spurious-429 race is genuinely closed.
- **Partition keying.** IPv6 collapsed to /64; IPv4-mapped-IPv6 normalised so the same client lands in one bucket regardless of listener binding; link-local scope ids dropped. Scope-prefixed bucket keys (`t:`, `k:`, `m:`, `m!:`, `tm:`, `km:`, `cp:`, `ap:`) make cross-scope collisions impossible.
- **Cross-tenant DoS on shared model buckets.** `IsModelChargeableAsync` mirrors the router's grant conditions exactly, so an ungranted caller cannot drain a model's gateway-wide budget. Anonymous callers use a separate `m!:` bucket under the same rule.
- **Two-stage evaluation.** Identity scopes gate the body parse; the parse is skipped entirely when no model-scoped rule exists anywhere; the parse result is cached and shared with the router.
- **Governor bounds.** The adaptive factor is clamped to `(0, 1]` in the governor *and* again where it is applied, so no adaptive path can exceed a configured tier. Backoff escalation is restricted to caller-scoped refusals, capped by exponent and by `MaxRetryAfterSeconds`, and jittered downward only.
- **Metric cardinality.** The only label on the rejection counter is a bounded `reason` string; the adaptive gauge is labelled by canonical model id only. No attacker-controlled value reaches a metric tag. Per-tenant and per-key numbers live in the explicitly-bounded usage report.
- **Bounded state.** Partitions 50 000, plan cache 20 000, backoff table 20 000, usage keys 500 per dimension, pressure dimensions 200, rules 2 000, windows 16 per rule. Every insert path enforces its cap.
- **Client compatibility.** `429` with an OpenAI-shaped `rate_limit_error` body and a golden test; `Retry-After` present on every refusal path; continuous refill keeps it at ~1 s.
- **Master-switch separation.** `AuthFailureProtectionEnabled` lives in appsettings and is deliberately *not* governed by the admin master switch, so a traffic-shaping decision taken during an incident cannot switch off a security control.
- **Pipeline ordering.** `UseGatewayForwardedHeaders` is first; the anonymous guard precedes `PublicModelDetection` (the first body parse); the auth-failure limiter wraps the security middleware; the limiter proper and the quota middleware run before the terminal endpoint middleware, so admin endpoints mapped earlier in the file are still metered. Verified against the framework's endpoint-middleware placement.
- **Forwarded-headers posture.** Off by default, never inferred, two distinct startup warnings (no trust anchors; disabled outside Development), `ForwardLimit` validated ≥ 1, `TrustAllProxies` warns loudly.
- **Schedule projection.** Lazy, lock-free on the read path when nothing is scheduled, double-checked under a lock when re-projecting, and stamped with a monotonic `EffectiveVersion` that participates in the plan cache key — so a window boundary invalidates cached plans exactly as an admin write does.
