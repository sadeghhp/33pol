# Admin UI (`/admin`)

Browser-based operator surface for the 33pol gateway. It shares the same **admin API** (`/admin/api/*`) as the Spectre operator console and automation scripts.

## Static assets

Files under `src/33pol.App/wwwroot/admin/` (served at `/admin/`):

| File | Role |
|------|------|
| `index.html` | App shell, pages, drawers, dialogs |
| `admin.css` | Design tokens, layout, components |
| `admin-errors.js` | `AdminErrors.classifyError` — shared error taxonomy |
| `admin-store.js` | `Alpine.store('admin')` — API client, loading scopes, toasts, connection |
| `admin-app.js` | `adminApp()` — navigation and feature logic |
| (CDN) | Alpine.js 3.x |

**Load order:** `admin.css` → `admin-errors.js` → `admin-store.js` → `admin-app.js` → Alpine (all deferred). Query `?v=3` on static assets busts caches after upgrades.

**Cache:** `/admin/*` static files are served with `Cache-Control: no-store`.

## Access

1. Open **`/admin`** (redirects to `/admin/index.html`).
2. Paste an **Admin** (or **Both**) API key on the sign-in screen. Click **Connect** (or Enter).
3. After connect, the header shows key prefix + **Connected** / **Invalid key**. Use **Change key** or **Sign out** as needed.
4. The key is persisted in **`localStorage`** under `33pol-admin-key`.

**Navigation:** Sidebar sections use URL hash (`#/dashboard`, `#/usage`, `#/routing`, `#/keys`, `#/settings`) and `sessionStorage` for the last tab. Legacy hashes `#/models` and `#/backends` redirect to **Routing** (Models / Backends sub-tabs).

## Information architecture

| Section | Hash | Content |
|---------|------|---------|
| Overview | `#/dashboard` | Metrics (2s poll while active + visible), health chips, recent requests |
| Usage | `#/usage` | Date presets, unified **Apply range**, rollups, events, forecast, export |
| Routing | `#/routing` | **Models** (registry, discover, drawer) and **Backends** (health table) |
| API keys | `#/keys` | List, create (drawer), revoke (modal) |
| Settings | `#/settings` | Config status, reload from disk, observability links |

## Errors and feedback

| Situation | Where shown |
|-----------|-------------|
| Invalid admin key (401) | Global banner + header chip |
| Network / gateway unreachable | Global banner |
| Provider fetch / validation | Inline under discover (not duplicated globally) |
| Model save validation | Inline in model drawer |
| Success actions | Toast (top-right, auto-dismiss) |
| Unhandled 5xx / HTML errors | Global banner; stack/detail under **Technical details** |

GET requests retry once on network failure. Usage export uses `downloadBlob` with the same error mapping as JSON APIs.

## API surface (by section)

| Section | Endpoints |
|---------|-----------|
| Overview | `GET /admin/api/summary`, `GET /admin/api/requests?limit=25`, `GET /health/live`, `GET /health/ready` |
| Usage | `GET /admin/api/usage`, `/usage/events`, `/usage/forecast`, `GET /usage/export` |
| Routing — Models | `GET/POST/PATCH/DELETE /admin/api/models`, provider catalog + POST discovery |
| Routing — Backends | `GET /admin/api/backends` |
| API keys | `GET/POST /admin/api/keys`, `POST …/revoke` |
| Settings | `GET /admin/api/config/status`, `POST /admin/api/config/reload` |

After adding or editing a model, verify **`GET /v1/models`** (link on Routing → Models).

## Provider model discovery

1. On the gateway host (or Docker `environment`), set the upstream secret, e.g. `OPENROUTER_API_KEY=sk-or-…`.
2. **Routing → Discover from provider** — enter the **variable name** (`OPENROUTER_API_KEY`), not the secret.
3. Click **Fetch models**. Errors appear inline on the discover panel.
4. Click **Add** on a row to open the model drawer for review and save.

### Troubleshooting

| Symptom | Cause | Fix |
|---------|--------|-----|
| 400 “not the API key” | Pasted `sk-…` into env var field | Use `OPENROUTER_API_KEY`; set secret on gateway |
| 400 “Missing API token” | Env var name correct but unset on gateway | Add to `.env` / compose and restart |
| Stale UI after upgrade | Cached admin assets | Hard refresh; assets use `?v=3` |
| GET provider models returns 405 | Old client | Use POST with JSON body |

## Security audit (strict)

| # | Area | Verdict | Notes |
|---|------|---------|--------|
| 1 | Provider discovery transport | **PASS** | UI uses **POST** only; no `?envVar=` / `?modelsUrl=` in JS |
| 2 | Provider discovery API | **PASS** | GET on discovery paths returns **405**; POST + `EnvVarNameValidator` |
| 3 | Registry `upstreamAuth.envVar` | **PASS** | Server rejects secret-like names on POST/PATCH model |
| 4 | Add/edit model form (UI) | **PASS** | Client validates env var name before save |
| 5 | Admin API key in URL | **PASS** | **`X-API-Key` header** only |
| 6 | Admin key storage | **ACCEPTED RISK** | `localStorage` — XSS can exfiltrate |
| 7 | New inference key display | **ACCEPTED** | Secret shown once in create drawer |
| 8 | Usage export | **LOW RISK** | Dates/format in query; key in header only |
| 9 | Usage/events query | **LOW RISK** | Optional `tenantId`, dates — no secrets |
| 10 | Alpine.js CDN | **DEFERRED** | Post-GA self-host + CSP (G-20) |
| 11 | Static asset caching | **PASS** | `/admin/*` → `no-store` |
| 12 | Proxy/access logs | **NOTE** | Legacy GET with secrets in query may still be logged |

**Automated checks:** `AdminUiSecurityTests`, `AdminUiIntegrationTests` (JS/HTML contracts).

## Threat model (localStorage)

| Risk | Mitigation |
|------|------------|
| **XSS** on `/admin` steals the admin key | Trusted network; CSP; short-lived admin keys |
| **Shared workstation** | **Sign out** or private browser profile |
| **Key in URL/history** | Provider discovery uses **POST**; admin key in header only |

## Docker + host LLM (LM Studio)

When the gateway runs in Docker, upstream URLs must use `http://host.docker.internal:<port>` (not `localhost`). Use **Templates** in the model drawer or see **[lm-studio-with-33pol.md](./lm-studio-with-33pol.md)**.

## Manual test checklist

- [ ] Connect with admin API key → **Connected**; Overview metrics load automatically
- [ ] `#/routing` — Models and Backends sub-tabs; legacy `#/models` / `#/backends` still work
- [ ] **Usage:** presets → **Apply range** → rollups + events; export JSON/CSV
- [ ] **Routing:** Discover → Fetch → Add → save in drawer → `GET /v1/models` updated
- [ ] **Providers:** paste `sk-…` in env var → inline error only (no POST)
- [ ] **Providers:** Network tab shows **POST** for fetch
- [ ] **Models:** remove uses confirm dialog (not `confirm()`)
- [ ] **Backends:** unhealthy first; **Edit model** jumps to Models
- [ ] **API keys:** create drawer → copy → acknowledge saved; revoke modal
- [ ] **Settings:** config status; reload with confirm
- [ ] Sign out clears session; Escape closes drawer/modal

## Deferred (post-GA)

- SSE live dashboard (`GET /admin/api/events/stream`, G-12)
- Self-hosted Alpine + strict CSP (G-20)
- Playwright E2E (G-20)

## Related

- [operator-console.md](./operator-console.md) — CLI equivalent
- [finops.md](./finops.md) — usage, forecast, webhooks
- Taiga: **US-admin-enhance** (#613) — admin UI overhaul
