# Rate limit admin

Operators can view and edit the **default** and **plan** rate tiers from the admin UI or API. Per-tenant overrides are not editable: they come from `RateLimiting:Tenants` in `appsettings.json` and apply **only on a database-less deployment**, where appsettings is the whole of the configuration. Once a database is configured the snapshot is loaded from it and there is no table for per-tenant overrides, so the map is ignored.

The resolver picks a tier in precedence order: per-tenant override, then the tenant's plan, then the default.

## What a tier controls

| Field | Meaning |
|-------|---------|
| `rpm` | Sustained requests per minute. The token bucket refills at `rpm / 60` per second. |
| `burst` | Extra tokens above `rpm`, so bucket capacity is `rpm + burst`. This is what an idle partition may spend at once. |
| `maxConcurrentStreams` | Streaming responses open at once. **`0` means unlimited, not "streaming denied".** |

A tier is a **per-partition** budget, and a partition is a *tenant* — every API key a tenant holds draws on the same bucket. Unauthenticated callers (only possible while some model is `publicAccess`) partition by client address instead; see [`ForwardedHeaders`](../integrations.md) for making that address the caller's rather than your ingress's.

Requests refused by authentication are counted separately, per client address, against the **default** tier, on inference and `/admin/api` paths. Only the ones answered `401` or `403` are charged, so traffic that authenticates never spends it; that budget is independent of a tenant's own limit in both directions.

Once an address has spent it, the next request from that address is refused `429` **before** authentication runs — a good key included. That is the point (it is what stops the guessing), but it means the address has to be the caller's rather than your ingress's: with `ForwardedHeaders` unconfigured behind a proxy, every caller shares one address and therefore one budget. Configure the trusted proxy, or raise the default tier. Reaching the shipped default takes 3 500 rejected credentials from one address inside a minute.

Every answer on an inference path carries the partition's budget:

```
X-33pol-RateLimit-Limit: 3500       # rpm + burst
X-33pol-RateLimit-Remaining: 3499   # whole requests left
X-33pol-RateLimit-Reset: 1          # seconds until the bucket is full again
```

They are vendor-prefixed on purpose — the upstream provider's own `X-RateLimit-*` headers are copied onto the response after the gateway's, and would otherwise silently replace these.

## API

| Method | Path | Auth |
|--------|------|------|
| `GET` | `/admin/api/rate-limits` | Admin |
| `PUT` | `/admin/api/rate-limits` | Admin |

Body shape (camelCase JSON):

```json
{
  "enabled": true,
  "default": { "rpm": 60, "burst": 10, "maxConcurrentStreams": 5 },
  "plans": {
    "standard": { "rpm": 120, "burst": 20, "maxConcurrentStreams": 10 }
  }
}
```

`enabled` is the gateway-wide master switch. With it off, neither request-rate nor stream-concurrency limits are enforced for any tier, and the per-address auth-failure budget is not enforced either. The numbers stay saved and apply again when it is re-enabled. Quotas and budgets are unaffected by it.

Validation (HTTP 400, `{ "message": "…" }`):

- `rpm`: 1 … 1_000_000 — there is no "unlimited" value; use `enabled: false`
- `burst`: 0 … 1_000_000
- `maxConcurrentStreams`: 0 … 10_000, where `0` is unlimited
- Plan slugs: non-empty, start with a letter, alphanumeric/`_`/`-`, max 64 chars

Tiers are validated even when `enabled` is false, so re-enabling can never restore numbers that were never checked.

## Persistence and apply

1. `PUT` writes the `RateLimitDefaults` (single row) and `RateLimitPlans` tables in the gateway database, replacing the plan set wholesale, and bumps the config version in the same transaction.
2. The admin service then forces an in-process config-snapshot refresh, so the new tiers are live on the next request — no restart.
3. `RateLimiting` in `appsettings.json` is **seed-only**: it populates a fresh database and is ignored thereafter. Editing it on a seeded deployment changes nothing.

Without a configured database, rate limits are read-only and `PUT` answers **503**.

`POST /admin/api/config/reload` reloads **models.json** only; it does not touch rate limits.

## Admin UI

**Settings → Rate limits** — toggle enforcement, edit the default tier and plan rows, **Save rate limits**. Validation errors appear inline under the card.

## Rollback

1. **UI/API:** Restore known-good numbers via **Settings → Rate limits** or `PUT /admin/api/rate-limits` with the previous payload. This is the only path that applies without a restart.
2. **Emergency:** `PUT` with `"enabled": false` stops all rate-limit enforcement immediately while you work out the right numbers. Quotas and budgets keep applying.
3. **Database:** the values live in `RateLimitDefaults` / `RateLimitPlans`. Restoring a database backup requires a restart (or a subsequent `PUT`) for the snapshot to pick the rows up.

After rollback, confirm:

- `GET /admin/api/rate-limits` shows expected values.
- A test inference call respects the tier (e.g. temporarily set `default.rpm` to `1`, `burst` to `0`, and verify a second request returns 429 with `Retry-After`).

## In-memory store tuning

These live in `appsettings.json` under `RateLimiting` and are read at startup only.

| Key | Default | Purpose |
|-----|---------|---------|
| `InMemoryPartitionRetentionSeconds` | 3600 | How long an untouched partition is kept before it is swept. |
| `InMemoryCompactionEveryOperations` | 256 | How often a sweep may be triggered, counted in acquires. |
| `InMemoryCompactionMinIntervalSeconds` | 5 | Floor on the wall-clock gap between sweeps; a sweep walks every live partition on a request thread. |
| `InMemoryMaxPartitions` | 50000 | Ceiling on live partitions per dimension. Passing it forces a sweep, then evicts least-recently-seen partitions down to the ceiling. |

Raise `InMemoryMaxPartitions` if you legitimately serve more than 50 000 distinct partitions (tenants plus anonymous client addresses) within the retention window; evicting a partition resets its bucket. Partitions currently being rejected are touched on every rejection, so they sort last and are not the ones evicted.

## Risks

- **Single-instance:** counters are in-process. The gateway is a single writer against one embedded SQLite file and scales vertically only, so there is no cross-replica fan-out to coordinate — but running two replicas would enforce each tier twice over.
- **In-memory store:** buckets already partly spent are not reset when tiers change; new limits apply from the next acquire. A restart resets every bucket to full.
