# Admin UI (`/admin`)

Browser-based operator surface for the 33pol gateway. It shares the same **admin API** (`/admin/api/*`) as the Spectre operator console and automation scripts.

## Static assets

Files under `src/33pol.App/wwwroot/admin/` (served at `/admin/`):

| File | Role |
|------|------|
| `index.html` | Markup and Alpine.js directives |
| `admin.css` | Layout, theme, modals |
| `admin-store.js` | `Alpine.store('admin')` — API client, errors, loading |
| `admin.js` | `adminApp()` — tab state and feature logic |
| (CDN) | Alpine.js 3.x |

**Load order:** `admin.css` → `admin-store.js` → `admin.js` → Alpine (all deferred). No bundler; edit and refresh the browser.

## Access

1. Open **`/admin`** (redirects to `/admin/index.html`).
2. Paste an **Admin** (or **Both**) API key. Click **Save** (or Enter). Connection badge shows **Connected** / **Invalid key**.
3. The key is persisted in **`localStorage`** under `33pol-admin-key`. **Sign out** clears it.

**Navigation:** Tabs use URL hash (`#/dashboard`, `#/models`, …) and `sessionStorage` for the last tab.

## Pages

| Tab | API | Purpose |
|-----|-----|---------|
| Dashboard | `GET /admin/api/summary` (2s poll when tab active + page visible) | Live gateway metrics |
| Dashboard | `GET /health/live`, `/health/ready`, link `/metrics` | Process health |
| Dashboard | `POST /admin/api/config/reload`, `GET /admin/api/config/status` | File-based registry reload |
| Dashboard | `GET /admin/api/requests?limit=25` | Recent in-memory request ring |
| Usage | `GET /admin/api/usage?from=&to=`, `/usage/events`, `/forecast`, `/export` | FinOps rollups, events, export |
| Backends | `GET /admin/api/backends` | Model → upstream URL + health (filter + unhealthy first) |
| Models | `GET/POST/PATCH/DELETE /admin/api/models` | Registry CRUD, templates, OpenRouter list |
| Models | `GET /admin/api/providers/catalog` | Built-in provider list |
| Models | `GET /admin/api/providers/{id}/models` | Fetch models (OpenRouter, Together, …) |
| Models | `GET /admin/api/providers/models` | Custom provider (`modelsUrl` + `envVar`) |
| API keys | `GET/POST /admin/api/keys`, `POST …/revoke` | Key management (modal revoke) |

After adding or editing a model, verify **`GET /v1/models`** (link on Models tab).

## Threat model (localStorage)

| Risk | Mitigation |
|------|------------|
| **XSS** on `/admin` steals the admin key from `localStorage` | Serve admin only on trusted networks; use CSP and avoid injecting untrusted HTML; prefer short-lived admin keys |
| **Shared workstation** | Use **Sign out** or a private browser profile |
| **No HttpOnly cookie** | Keys are visible to any script on the origin; do not embed third-party scripts on admin pages |
| **Key in URL/history** | UI never puts the key in query strings |

For production, prefer network isolation (VPN / admin ingress), rotating admin keys, and audit logs (`config.reload`, `api_key.*`).

## Observability

The admin UI does **not** ship log or trace history. Dashboard links to **Prometheus** (`/metrics`). Use Grafana per [observability.md](./observability.md).

## Docker + host LLM (LM Studio)

When the gateway runs in Docker, upstream URLs must use `http://host.docker.internal:<port>` (not `localhost`). Use **Templates** on the Models tab or see **[lm-studio-with-33pol.md](./lm-studio-with-33pol.md)**.

The UI shows short error titles and hides stack traces under **Technical details**. Registry persist failures return JSON `message` (503) instead of raw HTML.

## Manual test checklist

- [ ] Save admin API key → **Connected** badge; dashboard metrics update
- [ ] Hash `#/models` opens Models tab; refresh keeps tab
- [ ] **Usage:** date range → rollups + events + export with same range
- [ ] **Models:** template → save → `GET /v1/models` updated
- [ ] **Providers:** pick provider → fetch list → Use → save with upstream auth
- [ ] **Backends:** filter; unhealthy rows sort first
- [ ] **API keys:** create → Copy secret; revoke via modal

## Deferred (post-GA)

- SSE live dashboard (`GET /admin/api/events/stream`, G-12)
- Self-hosted Alpine + strict CSP
- Playwright E2E (G-20)

## Related

- [operator-console.md](./operator-console.md) — CLI equivalent
- [finops.md](./finops.md) — usage, forecast, webhooks
- Taiga: **US-admin-enhance** (#613)
