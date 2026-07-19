# CORS admin (MVP)

Operators can view and edit **browser allowed origins** from the admin UI or API. Origins must be exact (`https://app.example.com`, no path or trailing slash). Wildcard `*` is rejected.

## API

| Method | Path | Auth |
|--------|------|------|
| `GET` | `/admin/api/cors` | Admin |
| `PUT` | `/admin/api/cors` | Admin |

Body shape (camelCase JSON):

```json
{
  "allowedOrigins": [
    "https://sadeghhp.github.io",
    "http://localhost:5173"
  ]
}
```

Validation (HTTP 400, `{ "message": "…" }`):

- Absolute `http` or `https` origin only
- No path, query, fragment, or user-info
- No wildcard `*`
- At most 100 origins; each origin at most 256 characters
- Blank entries are ignored; duplicates and trailing slashes are normalized away

## Persistence and apply

1. `PUT` merges `Gateway:Cors:AllowedOrigins` into **`appsettings.json`** in the process content root (override path: `Gateway:AppSettingsPath`).
2. Other `Gateway` keys are **preserved**.
3. `IConfigurationRoot.Reload()` runs so `IOptionsMonitor<GatewayOptions>` and the dynamic CORS policy provider pick up new values immediately (no pod restart).

`POST /admin/api/config/reload` reloads **models.json** only; it does not re-read CORS from a separate file.

**Docker note:** Add `GATEWAY_CORS_ALLOWED_ORIGIN_*` or `GATEWAY_CORS_ALLOWED_ORIGINS` in repo-root `.env` and recreate the gateway (`docker compose up -d --force-recreate gateway`). No per-index Compose mapping is required.

## Admin UI

**Settings → CORS allowed origins** — add/remove origin rows, **Save CORS**. Validation errors appear inline under the card.

## Environment behavior

| Environment | Policy |
|-------------|--------|
| Development | Any origin (`*`) |
| Production / Staging | Allowlist only; empty list blocks all browser cross-origin traffic |

## Rollback

1. **UI/API:** Restore known-good origins via **Settings → CORS** or `PUT /admin/api/cors`.
2. **File:** Edit `appsettings.json` under `Gateway:Cors:AllowedOrigins`, then restart **or** issue a `PUT` so reload runs.
3. **Compose:** Set `GATEWAY_CORS_ALLOWED_ORIGIN_*` with the override file and recreate the gateway container.

After rollback, confirm:

- `GET /admin/api/cors` shows expected values.
- `OPTIONS /v1/models` with `Origin: <allowed>` returns `Access-Control-Allow-Origin`.

See also [docs/security.md](../security.md).
