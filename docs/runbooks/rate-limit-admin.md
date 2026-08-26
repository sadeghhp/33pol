# Rate limit admin

Operators can view and edit the **default** tier, the **plan** tiers, and the **scoped rules** — per model, per API key, per tenant, and the combinations — from the admin UI or API. All of it applies without a restart.

## Scopes and how they combine

There are six scopes a request can be subject to:

| Scope | Counts against | Configured as |
|-------|----------------|---------------|
| `global` | Every inference request through the gateway | rule, target `*` |
| `tenant` | One tenant, or one client address block for anonymous traffic | default tier / plan tier / `tenant` rule |
| `api_key` | One credential | rule, target = key id |
| `model` | One model, summed over every caller | rule, target = canonical model id |
| `tenant_model` | One tenant's use of one model | rule, target = `tenant\|modelId` |
| `api_key_model` | One key's use of one model | rule, target = `keyId\|modelId` |

**Naming a tenant.** The `tenant` and `tenant_model` scopes accept the tenant **id** (the GUID the admin API returns) or its **slug** — whichever you write, the rule matches, and the bucket is keyed on the id either way. The id wins if both are configured. Key ids have no second spelling: use the `id` from `POST /admin/api/keys`.

Targets are **not** checked against anything that exists. A rule for a model, tenant or key that is not there is stored and simply never matches — which is what you want while provisioning, and a trap when it is a typo. `GET /admin/api/rate-limits/usage` is the check: a rule that is doing something appears under `violations` or moves a row's `effectiveRpm`.

**The scopes compose; they do not override one another.** A request is admitted only when *every* scope that applies to it admits it, so adding a narrower rule can only ever tighten what a caller may do. This is what makes the outcome deterministic regardless of the order rules were configured in — there is no "most specific wins" tie-break to reason about, because nothing ties.

Precedence exists in exactly one place: **inside the tenant scope**, where three sources can each name a tier for the same caller. A per-tenant override wins, then the tenant's plan, then the default. Every other scope has one source.

Admission is **all-or-nothing**. Tokens are taken from each applicable bucket in scope order, and if a later scope refuses, the tokens already taken are handed back. A caller blocked by a narrow per-model limit therefore does not also burn its tenant-wide budget on every retry.

Evaluation runs in **two stages**. The scopes that need no request body — global, tenant, key — are evaluated first and gate the parse; the model-dependent ones follow. A caller already over its budget is refused without the gateway paying to read what it sent. When no model-scoped rule is configured anywhere (the default), the second stage is skipped entirely and the body is never read here at all.

**Model scopes are charged only to callers granted the model.** The `model` bucket is shared by every caller of that model, so charging it for a request the router is going to answer 403 would let one key deny a model to every tenant that *is* granted it, at nothing more than its own request rate. A request for a model its key was never granted is still charged its identity scopes — the attempts are not free — but touches no model-scoped bucket. Public models and deployments with authentication switched off have no grants to consult, so their model scopes apply to everyone.

## What a tier controls

| Field | Meaning |
|-------|---------|
| `rpm` | Sustained requests per minute. The token bucket refills at `rpm / 60` per second. |
| `burst` | Extra tokens above `rpm`, so bucket capacity is `rpm + burst`. This is what an idle partition may spend at once. |
| `maxConcurrentStreams` | Streaming responses open at once. **`0` means unlimited, not "streaming denied".** |

A tier is a **per-partition** budget. In the tenant scope a partition is a *tenant* — every API key a tenant holds draws on the same bucket unless you add an `api_key` rule to bound one of them separately. Unauthenticated callers (only possible while some model is `publicAccess`) partition by client **address block** instead: the full address for IPv4, the `/64` prefix for IPv6. IPv6 is collapsed because a single subscriber is routinely handed a `/64` or shorter, and keying on the full 128-bit address would let one client mint 2^64 buckets — the limit would never bind, and the churn would walk the partition table into its ceiling. See [`ForwardedHeaders`](../integrations.md) for making that address the caller's rather than your ingress's.

Requests refused by authentication are counted separately, per client address block, against the **`auth_failure`** tier (falling back to the default tier when none is configured), on inference and `/admin/api` paths. Only the ones answered `401` or `403` are charged, so traffic that authenticates never spends it; that budget is independent of a tenant's own limit in both directions.

Once an address has spent it, the next request from that address is refused `429` **before** authentication runs — a good key included. That is the point (it is what stops the guessing), but it means the address has to be the caller's rather than your ingress's: with `ForwardedHeaders` unconfigured behind a proxy, every caller shares one address and therefore one budget. Configure the trusted proxy, or raise the default tier. Reaching the shipped default takes 3 500 rejected credentials from one address inside a minute.

Every answer on an inference path carries the partition's budget:

```
X-33pol-RateLimit-Limit: 3500        # rpm + burst
X-33pol-RateLimit-Remaining: 3499    # whole requests left
X-33pol-RateLimit-Reset: 1           # seconds until the bucket is full again
X-33pol-RateLimit-Scope: model       # which scope the numbers above describe
X-33pol-RateLimit-Adaptive: 420/600  # only while load-aware enforcement is holding it down
```

They are vendor-prefixed on purpose — the upstream provider's own `X-RateLimit-*` headers are copied onto the response after the gateway's, and would otherwise silently replace these.

`Scope` matters once more than one limit can apply: a bare `Remaining: 4` is ambiguous, because a client cannot tell whether it is its own key, its whole organisation, or the model it chose that is nearly exhausted — and those call for three different responses. On a rejection the header names the scope that refused; on a success, the one closest to refusing. `Adaptive` is present **only** while the load-aware governor is holding that scope below its configured rate, so its absence is the answer to "am I being enforced as configured?".

## API

| Method | Path | Auth |
|--------|------|------|
| `GET` | `/admin/api/rate-limits` | Admin |
| `PUT` | `/admin/api/rate-limits` | Admin |
| `GET` | `/admin/api/rate-limits/usage?minutes=60&take=25` | Admin |

Body shape (camelCase JSON):

```json
{
  "enabled": true,
  "adaptiveEnabled": false,
  "default": { "rpm": 60, "burst": 10, "maxConcurrentStreams": 5 },
  "plans": {
    "standard": { "rpm": 120, "burst": 20, "maxConcurrentStreams": 10 }
  },
  "rules": [
    { "scope": "model",         "target": "gpt-4",              "rpm": 600, "burst": 60, "maxConcurrentStreams": 40 },
    { "scope": "tenant_model",  "target": "acme|gpt-4",         "rpm": 60,  "burst": 10, "maxConcurrentStreams": 4 },
    { "scope": "api_key",       "target": "6f1c…",              "rpm": 30,  "burst": 0,  "maxConcurrentStreams": 2 },
    { "scope": "model",         "target": "llama-70b",          "rpm": 0,   "burst": 0,  "maxConcurrentStreams": 8 },
    { "scope": "auth_failure",  "target": "*",                  "rpm": 60,  "burst": 20, "maxConcurrentStreams": 0 }
  ]
}
```

The fourth rule caps concurrency only: a scoped rule may leave `rpm` at `0`, which means "this rule does not limit the request rate". (The *default* tier is still floored at 1 — a zero there would silently disable the gateway's only universal limit.)

**`rules` is optional, and omitting it is not the same as sending `[]`.** Omitted means "I do not manage rules", and the stored set is carried through untouched — so a client written against the older contract cannot delete rules it cannot see. An empty array is a deliberate "there are no rules" and does delete them.

`enabled` is the gateway-wide master switch. With it off, neither request-rate nor stream-concurrency limits are enforced for any tier, and the per-address auth-failure budget is not enforced either. The numbers stay saved and apply again when it is re-enabled. Quotas and budgets are unaffected by it.

Validation (HTTP 400, `{ "message": "…" }`):

- `rpm`: 1 … 1_000_000 — there is no "unlimited" value; use `enabled: false`
- `burst`: 0 … 1_000_000
- `maxConcurrentStreams`: 0 … 10_000, where `0` is unlimited
- Plan slugs: non-empty, start with a letter, alphanumeric/`_`/`-`, max 64 chars
- Rule `scope`: one of `global`, `tenant`, `api_key`, `model`, `tenant_model`, `api_key_model`, `auth_failure`
- Rule `target`: non-empty, no surrounding whitespace, max 256 chars; exactly one `|` for the pair scopes and none for the others; `*` for `global` and `auth_failure`
- A rule must enforce something — `rpm` and `maxConcurrentStreams` both zero is rejected rather than accepted as a limit that never fires
- No two rules may share a (scope, target); duplicates are rejected rather than last-one-wins, so the applied configuration never depends on serialisation order
- At most 2 000 rules

Tiers are validated even when `enabled` is false, so re-enabling can never restore numbers that were never checked.

## Persistence and apply

1. `PUT` writes the `rate_limit_defaults` (single row), `rate_limit_plans` and `rate_limit_rules` tables in the gateway database, replacing the plan and rule sets wholesale, and bumps the config version in the same transaction.
2. The admin service then forces an in-process config-snapshot refresh, so the new tiers are live on the next request — no restart.
3. `RateLimiting` in `appsettings.json` is **seed-only**: it populates a fresh database and is ignored thereafter. Editing it on a seeded deployment changes nothing.

What gets seeded, and when:

| Configuration | Seeded into | Seeded when |
|---------------|-------------|-------------|
| `Default`, `Plans` | `rate_limit_defaults`, `rate_limit_plans` | the defaults row does not exist |
| `Adaptive:Enabled` | `rate_limit_defaults.AdaptiveEnabled` | same |
| `Global`, `Tenants`, `ApiKeys`, `Models`, `TenantModels`, `ApiKeyModels`, `AuthFailure` | `rate_limit_rules` | once per database, stamped by `rate_limit_defaults.RulesSeededAt` |

The rule seed is a **one-shot**, not a top-up. Deleting every rule through the admin API is a configuration decision, and a restart must not quietly restore the appsettings set — so the stamp, not an empty table, is what decides. A database created before the rules table existed carries a null stamp and is backfilled from configuration exactly once on upgrade, without disturbing the tiers already in it.

A malformed entry (a `tenant_model` target missing its `|`, say) is logged as a warning and skipped rather than refusing to start; the admin API rejects the same rule outright, because there a caller is waiting and can fix it. The same applies to two configuration keys that collapse to one rule — `"gpt-4"` and `"gpt-4 "` are distinct JSON keys but one `(scope, target)` — and to configuration past the 2 000-rule ceiling, which is truncated in scope-then-target order with a warning naming how many were dropped. Nothing in this path can stop the gateway starting.

Without a configured database, rate limits are read-only and `PUT` answers **503**.

`POST /admin/api/config/reload` reloads **models.json** only; it does not touch rate limits.

## Admin UI

**Settings → Rate limits** — toggle enforcement, edit the default tier, plan rows and scoped rules, toggle load-aware enforcement, then **Save rate limits**. Validation errors appear inline under the card. The **Rate-limit usage** card below it shows the live report.

## Rollback

1. **UI/API:** Restore known-good numbers via **Settings → Rate limits** or `PUT /admin/api/rate-limits` with the previous payload. This is the only path that applies without a restart.
2. **Emergency:** `PUT` with `"enabled": false` stops all rate-limit enforcement immediately while you work out the right numbers. Quotas and budgets keep applying.
3. **Database:** the values live in `rate_limit_defaults` / `rate_limit_plans` / `rate_limit_rules`. Restoring a database backup requires a restart (or a subsequent `PUT`) for the snapshot to pick the rows up.

After rollback, confirm:

- `GET /admin/api/rate-limits` shows expected values.
- A test inference call respects the tier (e.g. temporarily set `default.rpm` to `1`, `burst` to `0`, and verify a second request returns 429 with `Retry-After`).

## In-memory store tuning

These live in `appsettings.json` under `RateLimiting` and are read at startup only.

| Key | Default | Purpose |
|-----|---------|---------|
| `InMemoryPartitionRetentionSeconds` | 3600 | How long an untouched partition is kept before it is swept. |
| `MaintenanceIntervalSeconds` | 10 | How often the background sweeper runs. Sweeping no longer happens on a request thread at all. |
| `InMemoryMaxPartitions` | 50000 | Ceiling on live partitions per dimension. The sweeper evicts least-recently-seen partitions down to the ceiling. |
| `UsageReportMaxKeys` | 500 | Distinct keys the usage report tracks per section. Past it, new keys are ignored rather than evicting existing ones. |

Raise `InMemoryMaxPartitions` if you legitimately serve more than 50 000 distinct partitions (tenants plus anonymous client addresses) within the retention window; evicting a partition resets its bucket. Partitions currently being rejected are touched on every rejection, so they sort last and are not the ones evicted.

## Load-aware enforcement

Off by default. Two switches turn it on, and both must agree: `RateLimiting:Adaptive:Enabled` in `appsettings.json` ("was this deployment built to adapt") and `adaptiveEnabled` in the admin API ("should it be adapting right now"). The second is the one you can reach at three in the morning without a restart.

It moves two levers, and neither can block a caller outright:

**Model factor.** While a model is saturated, its per-model rules are scaled down. Saturation is read from the model's own bulkhead — in-flight against its ceiling, and queue occupancy — and from its circuit breaker; an open breaker counts as fully saturated whatever the occupancy says. The factor moves by additive-increase / multiplicative-decrease with a hold band between the watermarks, so it converges rather than oscillating. It is clamped to `[MinFactor, 1.0]`: **adaptation can only ever enforce more strictly than you configured. There is no path by which it raises a limit.**

**Partition backoff.** A caller that keeps being refused is told to wait longer, geometrically, up to `MaxRetryAfterSeconds`, with jitter so a crowd refused together does not return together. It escalates only past `BackoffAfterConsecutiveRejections` and resets on that partition's first admitted request, so an ordinary burst never notices it.

| Key | Default | Purpose |
|-----|---------|---------|
| `Adaptive:Enabled` | `false` | Deployment-level switch. |
| `Adaptive:MinFactor` | 0.25 | Floor on the model factor. A saturated model still admits a quarter of its configured rate. |
| `Adaptive:HighWatermark` | 0.85 | Saturation at or above which the factor is cut. |
| `Adaptive:LowWatermark` | 0.5 | Saturation at or below which it recovers. Between the two it holds. |
| `Adaptive:DecreaseFactor` | 0.8 | Multiplicative cut per evaluation under pressure. |
| `Adaptive:IncreaseStep` | 0.05 | Additive recovery per evaluation. |
| `Adaptive:BackoffAfterConsecutiveRejections` | 5 | Refusals before `Retry-After` starts escalating. |
| `Adaptive:BackoffGrowthFactor` | 1.6 | Compounding per rejection past the threshold. |
| `Adaptive:MaxRetryAfterSeconds` | 60 | Hard ceiling on `Retry-After`. Clients sleep for this header. |
| `Adaptive:RetryAfterJitter` | 0.2 | Share of the wait spread randomly, downwards only. |

Nonsense gains (a high watermark under the low one, a decrease factor above 1) are clamped into a stable range at startup, so a typo degrades rather than becoming an incident.

Switching it off resets every factor immediately — the configured limits are back on the next request, not after a recovery ramp.

Both levers are driven by the request's **final** admission outcome, taken where the last gate is — so a streaming request refused by a concurrency cap escalates its partition's backoff, and only a request that clears every gate clears it.

The backoff table holds at most 20 000 partitions. Anonymous traffic partitions by client address block, so a broad refusal storm would otherwise grow it without bound between maintenance ticks. Past the ceiling a new partition is simply not tracked — it still gets the bucket's own `Retry-After`, just not a lengthened one — rather than an existing entry being evicted, which would make a flood of one-off sources a way to clear the penalty on a repeat offender. `gateway_rate_limit_backed_off_partitions` is the gauge to watch.

## Usage reporting

`GET /admin/api/rate-limits/usage?minutes=60&take=25` answers "who is using what, against which limits, and where are they hitting them":

- `totals` — decisions, admissions, refusals split into `rateRejected` and `concurrencyRejected`, and the rejection rate over the window
- `byTenantModel` — the per-user-per-model grid
- `byTenant`, `byModel`, `byApiKey` — the same numbers rolled up each way
- `violations` — limit hits, attributed to the identity the refusing scope actually counts (a `model`-scope hit is attributed to the model, not to the hundred tenants that met it)
- `adaptive` — the current factor, saturation and a sentence explaining each model's last move
- `store` — live partitions against the ceiling

Each row carries `requestsPerMinute` (observed load), `configuredRpm`, `effectiveRpm` and `utilization`. Both rpm columns are **sustained rates**, not bucket capacity — capacity is `rpm + burst`, and comparing an observed per-minute rate against it understates utilisation by the whole burst allowance. Utilization above 1 is normal for a row being refused: the numerator counts attempts, not admissions.

A refusal from a concurrency cap carries no rate at all: it was decided against a slot count. Those decisions are counted in `concurrencyRejected` and in `violations`, and leave the rpm columns alone.

The counters are **in-memory and bounded** — three hours of per-minute rings per tracked key, reset by a restart. That is deliberate: admission decisions arrive at request rate, and persisting one row per decision would put a write on the hot path and make the busiest partition the most expensive one to meter. Durable, long-horizon usage (tokens and cost per tenant and model, over months) already lives in the billing rollups; this answers the question those cannot.

## Metrics

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_rate_limit_rejections_total` | counter | `reason` = `rate_limit:<scope>` / `stream_concurrency:<scope>` / `auth_failure` |
| `gateway_rate_limit_adaptive_factor` | gauge | `model` |
| `gateway_rate_limit_partitions` | gauge | `dimension` = `request` / `stream` / `ceiling` |
| `gateway_rate_limit_backed_off_partitions` | gauge | — |

Only `model` appears as a label. Tenant is bounded but large, and the anonymous partition key is unbounded — a per-partition time series is how a metrics backend is taken down. Per-tenant and per-key numbers live in the usage report, where the key set is explicitly capped.

Worth alerting on:

- `gateway_rate_limit_partitions{dimension="request"} / gateway_rate_limit_partitions{dimension="ceiling"} > 0.8` — approaching the ceiling means evictions, and an evicted partition gets a fresh bucket.
- `gateway_rate_limit_adaptive_factor < 1` sustained for a model — that model is saturated, and callers are being held below their configured rate.
- A sharp rise in `gateway_rate_limit_rejections_total{reason=~"rate_limit:model.*"}` — a model limit is binding, which is either correct or too tight.

## Risks

- **Single-instance:** counters are in-process. The gateway is a single writer against one embedded SQLite file and scales vertically only, so there is no cross-replica fan-out to coordinate — but running two replicas would enforce each tier twice over.
- **In-memory store:** buckets already partly spent are not reset when tiers change; new limits apply from the next acquire. A restart resets every bucket to full.
- **Composed scopes are ANDed:** a rule you add to bound one caller applies to every request that matches it. A `model` rule with a small `rpm` throttles *all* tenants on that model, which is usually what you want but is not a per-tenant limit — use `tenant_model` for that.
- **Adaptive enforcement is bounded but not free:** it can hold a model at `MinFactor` of its configured rate for as long as the model stays saturated. The floor is what keeps that a degradation rather than an outage; check `gateway_rate_limit_adaptive_factor` before concluding a tier is misconfigured.
- **The usage report is in-memory:** it resets with the process and reaches back three hours. It is an operational view, not an audit trail — the billing rollups are the durable record.
