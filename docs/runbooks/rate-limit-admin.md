# Rate limit admin (MVP)

Operators can view and edit **default** and **plan** rate tiers from the admin UI or API. Per-tenant overrides (`RateLimiting:Tenants`) remain configuration-only in this MVP.

## API

| Method | Path | Auth |
|--------|------|------|
| `GET` | `/admin/api/rate-limits` | Admin |
| `PUT` | `/admin/api/rate-limits` | Admin |

Body shape (camelCase JSON):

```json
{
  "default": { "rpm": 60, "burst": 10, "maxConcurrentStreams": 5 },
  "plans": {
    "standard": { "rpm": 120, "burst": 20, "maxConcurrentStreams": 10 }
  }
}
```

Validation (HTTP 400, `{ "message": "…" }`):

- `rpm`: 1 … 1_000_000
- `burst`: 0 … 1_000_000
- `maxConcurrentStreams`: 0 … 10_000
- Plan slugs: non-empty, start with a letter, alphanumeric/`_`/`-`, max 64 chars

## Persistence and apply

1. `PUT` merges `default` and `plans` into **`appsettings.json`** in the process content root (override path: `Gateway:AppSettingsPath`).
2. Existing `RateLimiting:Tenants` and in-memory tuning keys are **preserved**.
3. `IConfigurationRoot.Reload()` runs so `IOptionsMonitor<RateLimitingOptions>` picks up new values immediately (no pod restart).

`POST /admin/api/config/reload` reloads **models.json** only; it does not re-read rate limits from a separate file.

## Admin UI

**Settings → Rate limits** — edit default tier and plan rows, **Save rate limits**. Validation errors appear inline under the card.

## Rollback

1. **UI/API:** Restore known-good numbers via **Settings → Rate limits** or `PUT /admin/api/rate-limits` with the previous payload.
2. **File:** Edit `appsettings.json` (or the file at `Gateway:AppSettingsPath`) under `RateLimiting`, then restart the gateway **or** issue a `PUT` with the corrected JSON so reload runs.
3. **Git/deploy:** Redeploy the last known-good `appsettings.json` from version control and restart if the process does not reload configuration on file replace alone.

After rollback, confirm:

- `GET /admin/api/rate-limits` shows expected values.
- A test inference call respects the tier (e.g. temporarily set `default.rpm` to `1` and verify a second request returns 429).

## Risks

- **Multi-replica:** Each pod has its own `appsettings.json` unless shared storage or an external config source is used; admin `PUT` only updates the pod that served the request.
- **In-memory store:** RPM windows already counted are not reset on reload; only new limits apply to subsequent acquires.
