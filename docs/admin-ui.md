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

**Load order:** `admin.css` → `admin-store.js` → `admin.js` → Alpine (all deferred). Query `?v=2` on static assets busts caches after upgrades.

**Cache:** `/admin/*` static files are served with `Cache-Control: no-store`.

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
| Models | `GET/POST/PATCH/DELETE /admin/api/models` | Registry CRUD, templates |
| Models | `GET /admin/api/providers/catalog` | Built-in provider list |
| Models | `POST /admin/api/providers/{id}/models` | Fetch models — JSON body `{ "envVar": "OPENROUTER_API_KEY" }` only |
| Models | `POST /admin/api/providers/models` | Custom provider — `{ "modelsUrl", "envVar" }` in body |
| API keys | `GET/POST /admin/api/keys`, `POST …/revoke` | Key management (modal revoke) |

After adding or editing a model, verify **`GET /v1/models`** (link on Models tab).

## Provider model discovery

1. On the gateway host (or Docker `environment`), set the upstream secret, e.g. `OPENROUTER_API_KEY=sk-or-…`.
2. In the admin UI **Models** tab, enter the **variable name** (`OPENROUTER_API_KEY`), not the secret.
3. Click **Fetch models**. Errors appear in a sticky top banner and inline under the fetch button.

### Troubleshooting

| Symptom | Cause | Fix |
|---------|--------|-----|
| 400 “not the API key” | Pasted `sk-…` into env var field | Use `OPENROUTER_API_KEY`; set secret on gateway |
| 400 “Missing API token” | Env var name correct but unset on gateway | Add to `.env` / compose and restart |
| Still see `GET ?envVar=` in DevTools | Stale cached `admin.js` | Hard refresh; rebuild gateway; assets use `?v=2` |
| GET provider models returns 405 | Old client / bookmark | Use POST with JSON body |

## Security audit (strict)

| # | Area | Verdict | Notes |
|---|------|---------|--------|
| 1 | Provider discovery transport | **PASS** | UI uses **POST** only; no `?envVar=` / `?modelsUrl=` in JS |
| 2 | Provider discovery API | **PASS** | GET on discovery paths returns **405**; POST + `EnvVarNameValidator` |
| 3 | Registry `upstreamAuth.envVar` | **PASS** | Server rejects secret-like names on POST/PATCH model (`ModelConfigValidation`) |
| 4 | Add/edit model form (UI) | **PASS** | Client validates env var name before save |
| 5 | Admin API key in URL | **PASS** | **`X-API-Key` header** only (never query string) |
| 6 | Admin key storage | **ACCEPTED RISK** | `localStorage` — XSS can exfiltrate; trusted network + short-lived keys |
| 7 | New inference key display | **ACCEPTED** | Secret shown once in DOM after create; cleared on sign-out |
| 8 | Usage export | **LOW RISK** | `GET /usage/export?from=&to=&format=` — dates/format in query; admin key in header only |
| 9 | Usage/events query | **LOW RISK** | Optional `tenantId` (GUID), dates — no secrets |
| 10 | Alpine.js CDN | **DEFERRED** | Supply-chain; post-GA self-host + CSP (G-20) |
| 11 | Static asset caching | **PASS** | `/admin/*` → `Cache-Control: no-store` |
| 12 | Proxy/access logs | **NOTE** | Legacy GET with secrets in query may still be logged if an old client calls them; use POST |

**Automated checks:** `AdminUiSecurityTests` (JS must not build provider query strings; POST model with `sk-…` env var → 400).

## Threat model (localStorage)

| Risk | Mitigation |
|------|------------|
| **XSS** on `/admin` steals the admin key from `localStorage` | Serve admin only on trusted networks; use CSP and avoid injecting untrusted HTML; prefer short-lived admin keys |
| **Shared workstation** | Use **Sign out** or a private browser profile |
| **No HttpOnly cookie** | Keys are visible to any script on the origin; do not embed third-party scripts on admin pages |
| **Key in URL/history** | Provider discovery uses **POST**; admin API key never in query strings |

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
- [ ] **Providers:** paste `sk-…` in env var field → inline + global error (no request)
- [ ] **Providers:** Network tab shows **POST** for fetch (not GET with query)
- [ ] **Models:** paste `sk-…` in auth env var → save blocked (client + server)
- [ ] **Backends:** filter; unhealthy rows sort first
- [ ] **API keys:** create → Copy secret; revoke via modal; sign out clears created secret

## Deferred (post-GA)

- SSE live dashboard (`GET /admin/api/events/stream`, G-12)
- Self-hosted Alpine + strict CSP
- Playwright E2E (G-20)
- POST usage export (optional hardening; GET export is low risk today)

## Related

- [operator-console.md](./operator-console.md) — CLI equivalent
- [finops.md](./finops.md) — usage, forecast, webhooks
- Taiga: **US-P5-10** (#624, tasks #625–#630) — provider discovery error UX; see [post-ga-backlog.md](./post-ga-backlog.md). Broader: **US-admin-enhance** (#613).
