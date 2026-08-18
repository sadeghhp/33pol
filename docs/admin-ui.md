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
| `admin-app.js` | `adminApp()` — navigation, feature logic, and the CSP view layer |
| `admin-icons.js` | `AdminIcons(name)` and `AdminIcons.map` — inline SVG set |
| `vendor/alpine-csp-3.14.9.min.js` | Alpine.js 3.14.9, **CSP build**, self-hosted |
| `vendor/fonts.css`, `vendor/fonts/` | Self-hosted IBM Plex / Space Grotesk |

**Load order:** `admin.css` → `admin-icons.js` → `admin-errors.js` → `admin-store.js` → `admin-app.js` → Alpine (all deferred). Query `?v=N` on static assets busts caches after upgrades.

### Writing markup for the CSP build

The console ships Alpine's **CSP-friendly build** so `/admin` can be served under `script-src 'self'`
(see `AdminSecurityHeaders.cs`); the stock build compiles every directive with `new Function()` and
needs `unsafe-eval`. That build's evaluator resolves a directive's value as a **property path and
nothing else** — a function it finds there is invoked with the directive's own arguments (the event,
for `x-on`), and nothing else is parsed. So in `index.html`:

- no operators, ternaries, optional chaining, or calls with arguments — `x-text="formatNum(n)"`,
  `:class="{ active: tab === 'keys' }"` and `x-show="a && b"` all fail silently at runtime;
- every displayed value comes from a getter or a zero-argument method on `adminApp`
  (`x-text="totalErrorsText"`, `x-html="icons.trash"`);
- `x-model` needs a `{get, set}` pair, which `adminApp.mdl` supplies, shaped like the state it
  writes: `x-model="mdl.editModel.url"`;
- per-row values and actions are precomputed onto the row objects, so a template reaches
  `copyText(id)` as `@click="r.copyId"`;
- `x-for` clones only the template's **first** element — a row plus its detail panel must share one
  root (the requests and logs tables wrap each pair in its own `<tbody>`);
- x-bind writes attributes and supports only the `.camel` modifier in 3.14.9, so a property with no
  attribute behind it needs a directive — hence `x-indeterminate` for the select-all checkbox.

`AdminAssetSecurityTests.AdminIndex_UsesOnlyExpressionsTheCspEvaluatorCanResolve` enforces this, so a
directive that the evaluator could not resolve fails the build instead of the operator's browser.

**Cache:** `/admin/*` static files are served with `Cache-Control: no-store`.

## Access

1. Open **`/admin`** (redirects to `/admin/index.html`).
2. Paste an **Admin** (or **Both**) API key on the sign-in screen. Click **Connect** (or Enter).
3. After connect, the header shows key prefix + **Connected** / **Invalid key**. Use **Change key** or **Sign out** as needed.
4. The key is persisted in **`localStorage`** under `33pol-admin-key`.

**Navigation:** Sidebar sections use URL hash (`#/dashboard`, `#/usage`, `#/routing`, `#/keys`, `#/logs`, `#/errors`, `#/settings`) and `sessionStorage` for the last tab. Legacy hashes `#/models` and `#/backends` redirect to **Routing** (Models / Backends sub-tabs). The Errors tab also accepts filters in the hash — `#/errors?model=gpt-4o&status=502&code=upstream_error&range=24h` — which is how the Overview tile and the errors-by-model bars deep-link into it.

## Information architecture

| Section | Hash | Content |
|---------|------|---------|
| Overview | `#/dashboard` | Metrics, health chips, in-flight requests, recent requests with cost centre / tokens / cost — pushed over SSE, 2s poll as fallback |
| Usage | `#/usage` | Date presets, cost center / API key filters, rollups, enriched billing events, forecast, export |
| Routing | `#/routing` | **Models** (registry, quick-add drawer) and **Backends** (health table) |
| API keys | `#/keys` | List (assignee, MTD usage, last used), create/edit metadata, per-key model access, revoke, view usage |
| Logs | `#/logs` | In-memory diagnostic tail (warning and above), severity and search filters, expandable detail with hint and request ID, clear buffer |
| Errors | `#/errors` | Persisted, grouped failures: time-range presets, model / status / code facets, occurrence drill-down with stack trace, request-ID cross-link, JSON+CSV export, clear all |
| Settings | `#/settings` | Config status, rate limits (default + plans), tenant model allowlist, observability links |

### Logs vs Errors

Two panes, deliberately different, and the distinction is what keeps either useful:

| | Logs | Errors |
|---|---|---|
| Holds | Every `ILogger` warning and above | Failures only, grouped by fingerprint |
| Survives restart | No — a bounded in-process ring | Yes, when a database is configured |
| Shape | Flat, newest first | One row per distinct fault, with occurrence count and first/last seen |
| Clearing | `DELETE /admin/api/logs` empties the ring | `DELETE /admin/api/errors?confirm=true` also zeroes the error counters and rewrites the persisted snapshot |

Neither touches the durable logs written by the gateway's configured log providers.

Two consequences worth knowing before they read as bugs:

- **The Errors tab can show fewer errors than the Overview counter.** Client disconnects
  (`client_canceled`) are counted as errors by the aggregate counter but deliberately not recorded:
  the caller walked away, and filling the error store with disconnects buries the real faults.
- **Clearing errors leaves total requests and average latency alone.** The error rate therefore
  reads 0% against a non-zero request total until new traffic arrives. Clearing errors must not
  silently rewrite the throughput history alongside them; `scope=all` is the explicit opt-in that
  resets the whole counter snapshot.

**Polling and push:** on the Overview the console opens `GET /admin/api/live`, a server-sent-event
stream that delivers `{ version, summary, requests }` the moment activity changes; while it is
delivering, the 2s summary/requests poll is skipped. If the stream cannot be established (a
buffering proxy, a dropped connection) the poll takes over and the badge beside **Refresh** reads
**Polling** or **Reconnecting** instead of **Streaming**; reconnects back off from 1s to 15s. The
vitals bar still polls every 2s on other tabs. Logs and Errors quiet-poll every 10s, only while
their tab is on screen and auto-refresh is on. All polling and streaming stops when the tab is
hidden or the key has been rejected.

## Errors and feedback

| Situation | Where shown |
|-----------|-------------|
| Invalid admin key (401) | Header chip + inline hint (no duplicate global banner) |
| Network / gateway unreachable | Global banner |
| Model save validation | Inline in model drawer |
| Success actions | Toast (top-right, auto-dismiss) |
| Unhandled 5xx / HTML errors | Global banner; stack/detail under **Technical details** |

### Gateway inference errors (Overview)

| Data | Where shown |
|------|-------------|
| Aggregate error count | **Errors** metric card (red when &gt; 0) |
| Per-model error counts | **Errors by model** table (from `summary.errorsPerModel`; hidden when zero) |
| Recent failed forwards | **Recent requests** — **Error** column (`errorCode` from `X-33pol-Error-Code`) |
| Request correlation | **Request ID** column (copy button; click row for full ID, tenant, streaming flag) |
| Filter failures | **Errors only** checkbox on Recent requests |

Overview is **pushed** while the tab is visible (see *Polling and push* above), with a 2s poll as
the fallback. Both pause while the key is rejected (`connectionStatus === 'fail'`), so a stale tab
does not retry a 401 forever.

### Cost, tokens and cost centre on the feed

| Data | Where shown |
|------|-------------|
| Cost centre the request bills to (the key's own, else the tenant default) | **Cost center** column, and the detail panel; present on in-flight rows too |
| Prompt → completion tokens (or the combined total when the upstream reported no split) | **Tokens** column; detail panel lists prompt, completion, total, source and output tokens/s |
| Input cost, output cost, total | **Cost** column (total); detail panel splits **Input cost** / **Output cost** / **Total cost** |
| Pricing state | Cost cell reads `pricing…` while the usage event is queued, an amount once priced, `unpriced` when the model has no rate card (or the gateway has no billing store), `—` when the request produced no usage |
| What the visible tail adds up to | Strip above the table: in flight, shown, errors, priced spend (and how many rows are still pricing), tokens, distinct cost centres |

Token counts are stamped on the row when the response finishes; the cost arrives one usage-writer
flush later (`Billing:UsageWriterFlushIntervalMs`, 1s by default) and the row updates in place —
over the push stream that is the next frame, over polling the next 2s tick.

### In-flight requests

Requests appear on the console as soon as forwarding starts, not only once they finish:

| Data | Where shown |
|------|-------------|
| Requests being forwarded right now | **In flight** metric card and the top-bar `in flight` chip (`summary.activeRequests`) |
| Streaming subset | Card sub-line and the `streams` chip (`summary.activeStreams`) |
| What is running, per model | **Running now** chips under the vitals (`summary.activeRequestsPerModel`) |
| The individual calls | **Recent requests** — in-flight rows are tinted, show `···` for status and a live-growing duration |

`activeStreams` is the streaming subset of `activeRequests`, so a non-streaming completion or
embedding in progress moves the latter only. In-flight entries live in memory, are ordered ahead of
completed ones, and are never written to the durable stats snapshot.

**Counted populations:** Overview counters are **gateway-wide across all tenants** (the endpoints
require the `Operator` policy); **Usage & cost** is scoped to the caller's tenant and derived from
persisted billing events. On a multi-tenant gateway the two request totals legitimately differ, and
both pages say so.

**Error accounting:** a proxied upstream 4xx counts as an error on the dashboard (the client got an
error) but *not* as a circuit-breaker failure (the backend answered). Requests rejected at admission
— unhealthy backend, open circuit, full bulkhead, exhausted stream slot — count toward requests,
errors and **Errors by model**, and appear in the feed. They contribute no latency, since admission
takes microseconds and would drag the mean toward zero.

**Limitation:** middleware-only failures that never reach the router (rate limit, quota, invalid API
key, `model_not_found`) still do **not** appear in the recent-requests ring buffer — use the
**Errors** tab, the **Rate-limited** and **Quota-blocked** stats, Grafana, or structured logs with
`X-Request-Id`. Unhandled exceptions on any route, including admin routes, are captured by the
terminal handler and do reach the Errors tab with their stack trace.

If the gateway error body was not written (e.g. forward failed after the upstream response already started), **Error** may be empty even when **Status** is 4xx/5xx.

GET requests retry once on network failure. Usage export uses `downloadBlob` with the same error mapping as JSON APIs and saves under the server's `Content-Disposition` filename.

### Usage & cost

- **Scope.** Rollups, ledger, chart, tiles, forecast and exports all honour the same filter: UTC date
  range, cost centre (case-insensitive; `(none)` = rows without one; a datalist offers the known
  values), API key, and **Include anonymous usage** — requests to public models sent without a key,
  which are priced but belong to no tenant. The toggle defaults on and is remembered per browser.
  With a key selected the whole page is aggregated from the ledger for that key.
- **Presets** are inclusive UTC calendar days ("Last 7 days" = 7 days ending today) and show as
  selected when the inputs match. `From > To` is flagged inline and disables Apply/exports.
- **Tiles.** Cost is the selected range; **Projected this month** is month-to-date plus the average
  of the last 7 complete UTC days for each remaining day (same filters). Requests notes how many were
  anonymous. Money renders sub-cent amounts with three significant digits and unpriced as `—`.
- **Unpriced usage** banner lists models in the range that have no rate card.
- **Chart** has a y-axis and one column per UTC day in the range, zero days included.
- **Tables.** Rollups are sortable, newest first, 100 at a time; the ledger shows UTC timestamps
  (local time on hover), pages 50 at a time with **Load 50 more**, tags anonymous rows, and dims
  unpriced costs. Exports offer rollups or events, CSV or JSON, capped at 5,000 events with a toast
  when truncated.

## API surface (by section)

| Section | Endpoints |
|---------|-----------|
| Overview | `GET /admin/api/live?limit=25` (SSE; falls back to `GET /admin/api/summary` + `GET /admin/api/requests?limit=25`), `GET /health/live`, `GET /health/ready` |
| Usage | `GET /admin/api/usage?costCenter=`, `/usage/events?apiKeyId=&costCenter=`, `/usage/forecast`, `GET /usage/export` |
| Routing — Models | `GET/POST/PATCH/DELETE /admin/api/models` (write body: `{ model, apiKey?, clearApiKey? }`; GET returns `{ model, hasUpstreamCredential }`), `POST /admin/api/models/{id}/test` (type-specific health check) |
| Routing — Backends | `GET /admin/api/backends` |
| API keys | `GET/POST /admin/api/keys`, `PATCH …/keys/{id}`, `GET …/keys/{id}/usage`, `POST …/revoke`, `GET/PUT …/keys/{id}/model-grants` |
| Tenant model access | `GET/PUT /admin/api/tenant/model-grants` (optional ceiling; empty = all registry models) |
| Logs | `GET /admin/api/logs?limit=&level=&search=` (response carries `count`, `total`, `capacity`), `DELETE /admin/api/logs` (audited) |
| Errors | `GET /admin/api/errors/groups`, `GET /admin/api/errors` (occurrences; `?fingerprint=`, `?requestId=`), `GET /admin/api/errors/{id}`, `GET /admin/api/errors/facets`, `GET /admin/api/errors/export?format=json\|csv`, `DELETE /admin/api/errors?confirm=true[&scope=all]` (audited) |

**Per-key model access:** Inference keys start with **no models** allowed. Open **Models** on a key, check the registry models it may call, and save. `GET /v1/models` and inference only expose models in that allowlist (intersected with tenant policy when the tenant is restricted).

**API key metadata:** Create or **Edit** keys with **Label**, **Assignee**, **Cost center**, and **Description**. Assignee is for ownership display; cost center is independent and drives FinOps rollups when set (overrides tenant default on inference). List with `GET /admin/api/keys?includeUsageSummary=true` for month-to-date cost/request counts and **Last used**.

**Usage filters:** Usage tab supports **Cost center** and **API key** filters on rollups (cost center) and billing events (both). Use **Usage** on a key row to jump here filtered to that key.
| Settings | `GET /admin/api/config/status`, `POST /admin/api/config/reload`, `GET/PUT /admin/api/rate-limits` |

**Errors query parameters** (shared by groups, occurrences and export): `from`, `to` (ISO-8601),
`level`, `modelId`, `status`, `code`, `tenantId`, `requestId`, `fingerprint`, `search`,
`sort=lastSeen|firstSeen|count`, `limit` (max 200; 10,000 for export), `offset` (max 10,000).
Both list responses report `total` as the count matched **before** paging, plus `source`
(`database` or `memory`) and `persisted`, so the console can say when errors are in-memory only.

Rate limit changes are written to `appsettings.json` and applied via configuration reload (see [rate-limit-admin.md](./runbooks/rate-limit-admin.md)).

After adding or editing a model, verify **`GET /v1/models`** (link on Routing → Models).

## Quick-add model (name + URL + API key)

1. **Routing → Add model** (or edit an existing row).
2. Enter **model name**, **upstream URL**, and optionally an **API key** (password field). Leave the key empty for local upstreams with no auth.
3. **Save**. The gateway stores the key in an encrypted file (`config/upstream-secrets.enc` by default) and sets `upstreamAuth.secretRef` in the registry — **never** the raw key in `models.json`.
4. On edit, **Credential stored** means a secret exists; enter a **new API key** to rotate, or check **Remove stored API key** to clear it.

**URL presets** in the drawer only fill the upstream URL (OpenRouter, Together, Groq, LM Studio, vLLM).

### Model type and the Test button

Each model carries a **model type** (`modelType` in the registry) that decides which health check the
**Test** button runs. Set it in the model drawer; the Routing → Models table shows it per row.

| Model type | Test probes | Passes when |
|------------|-------------|-------------|
| Text generation | `POST /v1/chat/completions` | response has `choices` |
| Embedding | `POST /v1/embeddings` with `{ model, input: [<two test sentences>] }` | response has `data[].embedding` vectors of equal length |
| Rerank | `POST /v1/rerank` | response has `results` |
| OCR | `POST /v1/chat/completions` (OCR models are served as vision chat models) | response has `choices` |
| Image / video generation, audio transcription | *nothing* — the dialog reports **Not available** rather than a false failure | — |

The response records the type and endpoint it used (`modelType`, `endpoint`), so the dialog states
which upstream route was actually called. A `2xx` whose body does not match the expected shape is
reported as a failure — an embeddings upstream answering a chat probe is not a healthy model.

`modelType` is optional. When unset, the gateway infers it from a single-purpose `capabilities` list
(so models registered before the field existed still classify) and otherwise treats the model as
text generation. An unrecognised value is rejected with `400` on save.

**GitOps / env-var auth:** Existing entries with `upstreamAuth.envVar` still work. Set secrets in Docker `.env` and use the variable name in JSON — no UI discover flow required.

**Provider discovery API** (`POST /admin/api/providers/...`) remains for scripts/automation (env-based only); there is no discover panel in the UI.

### Upstream secrets file

| Setting | Default |
|---------|---------|
| Path | `Gateway:UpstreamSecretsPath` → `config/upstream-secrets.enc` |
| Encryption key | Derived from `Gateway:Security:KeyPepper` |

Rotating **KeyPepper** invalidates stored upstream secrets — re-enter API keys in admin. Back up the writable `config/` volume in Docker (same mount as `models.json`).

### Troubleshooting

| Symptom | Cause | Fix |
|---------|--------|-----|
| 400 on save with `envVar` in JSON | Secret pasted as env var name | Use quick-add **API key** field, or a valid name like `OPENROUTER_API_KEY` in the model route's `envVar` |
| Upstream 401 | Missing or wrong stored key | Edit model → set new API key; verify `hasUpstreamCredential` on GET |
| Test fails on an embedding model | Model type left as text generation, so the probe hit `/v1/chat/completions` | Edit model → set **Model type** to *Embedding* |
| Stale UI after upgrade | Cached admin assets | Hard refresh; every local asset carries `?v=N`, enforced by `AdminAssetSecurityTests.AdminIndex_CacheBustsEveryLocalAsset` |
| Docker local LLM fails | Used `localhost` in URL | Use `http://host.docker.internal:<port>` |

## Security audit (strict)

| # | Area | Verdict | Notes |
|---|------|---------|--------|
| 1 | Model write transport | **PASS** | API keys in **POST/PATCH JSON body** only (`apiKey`); not in query strings |
| 2 | Provider discovery API | **PASS** | GET on discovery paths returns **405**; POST + `EnvVarNameValidator` (automation) |
| 3 | Registry `upstreamAuth.envVar` | **PASS** | Server rejects secret-like env var names on model write |
| 4 | Upstream secrets at rest | **PASS** | Encrypted file store; GET models never returns `apiKey` |
| 5 | Admin API key in URL | **PASS** | **`X-API-Key` header** only |
| 6 | Admin key storage | **ACCEPTED RISK** | `localStorage` — XSS can exfiltrate |
| 7 | New inference key display | **ACCEPTED** | Secret shown once in create drawer |
| 8 | Usage export | **LOW RISK** | Dates/format in query; key in header only |
| 9 | Usage/events query | **LOW RISK** | Optional `tenantId`, dates — no secrets |
| 10 | Alpine.js CDN | **PASS** | Self-hosted CSP build; `script-src 'self'`, no `unsafe-eval` |
| 11 | Static asset caching | **PASS** | `/admin/*` → `no-store` |
| 12 | Proxy/access logs | **NOTE** | Legacy GET with secrets in query may still be logged |

**Automated checks:** `AdminUiSecurityTests`, `AdminUiIntegrationTests` (JS/HTML contracts).

## Threat model (localStorage)

| Risk | Mitigation |
|------|------------|
| **XSS** on `/admin` steals the admin key | Trusted network; CSP; short-lived admin keys |
| **Shared workstation** | **Sign out** or private browser profile |
| **Key in URL/history** | Model `apiKey` in POST body only; admin key in header only |

## Docker + host LLM (LM Studio)

When the gateway runs in Docker, upstream URLs must use `http://host.docker.internal:<port>` (not `localhost`). Use **Templates** in the model drawer or see **[lm-studio-with-33pol.md](./lm-studio-with-33pol.md)**.

## Manual test checklist

- [ ] Connect with admin API key → **Connected**; Overview metrics load automatically
- [ ] Overview **Errors by model** appears when any model has errors since process start
- [ ] Overview **Recent requests** shows Request ID and Error columns; **Errors only** filter works
- [ ] `#/routing` — Models and Backends sub-tabs; legacy `#/models` / `#/backends` still work
- [ ] **Usage:** presets → **Apply range** → rollups + events; export JSON/CSV
- [ ] **Routing:** Add model (name + URL + API key) → save → `GET /v1/models` lists id
- [ ] **Routing:** Edit → rotate API key → upstream still works
- [ ] **Routing:** Edit → remove stored key → `hasUpstreamCredential` false
- [ ] **Models:** remove uses confirm dialog (not `confirm()`)
- [ ] **Backends:** unhealthy first; **Edit model** jumps to Models
- [ ] **API keys:** create drawer → copy → acknowledge saved; revoke modal
- [ ] **Settings:** config status; reload with confirm
- [ ] **Logs:** typing in search neither flashes the skeleton nor raises the global banner; the
      truncation hint reads "Showing N of M matching entries"; Request ID is populated
- [ ] **Errors:** repeated failures collapse into one row with an occurrence count and first/last seen
- [ ] **Errors:** range chips and the model / status / code facets filter; **Clear filters** resets them
- [ ] **Errors:** expanding a row shows the hint, endpoint, upstream, stack trace and recent occurrences
- [ ] **Errors:** **View in Recent requests** lands on the matching row, or toasts when it has aged out
- [ ] **Errors:** JSON and CSV export download
- [ ] Overview **Errors** tile and each errors-by-model bar deep-link into `#/errors` pre-filtered
- [ ] **Clear errors** (Overview or Errors tab) zeroes the tile, the by-model bars and the tab, with
      no phantom spike on the error-rate sparkline, and the counts stay zero after a gateway restart
- [ ] Sign out clears session; Escape closes drawer/modal

## Deferred (post-GA)

- SSE live dashboard (`GET /admin/api/events/stream`, G-12)
- Playwright E2E (G-20)

## Related

- [operator-console.md](./operator-console.md) — CLI equivalent
- [finops.md](./finops.md) — usage, forecast, webhooks
- Taiga: **US-admin-quick-model** / **#613** — admin UI + file-backed upstream secrets
