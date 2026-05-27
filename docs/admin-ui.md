# Admin UI (`/admin`)

Browser-based operator surface for the 33pol gateway. It shares the same **admin API** (`/admin/api/*`) as the Spectre operator console and automation scripts.

## Static assets

Files under `src/33pol.App/wwwroot/admin/` (served at `/admin/`):

| File | Role |
|------|------|
| `index.html` | Markup and Alpine.js directives only |
| `admin.css` | Layout and theme |
| `admin.js` | `adminApp()` — API client, tab state, forms |
| (CDN) | Alpine.js 3.x |

**Load order:** `admin.css` → deferred `admin.js` → deferred Alpine. No bundler; edit and refresh the browser.

## Access

1. Open **`/admin`** (redirects to `/admin/index.html`).
2. Paste an **Admin** (or **Both**) API key. The UI sends it on every request as `X-API-Key`.
3. The key is persisted in **`localStorage`** under `33pol-admin-key`.

## Pages

| Tab | API | Purpose |
|-----|-----|---------|
| Dashboard | `GET /admin/api/summary` (2s poll) | Live gateway metrics |
| Dashboard | `POST /admin/api/config/reload`, `GET /admin/api/config/status` | File-based model registry reload |
| Dashboard | `GET /admin/api/requests?limit=25` | Recent in-memory request ring |
| Usage | `GET /admin/api/usage`, `/forecast`, `/export` | FinOps rollups and exports |
| Backends | `GET /admin/api/backends` | Model → upstream URL + health |
| Models | `GET/POST/PATCH/DELETE /admin/api/models` | Live registry CRUD |
| API keys | `GET/POST /admin/api/keys`, `POST …/revoke` | Tenant key management |

After adding or editing a model in the UI, verify **`GET /v1/models`** reflects the change without restarting the gateway.

## Threat model (localStorage)

| Risk | Mitigation |
|------|------------|
| **XSS** on `/admin` steals the admin key from `localStorage` | Serve admin only on trusted networks; use CSP and avoid injecting untrusted HTML; prefer short-lived admin keys |
| **Shared workstation** | Anyone with browser profile access can read `localStorage` — clear the key or use a private profile |
| **No HttpOnly cookie** | Keys are visible to any script on the origin; do not embed third-party scripts on admin pages |
| **Key in URL/history** | UI never puts the key in query strings |

For production, prefer network isolation (VPN / admin ingress), rotating admin keys, and audit logs (`config.reload`, `api_key.*`).

## Observability

The admin UI does **not** ship log or trace history. Use Prometheus/Grafana and the guidance in [observability.md](./observability.md).

## Docker + host LLM (LM Studio)

When the gateway runs in Docker, upstream URLs must use `http://host.docker.internal:<port>` (not `localhost`). LM Studio: start the Developer API server and enable **Serve on Local Network**. See [deploy/docker/README.md](../deploy/docker/README.md).

The UI shows short error titles and hides stack traces under **Technical details**. Registry persist failures return JSON `message` (503) instead of raw HTML.

## Manual test checklist

- [ ] Enter admin API key → dashboard metrics update
- [ ] **Models:** add entry (`id`, `url`, `aliases`) → appears on `GET /v1/models`
- [ ] **Models:** edit URL/aliases via PATCH → `GET /v1/models` updated
- [ ] **Config reload** reloads `config/models.json` from disk
- [ ] **Backends** shows health per model
- [ ] **Usage** rollups and export download
- [ ] **API keys:** create shows `secret` once; revoke disables key

## Related

- [operator-console.md](./operator-console.md) — CLI equivalent
- [finops.md](./finops.md) — usage, forecast, webhooks
- Phase plan: [phase-5-finops-ui-ecosystem-and-ga.md](./implementation-plan/phases/phase-5-finops-ui-ecosystem-and-ga.md) (WP5.3)
