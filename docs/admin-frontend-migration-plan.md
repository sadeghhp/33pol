# Admin console frontend migration plan

Migrating `/admin` off Alpine's CSP build to a compiled, fine-grained framework — with a measured
baseline, a framework decision, and a strangler migration that keeps the realtime behaviour intact
and the CSP no looser than it is today.

| | |
|---|---|
| **Scope** | `src/33pol.App/wwwroot/admin/` |
| **Baseline commit** | `afeb6e0` (`main`) |
| **Measured** | 2026-09-13, headless Chromium 1243 via CDP against a Release build on `127.0.0.1:5080` |
| **Status** | Analysis only — no production code changed |

**How claims are marked.** `[MEASURED]` reproduced in a browser against the running gateway ·
`[VERIFIED]` read directly from repository source · `[INFERRED]` reasoned, not observed ·
`[RECOMMEND]` a judgement call.

---

## 1. Executive decision

| | |
|---|---|
| **Framework** | **SolidJS** — fine-grained signals; no component re-render, no memo discipline |
| **Fallback** | **Preact + Signals** — if React-ecosystem access or hiring pool outweighs granularity |
| **Build tool** | **Vite** — Solid's JSX needs Babel; Vite ships the official plugin. Node is build-time only |
| **Approach** | **Strangler** — Alpine keeps the shell; pages become Solid islands one at a time |

The console is not slow because of how much it does. It is slow because of *how* it invalidates. A
500 ms clock writes one reactive field, and coarse view-model getters turn that into a full rebuild
of every visible row. That single mechanism is **70% of idle CPU** `[MEASURED]`. Everything else
follows from the same shape.

### Measured baseline — Overview, idle, no interaction

| Metric | Value | Note |
|---|---:|---|
| Idle CPU | **30.6%** | 6.12 s of task time per 20 s, doing nothing |
| …from the 500 ms tick | **70%** | 4.31 s of that 6.12 s; clearing the timer alone drops it to 9.1% |
| Long tasks / 20 s | **61** | longest 282 ms; the main thread is never quiet |
| Cold transfer | **796 KB** | 17 assets, zero compressed, zero cacheable |
| Warm reload | **857 KB** | 0 responses served from cache — `no-store` on everything |
| DOM after a tab tour | **10,872** | from 1,755 at sign-in; nothing is ever unmounted |
| Live bindings | **8,227** | 1,102 of them alive *before* the operator types a key |
| CSS actually used | **37%** | 39 KB of 105 KB, after visiting every single tab |

### Top reasons Solid wins here

1. **It inverts the measured failure mode.** The problem is coarse invalidation. Solid's default is
   the opposite: a signal write updates only the DOM nodes that read it. No component re-renders, so
   there is no memoisation discipline to get wrong six months from now.
2. **It makes three existing tests unnecessary.** `AdminIndex_UsesOnlyExpressionsTheCspEvaluatorCanResolve`,
   `…BindsOnlyToNamesDeclaredOnAdminApp` and `…WrapsMultiRowLoopTemplatesInTheirOwnTbody` exist only
   because Alpine's CSP evaluator fails *silently*
   (`tests/33pol.Integration.Tests/Admin/AdminAssetSecurityTests.cs:126, 169, 212`). Compiled TSX
   turns all three classes of bug into compile errors.
3. **It lets the CSP get stricter, not looser.** Today `style-src 'unsafe-inline'` is required because
   Alpine's `x-show` calls `setAttribute("style", …)` `[VERIFIED]` in the vendored build. Solid sets
   styles through CSSOM, which CSP does not police. Combined with removing 94 literal `style=`
   attributes (mostly `colgroup` widths), `style-src 'self'` becomes reachable.
4. **~7 KB gzip runtime, no VDOM**, and per-route code splitting through Vite — against a current
   392 KB of uncompressed app + framework JS.
5. **Clean coexistence.** Independent `render()` roots mount into container elements the Alpine shell
   already provides, so pages migrate one merge at a time.

### Why the strongest alternative loses

**Svelte 5** is genuinely close — runes give comparable granularity and the smallest runtime of the
five. It loses on two repository-specific points, not on merit. Its built-in transitions inject a
`<style>` element at runtime, which works against the goal of *tightening* `style-src`; and it needs
`svelte-check` as a second type-checker beside `tsc`, where Solid's TSX is checked by `tsc` alone. For
a team with no existing JavaScript toolchain at all (`[VERIFIED]`: no `package.json` anywhere in the
repository), one type-checker is worth real money.

**React** loses on the decisive axis: its default is coarse invalidation, which is the problem being
migrated away from. Fixing it across a 25-row SSE-driven feed means `memo`/`useMemo` discipline
everywhere — recreating the current failure's shape in new clothes — plus ~45 KB gzip. **Vue 3** is
middle on granularity and needs `vue-tsc`; its SFC template checking is weaker than TSX.

**When the fallback becomes preferable:** if the team gains React experience it wants to reuse, or a
specific React-ecosystem component earns its place (a complex data grid, a date-range picker).
`preact/compat` opens that ecosystem at roughly a tenth of React's weight, and `@preact/signals`
recovers most of the granularity — but only where you remember to use it, which is exactly the
discipline risk Solid removes.

---

## 2. Current architecture

One Alpine CSP-build component, one HTML document, one stylesheet, no build step.

```
src/33pol.App/wwwroot/admin/
├── index.html          2,569 lines · 165 KB · app shell + all 7 panels + 9 drawers/modals
├── admin-app.js        7,213 lines · 323 KB · adminApp() → one object literal
│                       391 getters · 543 zero-arg methods · 1,098 functions
├── admin-store.js        272 lines · Alpine.store('admin') — fetch, retry, loading, toasts
├── admin-errors.js       112 lines · AdminErrors.classifyError — pure error taxonomy
├── admin-icons.js         76 lines · AdminIcons(name) → inline SVG strings
├── admin.css           2,104 lines · 105 KB · token system + components (37% used)
└── vendor/             alpine-csp-3.14.9.min.js (45 KB) · 9 woff2 faces (279 KB)

Served by   src/33pol.App/GatewayHostBuilderExtensions.cs:119–131
            UseDefaultFiles + UseStaticFiles; OnPrepareResponse stamps no-store + CSP on /admin/*
CSP         src/33pol.App/AdminSecurityHeaders.cs:36–46
Guarded by  tests/33pol.Integration.Tests/Admin/AdminAssetSecurityTests.cs (11 tests)
            + AdminWallboardAssetTests, Phase5/AdminUiIntegrationTests, Phase5/AdminUiSecurityTests
Built by    dotnet publish only — no Node in CI (.github/workflows/ci-reusable.yml)
            or in Docker (Dockerfile: sdk:10.0 → aspnet:10.0, two stages)
```

### Subsystem map

| Subsystem | Where | Shape |
|---|---|---|
| Entry / init | `admin-app.js:9`, `:330–367` | One `x-data="adminApp"` on `<body>`; listeners for hashchange, visibilitychange, fullscreenchange, unhandledrejection, beforeunload |
| Routing | `:407 applyHashTab`, `:728 setTab` | Hash-based, 7 tabs + sub-tabs, legacy `#/models`/`#/backends` redirects, last tab in sessionStorage, Errors + Overview carry query params |
| API transport | `admin-store.js:118–158` | `fetchWithRetry` (GET retries once, mutations zero) → `apiFetch` → `apiJson`; body read as text then parsed |
| Error taxonomy | `admin-errors.js:8` | Pure `classifyError(status, statusText, text, ctx)` → `{title, message, detail, global, section}` |
| Loading | `admin-store.js:73 withLoading` | Depth-counted scopes over 9 fixed keys; one global `loadingMessage` |
| Polling | `admin-app.js:1027 syncPoll` | 2 s master tick on every tab; sub-cadences at ×5 (health, logs, errors) and ×15 (slow Overview cards); suppressed while SSE owns the Overview |
| SSE | `:1900 openLiveStream`, `:1956 parseSseFrame`, `:1966 applyLiveFrame` | `fetch` + `ReadableStream` (not `EventSource` — the key travels in a header); exponential backoff 1 s→15 s; 45 s staleness watchdog at `:1885 checkLiveStale` |
| Timers | `:349` 500 ms tick · `:1035` 2 s poll · `:606` wallboard idle · `:3021`/`:3303` rate-limit debounces · `admin-store.js:229` 5 min watchdog | Six independent timers |
| Race guards | `_sequenced(seqKey,…)`, `_usageSeq` at `:3615` | Monotonic tokens discard stale responses — already correct, worth preserving |
| Pause / pin | `:2185 toggleRequestsPause`, `:5540 togglePinRequest` | `_pausedFrame` parks rows while summary keeps flowing; `PINNED_REQUESTS` Map snapshots pinned rows so they survive eviction |
| Wallboard | `:513–700` | Fullscreen, Screen Wake Lock with re-acquire on visibility, 8 s idle chrome hiding, 20 s staleness banding, reduced loader set (`:1795 overviewSlowLoaders`) |
| View layer | 391 getters, e.g. `:5530 requestRows`, `:6140 logRows`, `:6318 errorRows`, `:4070 mdl` | Every row rebuilt as a ~50-field object with bound closures on each evaluation; `mdl` builds ~90 `{get,set}` pairs per access |
| Icons | `admin-icons.js` + 174 live `x-html` | SVG strings assigned to `innerHTML`; Alpine does `innerHTML=o` with no dirty check `[VERIFIED]` in the minified build |

---

## 3. Verified problems

| Problem | Evidence | Runtime impact | Severity | Disposition |
|---|---|---|---|---|
| **500 ms tick drives whole-feed rebuild** | `admin-app.js:349` writes `_nowTick`; `:5504 isRecentArrival` reads it for *every* row regardless of in-flight state, so `:5530 requestRows` depends on it unconditionally | `[MEASURED]` 4.31 s of 6.12 s idle task time per 20 s. Clearing only this timer: 30.6% → 9.1% CPU | Critical | Migrate away |
| **No compression, no caching** | No `ResponseCompression` registered anywhere in `src/`; `GatewayHostBuilderExtensions.cs:126` stamps `no-store` on all of `/admin/*` | `[MEASURED]` 17/17 assets `content-encoding: none`; warm reload re-transfers 857 KB with 0 cache hits | Critical | **Fix now** |
| **DOM grows without bound** | All 7 panels are `x-show` (`index.html:167, 910, 1109, 1251, 1352, 1459, 1685`); nothing unmounts | `[MEASURED]` 1,755 → 4,176 → 10,872 nodes across a tab tour; listeners 255 → 1,220; heap 8.6 → 33.5 MB. Never released. | Critical | Migrate away |
| **Shell mounted before sign-in** | `index.html:62` `<div class="app-shell" x-show="signedIn">` | `[MEASURED]` 1,755 nodes and 1,102 bound attributes live at the auth gate | High | Migrate away |
| **Hidden detail rows stay mounted** | `index.html:872` (requests), `:1424` (logs), `:1624` (error occurrences) — `x-show="r.expanded"` | `[MEASURED]` 159 hidden detail rows mounted after visiting Logs + Errors, each carrying ~17 live bindings | High | Migrate away |
| **Getters allocate per evaluation** | `:5530 requestRows` — ~50 fields + 3 closures per row; `:4070 mdl` — ~90 `{get,set}` pairs rebuilt on *each* of 58 `x-model` reads; `:4180 sortBy` likewise | `[INFERRED]` from source; contributes to the measured 4.05 s/20 s of script time and to GC pressure | High | Migrate away |
| **Wholesale state replacement** | `:1966 applyLiveFrame` and `:2529 loadSummary` both do `this.summary = …`; every dependent getter invalidates even when one counter moved | `[MEASURED]` poll + SSE alone cost 1.62 s/20 s (8% CPU) with the tick removed | High | Migrate away |
| **Unconditional reload on tab activation** | `:1344 onTabActivated` — no freshness check; `:1770 loadSettings` fans out to 5 endpoints every visit | `[MEASURED]` 654 ms to return to Overview, 334 ms to Logs, with data seconds old | High | Migrate away |
| **SVG re-parsed via reactive innerHTML** | 174 live `x-html` on the Overview; the vendored Alpine build assigns `innerHTML=o` with no equality check | `[INFERRED]`; re-parses identical SVG on every effect re-run inside loops | High | Migrate away |
| **Dead CSS shipped** | `admin.css` 105 KB, token system at `:8–264` is sound | `[MEASURED]` 37% used (39 KB) after visiting every tab — ~66 KB never applies | Medium | Delete per page |
| **Undebounced client filters** | `index.html:1124, 1205, 1267` — `modelsFilter`, `backendsFilter`, `keysTextFilter` have no `.debounce`, unlike logs/errors search | `[INFERRED]`; re-filters + re-sorts + rebuilds rows per keystroke. Not measured — the dev gateway had too few keys. | Medium | Migrate away |
| **Unbounded tables** | Keys and Usage rollups render whatever the server returns; `logsPageSize: 200` at `:196` | `[INFERRED]`; worst case scales with tenant and model count | High | Server paging |
| **No focus management in drawers** | 9 dialogs have `role="dialog" aria-modal="true"` and Escape (`:2241`), but only `:2216 openConfirm` moves focus; background stays in tab order because it is `x-show`, not unmounted | `[VERIFIED]`; Tab walks out of an open drawer into the live page behind it | High | Primitives |
| **Monolithic source** | `index.html` 2,569 lines; `admin-app.js` 7,213 lines, 391 getters; rate limits alone spans `:2569–3500` and `:6408–7110` with 84 `rl*` members | `[MEASURED]` 62% of 1,098 functions execute in a full session — nothing can be lazily loaded | High | Migrate away |

### One earlier hypothesis needs correcting

"Most tabs remain mounted with `x-show`" is true, but the cost is *not* where it sounds. `[MEASURED]`
per-panel weight with Overview active: `panel-logs` 67 nodes, `panel-keys` 82, `panel-routing` 128 —
inactive panels are cheap *while empty*, because their tables hold no rows until the tab is first
opened. The real cost appears *after* a tab is visited and its rows stay mounted for the rest of the
session. That changes the fix: the win is unmounting *visited* pages, not trimming unvisited markup.

### Measurement method — so this is reproducible

Release build of `src/33pol.App` on `127.0.0.1:5080` with a SQLite store, 60 seeded inference requests
producing 25 feed rows / 130 log rows / 4 error groups. Headless Chromium 1243 via Playwright; CPU from
`Performance.getMetrics` deltas (`TaskDuration`, `ScriptDuration`, `LayoutDuration`) over fixed 20 s
windows; coverage from `page.coverage`; DOM and binding counts by walking
`document.querySelectorAll('*')` attributes. Tick attribution by clearing `_tickTimer` through
`Alpine.$data(document.body)` and re-sampling the same window. Numbers come from a dev-scale dataset;
a busy production gateway will be worse, not better.

---

## 4. Constraints

| Constraint | Classification | Evidence | Consequence for the plan |
|---|---|---|---|
| **`script-src 'self'`, no `unsafe-eval`** | Hard | `AdminSecurityHeaders.cs:36–45`; asserted at `AdminAssetSecurityTests.cs:108–116` | Every framework must ship precompiled templates. All five candidates qualify; runtime template compilation (Vue full build) does not. |
| **No runtime CDN; all assets self-hosted** | Hard | `AdminAssetSecurityTests.cs:22 ReferencesNoExternalScriptsOrStylesheets`, `:39 DoesNotPreconnectToThirdPartyOrigins`, `:52 VendoredAssets_AreServedLocally` | Build output and fonts ship inside the image. npm is a build-time dependency only, never a runtime one. |
| **Air-gapped / on-prem operation** | Hard | `docs/admin-ui.md`; `Dockerfile` produces a self-contained image | No telemetry, no font CDN, no dynamic import from a remote origin. |
| **ASP.NET serves static assets** | Intentional | `GatewayHostBuilderExtensions.cs:119–131` | Keep. No Node runtime in production, no separate frontend host, no SSR. |
| **Build artifacts not committed** | Intentional | `.gitignore`: `**/bin/**`, `**/obj/**`, `perf/*` | Precedent settles the artifact question: the frontend bundle is **CI/build-produced**, never committed. |
| **`style-src 'unsafe-inline'`** | Removable | `AdminSecurityHeaders.cs:24–33` explains it; Alpine's `x-show` calls `setAttribute("style", …)` `[VERIFIED]`; plus 94 literal `style=` attributes, 71 of them `colgroup` widths | Becomes removable once Alpine is gone and the width attributes move to CSS. A security *improvement*, not a regression. |
| **Alpine CSP expression restriction** | Legacy | `docs/admin-ui.md`; enforced by three tests at `AdminAssetSecurityTests.cs:126, 169, 212` | Disappears with Alpine. Do **not** reproduce the `mdl` / row-view-model pattern in the new framework. |
| **`?v=N` cache busting** | Accidental | `AdminAssetSecurityTests.cs:284–303` — regex accepts `?v=\d+` or a version in the filename | Replace with content hashes. **The existing test will fail on hashed names** (`app-a1b2c3d4.js` matches neither branch) — it must be rewritten in the same commit. |
| **`no-store` on all of /admin** | Accidental | `GatewayHostBuilderExtensions.cs:126`; `docs/admin-ui.md` documents it as intended | Correct for `index.html`, wrong for hashed assets and fonts. Split the policy. |
| **No Node in CI or Docker** | Removable | `.github/workflows/ci-reusable.yml` (setup-dotnet only); `Dockerfile` (sdk:10.0 → aspnet:10.0) | Adding a pinned Node stage is the single biggest process change. Symmetric with the existing NuGet dependency — justified, not free. |
| **.NET 10 / `dotnet publish` deployment** | Hard | `release.yml:56–66` tarball; `Dockerfile:30` | The frontend build must slot in *before* publish and leave `wwwroot/admin` populated. |
| **Browser targets** | Removable | Uses `ReadableStream`, `AbortController`, Screen Wake Lock, `:has()`-free CSS | Already effectively evergreen-only. Set an explicit `browserslist` to lock it in. |

---

## 5. Framework decision matrix

Weights derived from this repository's measured problems, not from general preference. Scores 1–5.
Reactivity granularity carries the most weight because it is where 100% of the measured idle cost
originates.

| Criterion | Weight | **Solid** | Preact+Sig | Svelte 5 | React 19 | Vue 3 |
|---|---:|---:|---:|---:|---:|---:|
| **Reactivity granularity** — the measured failure mode | 20 | **5** | 3.5 | 4.5 | 2.5 | 3.5 |
| **CSP fit incl. tightening style-src** | 15 | **5** | 5 | 3.5 | 5 | 4 |
| **TypeScript + template typechecking** — replaces 3 Alpine guard tests | 12 | **5** | 5 | 4 | 5 | 3.5 |
| **Runtime size** | 10 | **4.5** | 4.5 | 5 | 2 | 3 |
| **Coexistence with Alpine** | 10 | **5** | 5 | 4.5 | 4.5 | 4 |
| **Live feed / large table fit** | 10 | **5** | 3.5 | 4.5 | 3 | 3.5 |
| **Build & dependency surface** | 8 | **4** | 5 | 4 | 4 | 4 |
| **Testing ecosystem** | 6 | **4** | 5 | 4 | 5 | 4.5 |
| **Accessibility primitives** | 5 | **3.5** | 5 | 4 | 5 | 4 |
| **Team learning cost** — no existing JS toolchain | 4 | **3.5** | 4.5 | 3 | 5 | 4 |
| **Weighted score / 100** | 100 | **93.5** | **89.6** | **83.7** | **77.4** | **74.4** |

The gap between Solid (93.5) and Preact (89.6) is narrow and sits almost entirely in one row:
granularity. That is deliberate — it reflects that Preact *can* reach the same place with signals, but
only where a developer chooses to. Solid gets there by construction. If that discipline argument ever
stops being persuasive for this team, the matrix says switch to Preact rather than to anything else.

**Candidates considered and dropped before scoring.** Lit / Web Components: no repository evidence of
a component-sharing need, and its templating needs its own typechecking story. htmx: the console is a
stateful realtime dashboard, not a document-navigation app — server-rendered fragment swaps do not fit
a 25-row SSE feed. Stock Alpine (non-CSP build): requires `unsafe-eval`. Rejected on a hard constraint.

---

## 6. Target architecture

```
src/33pol.Admin.Web/              new — frontend source, reviewed and versioned
├── package.json · package-lock.json · tsconfig.json · vite.config.ts
├── index.html                    entry template; Vite injects hashed asset tags
├── legacy/                       Alpine files, copied verbatim to output during coexistence
└── src/
    ├── app/         shell, router, theme, error boundary, mount points
    ├── api/         transport only: fetch client, auth header, classifyError, typed endpoints
    ├── domain/      pure functions: sort, filter, format, freshness, severity — zero framework imports
    ├── realtime/    SSE reader, poll scheduler, connection state machine
    ├── stores/      one store per resource: signals + keyed entity maps
    ├── pages/       overview · logs · errors · keys · usage · routing · settings · ratelimits (lazy)
    ├── components/  primitives/ (Dialog, Drawer, Tabs, …) + shared widgets + icons/
    ├── styles/      tokens.css (ported) + per-component css
    ├── types/       API responses, SSE frames, domain entities
    └── utils/

Build output → src/33pol.App/wwwroot/admin/   (gitignored from M2 onward)
```

### Dependency direction

```
pages  ──▶  stores  ──▶  api ──▶ types
   │           │       └──▶ domain
   │           └──▶  realtime ──▶ api
   └──▶  components  ──▶  domain
```

Rules enforced by an eslint import boundary (or a tiny CI script):

- `domain/` imports nothing from `solid-js`, `api/`, `stores/` or `components/`
- `api/` and `realtime/` never import Solid primitives that render
- nothing imports from `pages/` — pages are leaves
- `components/primitives/` never import `stores/`

### Disposition of every significant existing abstraction

| Abstraction | Action | Rationale |
|---|---|---|
| `AdminErrors.classifyError` | **Preserve** | Pure, complete, and encodes real product decisions (409 lifecycle codes become informative outcomes, not faults). Port to TS unchanged → `api/errors.ts`. |
| `_sequenced` / `_usageSeq` race guards | **Extract** | Correct stale-response protection. Generalise into the resource layer so every resource gets it for free instead of five hand-rolled copies. |
| `fetchWithRetry` / `apiFetch` / `apiJson` | **Extract** | Keep the semantics (GET retries once, mutations never; 401 flips connection state). Add jitter and 429/503 `Retry-After` handling → `api/client.ts`. |
| SSE reader + frame parser + staleness watchdog | **Extract** | The trickiest correct code in the repo. Lift into `realtime/` essentially as-is, then put it under unit test *before* any UI depends on it. |
| Pause / `_pausedFrame`, `PINNED_REQUESTS`, `SEEN_REQUEST_IDS` | **Preserve semantics, rewrite impl.** | The behaviour is right; the implementation is three module-level Maps kept deliberately outside reactivity to avoid render loops (`admin-app.js:57–62`). A keyed store removes the need for that workaround entirely. |
| Hash router, legacy redirects, sessionStorage last tab | **Preserve** | Deep links are documented and used by the Overview tiles. Extract to `app/router.ts` with the same URL contract. |
| Wallboard: fullscreen, wake lock, idle chrome, staleness banding | **Preserve** | Genuinely subtle (wake locks are dropped on hide and never restored automatically). Extract to `pages/overview/useWallboard.ts`. |
| `admin.css` token block (`:8–264`) | **Preserve** | A real design system — dark-first, full light theme, coherent scale. Port wholesale to `styles/tokens.css`. |
| `sortedList` | **Rewrite** | Parses dates inside the comparator. Decorate–sort–undecorate in `domain/sort.ts`. |
| `withLoading` depth-counted scopes | **Rewrite** | Nine global scopes conflate "first load" with "background refresh". Replaced by a per-resource lifecycle (§8). |
| `mdl`, `sortBy`, and all `get *Rows()` builders | **Delete** | Pure Alpine-CSP workarounds. Replaced by ordinary props and keyed components. Reproducing them would import the performance problem. |
| `AdminIcons` + `x-html` | **Delete** | Replaced by compiled SVG components (see *CSS and icons*). |
| Three Alpine-shaped guard tests | **Delete** | They exist because the CSP evaluator fails silently. `tsc` replaces all three. |

---

## 7. State and reactivity design

The organising rule: **one store per resource, entities keyed by id, and a clock that only
time-dependent components subscribe to.** No single global store.

```
stores/summary.ts
  summary: Store<Summary | null>         setSummary(reconcile(frame.summary, {key:'window'}))
  updatedAt: Signal<number>              → path-level diff: one counter notifies one text node
  vitalsHistory: Signal<Sample[]>        ring buffer, capped 60 (as today)

stores/requests.ts                       bounded: server returns ≤25 (12 in wallboard)
  byId: Store<Record<RequestId, RequestRow>>   reconcile by requestId — a changed row touches only its cells
  order: Signal<RequestId[]>                   sort/filter output; pure fn in domain/requests.ts
  pinnedIds: Signal<RequestId[]>               pinned snapshots live in byId, so eviction cannot lose them
  paused: Signal<boolean>   parkedFrame: Signal<RequestRow[] | null>
  expandedId: Signal<RequestId | null>         exactly one detail row is ever mounted

stores/logs.ts · errors.ts · keys.ts     same shape: { byId, order, total, page }
  errors adds: occurrencesByFingerprint: Store<Record<string, Occurrence[]>>   lazy, on expand

stores/usage.ts                          query-snapshot shaped, not entity-shaped
  query: Signal<UsageQuery>   result: Resource<UsageReport>   seq guards stale responses

stores/routing.ts     models / backends — config scale, plain arrays + derived filter
stores/settings.ts    configStatus · cors · grants · rateLimits{ server, schedule, preview }
                      rlDraft is LOCAL component state until Save — never global

stores/connection.ts
  status: 'unknown'|'ok'|'degraded'|'fail'     mode: 'off'|'polling'|'stream'|'reconnecting'
  lastFrameAt · lastVersion · retryDelayMs

app/clock.ts
  nowMs: Signal<number>   1 s tick, started ONLY while ≥1 subscriber exists (createRoot refcount)
```

### How each stated requirement is met

| Requirement | Mechanism |
|---|---|
| Changing one counter updates only its consumers | `reconcile()` diffs the incoming summary by path; Solid notifies only the leaf signals that actually changed value. |
| Changing one request updates only that row | `byId` is a keyed store; `<For each={order()}>` keeps row components alive across frames. A row's cells read `byId[id].statusCode` directly — no intermediate row view-model to regenerate. |
| Inactive pages cost nothing | Routes are lazy (`lazy(() => import('./pages/logs'))`) and unmounted on leave. Their DOM, effects and listeners are disposed — the fix for the measured 10,872-node growth. |
| Collapsed details are not mounted | `<Show when={expandedId() === id}>`. One detail panel exists at a time instead of the measured 159. |
| Static icons are never re-parsed | Icons compile to JSX element factories. No string, no `innerHTML`, no parse. |
| Timers update only time-dependent UI | Two rules: (a) `nowMs` is read *only* by the in-flight duration cell and the "updated Ns ago" label; (b) the arrival flash becomes a CSS animation triggered on mount, removing its clock dependency entirely. This is the direct fix for the measured 70%. |
| Cached tab data survives navigation | Stores live above the router, so unmounting a page disposes its DOM but not its data. |
| Background refresh never blanks usable UI | `refreshing` is a distinct state from `loading`; only `loading` renders a skeleton. |
| Large arrays are not recreated | `order` holds ids, not objects. Re-sorting produces a new id array; the row objects and their DOM are untouched. |
| Sorting/filtering is bounded | Every list is server-bounded to ≤100 rows (see *Table strategy*), and comparators use precomputed sort keys. |

---

## 8. Data and realtime design

### Resource lifecycle

```
idle ──fetch()──▶ loading ──ok──▶ ready ──age > freshMs──▶ stale
                     │              │                        │
                     └──err──▶ error   └──refetch()──▶ refreshing ◀──┘
                                 │              │
                                 └──retry()─────┘   data stays on screen throughout
```

- **freshMs per resource:** summary 2 s (live) · requests 2 s · logs 10 s · errors 10 s · keys 30 s ·
  routing 30 s · usage 60 s · settings 60 s
- **Tab activation:** `ready && age < freshMs` → render cached, fetch nothing. Otherwise → render
  cached, enter `refreshing`.
- **Manual refresh:** always `refreshing`, never `loading`, so the table never blanks.
- **Cancellation:** one `AbortController` per resource; leaving a page aborts its in-flight fetch.
- **Deduplication:** a second fetch while one is in flight returns the same promise.
- **Stale protection:** monotonic seq per resource; a response whose seq ≠ current is discarded.

`[RECOMMEND]` **No data-fetching library.** TanStack Query is ~13 KB gzip and brings cache keys,
garbage collection and window-focus refetching that nine fixed resources do not need. The lifecycle
above is roughly 120 lines wrapping Solid's own `createResource`, and it already exists in spirit at
`admin-store.js:73` and `admin-app.js:_sequenced` — this is extraction, not invention.

### Realtime: the machine as it exists today

`[VERIFIED]` by reading `syncLive:1876`, `openLiveStream:1900`, `applyLiveFrame:1966`,
`checkLiveStale:1885`, `syncPoll:1027` and `verifyConnection` in `admin-store.js`.

```
off ─── apiKey set ──▶ decide()
    decide(): want stream ⟺ apiKey ∧ tab==='dashboard' ∧ !document.hidden ∧ status≠'fail'

polling   2 s master tick, ALL tabs. Sub-cadences ×5 (health, logs, errors) and ×15 (slow cards).
          Skips summary+requests while mode==='stream' ∧ tab==='dashboard'.
          Returns early when status==='fail' — a rejected key is never retried on this path.

stream    fetch(/admin/api/live?limit=25) + ReadableStream, key in X-API-Key header
          frame: "event: update" + one JSON line;  ":" lines are 15 s heartbeats
          applyLiveFrame → summary, requests, summaryUpdatedAt, _nowTick, recordVitals
          paused ⇒ requests parked in _pausedFrame; summary keeps flowing

reconnecting  on stream error: mode = (gotFrame ∨ _liveFrames>0) ? 'reconnecting' : 'polling'
              backoff 1 s ×2 → cap 15 s, reset to 1 s on the first good frame
stale         no bytes for 45 s (3 × heartbeat) → abort, 'reconnecting', reconnect immediately
fail          only a 401 sets it. Halts poll AND stream. Cleared by the 5-min watchdog,
              a window focus event, or an operator changing the key.
```

### Target machine

Same states, same thresholds, same user-visible semantics — but with one owner and two defects closed.

```
off ⇄ polling ⇄ connecting ─▶ stream ─▶ reconnecting ─▶ (stream | polling)
                         └──▶ fail (401 only) ──▶ off
```

**New invariant — single writer.** `connection.source: 'poll' | 'stream'`. Only the current source may
write summary/requests. Today this is an implicit condition inside the poll callback; making it a field
means poll and stream **cannot** both write during the reconnect overlap window.

**New invariant — frame ordering.** `[RECOMMEND]` The server already sends `frame.version` and the
client already stores it (`liveVersion`) but never compares it. Target: drop any frame whose version
≤ `lastAppliedVersion`. Closes duplicate delivery and out-of-order application on reconnect.

**Preserved exactly:** the 2 s / 10 s / 30 s cadences and the ×5/×15 tick arithmetic; the 45 s
staleness budget, 1 s→15 s backoff, reset-on-good-frame; 401 ⇒ fail, everything else ⇒ degraded and
keep polling; pause parks rows, never the summary, with `pausedPendingCount` = parked − shown; pinned
rows survive eviction via snapshot; stream runs only on Overview, only while visible; the wallboard
reduces the slow-loader set to policy only.

### Regression surface — the part that must not break

These behaviours are correct today, subtle, and easy to lose silently:

- **Pause keeps vitals live while parking rows.**
- **Pinned rows survive eviction** from the 25-row window.
- **A first-frame-less stream degrades to "Polling"** rather than promising a reconnect.
- **The wake lock is re-acquired on visibility change** — browsers drop it silently.
- **The wallboard loads only the policy card**, so a screen left up for weeks does not hammer the database.
- **A 401 stops polling**, so a stale tab cannot fill the admin audit trail with one rejected request every 2 s.

Each gets a named test in M4 before any UI depends on it.

---

## 9. Quick wins

### A. Do now

Every item here is server-side or configuration. **Zero throwaway work** — none of it touches the
Alpine app.

- **Split the cache policy.** `no-store` for `index.html`; `max-age=31536000, immutable` for hashed
  assets and fonts. Recovers the measured 857 KB warm reload.
- **Register response compression** (Brotli + Gzip) for static files, excluding `text/event-stream`.
  ~664 KB of text → ~158 KB.
- **Preload the two body-critical faces** (IBM Plex Sans 400/500). Fonts are discovered only after
  `fonts.css` parses today.
- **Retry with backoff + jitter** and honour `Retry-After` on 429/503 in `fetchWithRetry`. Logic ports
  to TS verbatim in M3.
- **Logs default page size 200 → 50.** Server-side parameter; survives migration.
- **Commit the perf harness** so every later claim is checkable.

### B. Only if migration slips past one quarter

Real wins, but all five are deleted by M11–M18. Spend this only to buy time, and expect to throw it away.

- **Narrow the 500 ms tick.** Make `isRecentArrival` not read `_nowTick` for settled rows. Recovers
  ~70% of idle CPU — but it is Alpine surgery that gets deleted.
- **`x-if` for collapsed detail rows** in requests, logs and errors.
- **Hoist `mdl` and `sortBy`** out of getters into `init()`.
- **Debounce** `modelsFilter`, `backendsFilter`, `keysTextFilter`.
- **Freshness check in `onTabActivated`** so tab switches stop refetching.

### C. Do not fix

- **The `mdl` / `sortBy` / row-view-model pattern.** Deleted wholesale.
- **The `x-html` icon pipeline.** Replaced by compiled components.
- **The non-reactive `Map` workarounds** (`SEEN_REQUEST_IDS`, `PINNED_REQUESTS`). They exist to dodge
  Alpine render loops.
- **The three Alpine-shaped guard tests.** Replaced by the compiler.
- **The 63% dead CSS.** Delete it per page as each page migrates — a single sweep now is a high-risk
  change with no test to catch a missed selector.
- **`style-src 'unsafe-inline'`.** Cannot be removed while Alpine sets style attributes. Becomes free
  at M16.
- **Bundle splitting of `admin-app.js`.** Without a build step it means hand-written module boundaries
  that the migration immediately discards.

---

## 10. Migration roadmap

Every milestone is independently mergeable and independently revertible. Rollback for M5 onward is the
same shape: the page's Solid island is removed and its Alpine markup restored from the previous commit
— which is why each page migration is exactly one commit.

| M | Changes | Depends | Acceptance criteria | Rollback |
|---|---|---|---|---|
| **M0** Baseline | Commit the Playwright perf harness to `perf/frontend/`; record the §12 numbers as the tracked baseline | — | Harness runs against a local gateway and emits JSON; numbers reproduce within ±15% | Delete directory |
| **M1** Asset delivery | Split cache policy; response compression; font preload; retry/backoff+jitter; logs page size 50 | — | Warm reload ≤ 20 KB; all text assets `content-encoding: br`; existing 11 asset-security tests still green; new caching test green | Revert commit — server-side only, no client change |
| **M2** Build system | Create `src/33pol.Admin.Web/`; Vite + TS + `vite-plugin-solid`; Alpine files move to `legacy/` and are copied to output; MSBuild target; CI job; Docker Node stage; gitignore the output dir | M1 | **Byte-identical admin console** in the browser; `dotnet publish` produces a working `wwwroot/admin`; Docker image builds; all admin integration tests green | Revert; restore `wwwroot/admin` from git; drop the CI job |
| **M3** TS foundation | `types/`, `api/`, `domain/`; port `classifyError`; port sort/filter/format as pure functions. **Nothing wired to the UI.** | M2 | `tsc --noEmit` clean; Vitest unit suite ≥ 90% on `domain/`; ported `classifyError` matches the original on a table of ~20 status/body cases | Revert — dead code until M5 |
| **M4** Realtime layer | `realtime/`: SSE reader, poll scheduler, connection machine. Headless, not rendered. | M3 | Every bullet in the §8 regression surface has a passing test; fake `ReadableStream` drives connect → interrupt → backoff → poll fallback → recover → poll shutdown; duplicate and out-of-order frames rejected | Revert — still unused |
| **M5** Primitives | `components/primitives/`: Dialog, Drawer, Tabs, DisclosureRow, Menu, Field, Alert, Toast, Button, Select. Icons compiled. Tokens ported. | M3 | Keyboard suite passes per primitive (trap, initial focus, restore, Escape, `inert` background); axe clean; visual parity against the Alpine equivalents | Revert — unused until M6 |
| **M6** Logs page | First Solid island. Alpine `#panel-logs` markup deleted; a mount container replaces it. Boundary test added. | M4, M5 | Feature parity incl. level filter, search, auto-refresh, detail expand, copy, clear buffer. Logs DOM ≤ 400 nodes at 50 rows. No Alpine directive inside `[data-solid-root]`. | Single revert restores the Alpine panel |
| **M7** Overview page | The headline win. Vitals, sparklines, attention, glance grid, slow cards, live tail, pause, pin, wallboard. | M6 | **Idle CPU ≤ 3%** (from 30.6%); zero long tasks > 50 ms over 20 s idle; every §8 regression-surface behaviour verified by E2E; wallboard wake lock and staleness banding intact | Single revert |
| **M8** Errors page | Grouped failures, facets, range chips, occurrence drill-down, export, clear | M6 | Parity incl. deep links from Overview tiles (`#/errors?model=…&status=…`); occurrences still lazy-loaded on expand | Single revert |
| **M9** Keys page | First write-path page: create, edit, revoke, archive/restore, permanent delete, per-key model access. **Server-side paging + filtering added.** | M8 | All 409 lifecycle outcomes render as informative messages, not faults; confirm dialogs keyboard-complete; DOM bounded at 50 rows regardless of key count | Revert page; the API paging parameter is additive and can stay |
| **M10** Routing | Models + Backends sub-tabs, quick-add drawer, model test dialog, health table | M9 | Parity; legacy `#/models` / `#/backends` redirects still work | Single revert |
| **M11** Usage | Date presets, filters, rollups, events with load-more, forecast, CSV export | M10 | Race guard holds under three rapid preset changes; export filename honours `Content-Disposition`; rollups server-paged | Single revert |
| **M12** Settings | Runtime status, CORS, model access, observability — rate limits excluded | M11 | Parity; mutations round-trip; no fan-out reload on revisit | Single revert |
| **M13** Rate limits | Largest feature: tiers, scoped rules, scheduled windows, timeline, calendar, preview, new-rule flow, dirty tracking | M12 | Parity against the existing rate-limit integration tests; draft/dirty semantics preserved; timeline and preview debounces preserved; **lazy chunk ≤ 40 KB gzip** | Single revert |
| **M14** Shell swap | Solid takes over shell, router, topbar, auth gate, toasts, global alert. **Alpine deleted.** `legacy/` removed. | M13 | Auth gate mounts ≤ 150 DOM nodes; full E2E suite green; the three Alpine guard tests removed in the same commit | Revert the commit — the last point where the old console still exists in git history |
| **M15** CSS sweep | Delete rules orphaned by M6–M14; move the 94 inline `style=` attributes into classes | M14 | CSS coverage ≥ 85% after a full tab tour; no visual regression on the reference screenshots | Revert |
| **M16** CSP tightening | Drop `'unsafe-inline'` from `style-src` | M15 | Console clean of CSP violations across every page, drawer and dialog; `AdminAssetSecurityTests` updated to assert the stricter policy | Revert the header change alone |
| **M17** Final budgets | Route-level code splitting review; preload hints; budget gates made blocking in CI | M16 | Every §12 budget met and enforced | Relax the gate, not the code |

---

## 11. Page migration order

This deviates from the conventional sequence in two places, both on evidence.

| # | Page | Weight | Why here |
|---|---|---|---|
| 1 | **Logs** | 67 nodes, 26 bindings | **Deviation.** The obvious order puts Overview first. Logs is the smallest panel in the app and still exercises the entire stack end to end — resource lifecycle, polling cadence, server pagination, a filter, a detail disclosure, a table and three primitives. It is the cheapest possible proof that the foundation works, and if the foundation is wrong, this is where you want to find out. |
| 2 | **Overview** | 2,885 nodes, 1,981 bindings | **Deviation.** Normally you would leave the riskiest page for last. Here it goes second, because **100% of the measured idle CPU lives on it** — leaving it until M13 means the headline number does not move for most of the project. The risk is bought down by M4: the realtime layer is fully unit-tested before any pixel depends on it. |
| 3 | **Errors** | 120 nodes, 79 bindings | Structurally a richer Logs — server-paged, grouped, with nested lazy occurrences. Reuses M6 wholesale and adds one new pattern (nested disclosure). |
| 4 | **Keys** | 82 nodes, 32 bindings | First write-path page. Introduces mutations, optimistic-vs-confirmed updates and the 409 lifecycle taxonomy. Small surface, high learning value — and it is the table most likely to grow unbounded in production, so it carries the server-paging work. |
| 5 | **Routing** | 128 nodes, 39 bindings | Small, adds the drawer and the model-test dialog. Natural follow-on from Keys' mutation patterns. |
| 6 | **Usage** | 214 nodes, 110 bindings | Needs the stale-response guard under genuine pressure (three concurrent report queries) and the blob-download path. Deferred until the resource layer has proven itself on four simpler pages. |
| 7 | **Settings** | 94 nodes, 58 bindings | Mostly forms. Depends on the `Field` primitive being mature, which it will be by now. |
| 8 | **Rate limits** | ~1,600 lines, 84 `rl*` members | Last. The single largest feature in the console — roughly 22% of `admin-app.js`, with its own drawers, timeline, calendar, preview, dirty tracking and multi-step flow. Every primitive and pattern it needs should already exist and be battle-tested. It is also the best code-splitting candidate: a lazily-loaded chunk most operators never fetch. |

---

## Table strategy

Decided per table. `[RECOMMEND]` **virtualization for none of them** — every table can be
server-bounded to ≤100 rows, and virtualization costs screen-reader row semantics, browser
find-in-page and sticky-header complexity for no benefit at that size.

| Table | Today | Target | Bounded worst case | Note |
|---|---|---|---|---|
| **Requests feed** | 25 (12 wallboard), server-capped | Simple client rendering, keyed | 25 rows ≈ 330 nodes + 1 detail panel | Already correct. The fix is mounting one detail panel instead of 25. |
| **Logs** | 200 | Server pagination at 50; level + search already server-side | 50 rows ≈ 300 nodes | Page size drops in M1 — measured 130 rows produced 5,460 extra nodes. |
| **Errors** | 50 + facets | Keep. Occurrences stay lazy per group. | 50 rows + 1 group's occurrences | Already the best-behaved table in the app. |
| **Keys** | Unbounded | **Server pagination + filtering + sorting**, page 50 | 50 rows ≈ 450 nodes | Needs a new API parameter — backend work, flagged in M9. Scales with tenant count. |
| **Usage rollups** | Unbounded | Server pagination, page 100; CSV export for bulk | 100 rows ≈ 800 nodes | Worst case is days × models × cost centres — the most explosive table in the app. |
| **Usage events** | Incremental load-more | Keep; cap mounted rows at 200 with an explicit "load more" | 200 rows | `loadMoreUsageEvents` already exists. |
| **Models / Backends** | Unbounded, client filter | Client rendering + debounced filter | Registry scale, ~10² | Config-scale. Server paging would be over-engineering. |
| **Rate-limit tables** | Config scale | Client rendering | Tens of rows | Bounded by configuration, not traffic. |

---

## Accessibility

The audit finding is narrow and specific: the semantics are largely right, the *behaviour* is missing.

`[VERIFIED]` All 9 dialogs and drawers carry `role="dialog"` and `aria-modal="true"`, and Escape is
handled centrally at `admin-app.js:2241` with a correct close-precedence chain. Toasts, the global
alert and the wallboard staleness band all have appropriate `aria-live` values. Four
`prefers-reduced-motion` blocks already exist in the stylesheet.

What is missing: **focus is managed only for confirm dialogs** (`:2216 openConfirm`). The other eight
surfaces never move focus in, never restore it out, and — because they are `x-show` rather than
unmounted — leave the entire page behind them in the tab order and in the accessibility tree. A
keyboard user opening the model drawer can Tab straight out of it into the live table underneath.

### Primitives — behaviour implemented once

| Primitive | Owns | Replaces |
|---|---|---|
| `<Dialog>` | Focus trap, initial focus, focus restore, Escape, `inert` on the shell, scroll lock, labelled by its own heading | 4 confirm/test dialogs |
| `<Drawer>` | Same contract as Dialog plus edge placement and a close affordance in the tab order | 5 drawers |
| `<Tabs>` | Roving tabindex, Home/End/arrow keys, `aria-controls`, and **unmounting** the inactive panel | Main nav + routing + settings sub-tabs |
| `<DisclosureRow>` | A real `<button>` in the first cell carrying `aria-expanded` and `aria-controls`, mounting the detail row only when open | The current `<tr role="button" tabindex="0">` pattern in requests, logs and errors — which announces a whole table row as a button and swallows text selection |
| `<Field>` | Label association, `aria-describedby` for hint and error, `aria-invalid` | Ad-hoc form markup across Settings, Keys, Routing |
| `<Alert>` `<Toast>` | Correct live-region politeness; **no scroll-into-view on transient errors** | `admin-store.js:56 scrollToAlert`, which yanks the viewport on recoverable failures |
| `<Menu>` `<Select>` | Native `<select>` wherever it suffices; typeahead and arrow keys only where it does not | Filter controls |

---

## CSS and icons

`[MEASURED]` 37% of `admin.css` is used after visiting every tab — about 66 KB of the 105 KB never
matches anything. `[VERIFIED]` the token block (`admin.css:8–264`) is a genuine design system: a full
type scale, dark-first with a complete light theme, semantic colour separated from accent. **That part
is an asset and should be ported unchanged.**

`[RECOMMEND]` **plain CSS with the existing tokens, plus per-component files colocated with
components.** No CSS framework: the repo already has a working token system and a distinctive visual
identity, and Tailwind or similar would mean re-deriving it and inflating the diff on every page
migration. No CSS Modules initially either — Vite's per-component imports plus the existing BEM-ish
naming are sufficient; revisit only if a genuine collision appears. No PostCSS: custom properties and
nesting are natively supported in the target browsers.

Selective techniques worth adopting, in order of value: `contain: layout style` on table rows and
cards (cheap, compounds with the binding reductions); `content-visibility: auto` on off-screen cards,
applied carefully around the sticky headers; and keeping `font-display: swap` as-is. `--font-display`
(Space Grotesk) appears in only 5 rules across the stylesheet while shipping 3 weights at 66 KB —
`[RECOMMEND]` auditing whether all three are needed, but **verify before deleting**; Chrome's CSS
coverage does not report `@font-face` usage, so the measured 0% for `fonts.css` is a tool artifact,
not evidence.

### Icons

`[RECOMMEND]` **compiled SVG components.** Each icon becomes a small TSX function returning real
elements. Compared with the alternatives: a `<symbol>/<use>` sprite means one more request and a
runtime indirection for no gain once icons are tree-shaken per route; static inline SVG in templates is
correct but duplicates markup and loses the single point of change that `AdminIcons.map` provides
today. Compiled components keep that single source, tree-shake to only the icons a route uses, are
type-checked, and — the point — **never touch `innerHTML`**, which removes 174 live parse sites from
the Overview alone.

---

## TypeScript staging

Adopt from the boundaries inward. Each stage is useful on its own.

1. **API response types** (M3). `[RECOMMEND]` evaluating generation from the OpenAPI document —
   `app.MapOpenApi()` already exists at `GatewayHostBuilderExtensions.cs:139`, though it is
   Development-only and the admin endpoints may not be fully described. If the generated types are
   thin, hand-write them; do not let generation tooling become a blocker.
2. **Realtime frame types** (M4), including `version`, which the new ordering invariant depends on.
3. **Domain entities** (M3): `RequestRow`, `LogEntry`, `ErrorGroup`, `ApiKey`, `ModelRoute`.
4. **Settings schemas** (M12–M13). The rate-limit draft/schedule/window model is the most complex
   shape in the app and the one most likely to drift from the server.
5. **Store state** (M5 onward) — largely inferred, rarely annotated.
6. **Component props** — naturally, as components are written.
7. **Mutations** last: request and response bodies for every write path.

### Where types are not enough

Runtime validation is warranted in exactly three places, because all three cross a trust or version
boundary: **SSE frames** (a gateway mid-deploy can send an older or newer shape, and the ordering
invariant depends on `version` being a number); **rate-limit settings on load** (a malformed draft
silently corrupts a save); and **state restored from localStorage** (the API key and the persisted
Overview window). `[RECOMMEND]` a hand-written validator for these three, or `valibot` (~1.5 KB) if the
shapes grow — explicitly not `zod` (~13 KB), which would consume a fifth of the initial JS budget for
three call sites.

---

## 12. Performance plan

| Metric | Measured baseline | Target | Measurement |
|---|---:|---:|---|
| **Idle Overview CPU** (20 s) | 30.6% · 6.12 s | ≤ 3% · 0.6 s | CDP `TaskDuration` delta |
| **Long tasks** (20 s idle) | 61 · max 282 ms | 0 > 50 ms | `PerformanceObserver('longtask')` |
| **Cold transfer** | 796 KB | ≤ 250 KB | Sum of response `content-length` |
| **Warm transfer** | 857 KB · 0 cached | ≤ 20 KB | Second navigation, same context |
| **Initial JS** (gzip) | ≈95 KB equivalent (392 KB uncompressed, uncached) | ≤ 60 KB | Build output + gzip |
| **Initial CSS** (gzip) | ≈18 KB · 37% used | ≤ 15 KB · ≥ 85% used | Build output + `page.coverage` |
| **Lazy chunk** (largest) | n/a — no splitting | ≤ 40 KB gzip | Rollup output |
| **DOM, Overview** | 4,176 | ≤ 1,500 | `querySelectorAll('*').length` |
| **DOM, after full tab tour** | 10,872 · monotonic | ≤ 2,000 · non-monotonic | Same, after visiting all 7 tabs |
| **DOM, auth gate** | 1,755 | ≤ 150 | Before sign-in |
| **Event listeners after tour** | 1,220 | ≤ 400 | CDP `JSEventListeners` |
| **JS heap after tour** | 33.5 MB | ≤ 20 MB | CDP `JSHeapUsedSize` |
| **Tab switch (cached data)** | 654 ms | ≤ 100 ms | hash change → row painted |
| **Filter keystroke → repaint** | not measured | ≤ 50 ms | Playwright type + paint timing |

### Repeatable scenarios

| # | Scenario | CI | Notes |
|---|---|---|---|
| 1 | Cold startup | **Automate · block** | Byte counts and DOM counts are deterministic on any runner. |
| 2 | Warm startup | **Automate · block** | Cache-hit assertion is binary; this is the M1 regression guard. |
| 3 | Overview idle 10–30 s | **Automate · trend** | CPU on a shared runner is noisy. Track as a trend and alert on a >50% jump rather than failing a build. **Block on DOM and listener counts instead** — deterministic proxies for the same defect. |
| 4 | Live request traffic | **Automate · trend** | Needs the k6 smoke job already in `ci.yml` to drive load against the same instance. |
| 5 | Logs open → filter → detail | **Automate · block** | Assert bounded DOM at 50 rows and exactly one mounted detail panel. |
| 6 | Overview → Logs → Keys → Overview | **Automate · block** | The key assertion is **non-monotonic DOM**: node count must come back down. |
| 7 | Degraded API (429 / 503 / offline) | **Automate · block** | Playwright route interception. Assert backoff, no banner storm, recovery. |
| 8 | SSE disconnect and recovery | **Automate · block** | Abort the stream mid-session; assert fallback to polling, then stream resumption and polling shutdown with no double-write. |

---

## 13. Testing and security plan

> **The biggest risk in this whole plan.** `[VERIFIED]` There are **zero JavaScript unit tests, zero
> integration tests and zero browser tests** for the admin console today. The four C# test files that
> mention it assert on HTTP headers and run regexes over `index.html` — valuable, but they cannot catch
> a single behavioural regression in 7,213 lines of application logic. **M3 and M4 exist primarily to
> build the safety net, not the code.** Do not start M6 until they are done.

### Existing C# tests — disposition

| Action | Tests | Reason |
|---|---|---|
| **Retain unchanged** | `ReferencesNoExternalScriptsOrStylesheets`, `DoesNotPreconnectToThirdPartyOrigins`, `VendoredAssets_AreServedLocally`, `EveryReferencedAsset_ResolvesFromThisOrigin`, `AdminAssets_CarryARestrictiveContentSecurityPolicy`, `AdminAssets_CarrySupportingSecurityHeaders` | Framework-agnostic. They enforce the hard constraints and must keep passing through every milestone — they are the proof that security did not regress. |
| **Rewrite (M2)** | `AdminIndex_CacheBustsEveryLocalAsset` | Its regex accepts `?v=\d+` or a version in the filename, and will **fail on content hashes**. Replace with: every referenced asset carries a content hash, and hashed assets are served `immutable`. |
| **Expand (M2)** | New: asset-manifest consistency | Assert that every asset `index.html` references exists in the published output. This is the guard against frontend/backend version mismatch. |
| **Add (M6)** | New: DOM ownership boundary | No Alpine directive (`x-*`, `:`, `@`) may appear inside a `[data-solid-root]` subtree. Deleted at M14 with Alpine. |
| **Delete (M14)** | `UsesOnlyExpressionsTheCspEvaluatorCanResolve`, `BindsOnlyToNamesDeclaredOnAdminApp`, `WrapsMultiRowLoopTemplatesInTheirOwnTbody` | All three exist because Alpine's CSP evaluator fails silently. `tsc` catches all three classes at compile time. Deleting them earlier would remove the guard while Alpine still runs. |
| **Tighten (M16)** | `AdminAssets_CarryARestrictiveContentSecurityPolicy` | Add an assertion that `style-src` no longer contains `'unsafe-inline'`. |

### New test suite — migration-critical only

**Unit** (Vitest, `domain/` and `api/`): sort comparators and precomputed keys; every client-side
filter predicate; `classifyError` across the status/body matrix including the six 409 lifecycle codes;
freshness and staleness decisions; retry-vs-fail decisions; summary merge/reconcile; pinned-row
snapshot retention.

**Integration** (Vitest + a fetch stub and a synthetic `ReadableStream`): the full resource lifecycle
including dedupe, abort and stale-response discard; SSE frame application; SSE failure → polling
fallback → recovery → polling shutdown; duplicate and out-of-order frame rejection; pause/resume with
parked frames; pagination; settings mutations round-tripping.

**E2E** (Playwright against the real gateway, seeded exactly as the perf harness does): admin entry and
sign-in; Overview renders; navigation across all seven tabs; pause and resume; pin survives eviction;
request detail expand/collapse; Logs filter and detail; Errors drill-down; key create and revoke; a
settings save; API failure surfaces correctly and recovers; SSE disconnect and recovery; keyboard-only
operation of one dialog and one drawer.

**Visual regression:** `[RECOMMEND]` limiting it to two shots per page — default state and one dialog
open — taken at M15 only, to catch dead-CSS deletion mistakes. Broader visual coverage on a design
system this large produces more false positives than caught bugs.

### Security requirements that must hold at every milestone

- CSP unchanged or stricter. `script-src 'self'` with no `unsafe-eval` is a merge gate, not a goal.
- No runtime CDN, no external font host, no telemetry — enforced by the retained asset tests.
- `innerHTML` reduced to zero. The only current uses are icon injection (`x-html`), all of which
  disappear.
- **Source maps:** generate them, but do not publish them to `wwwroot`. Upload as a CI artifact instead
  — the console is a privileged surface and shipping maps hands an attacker a readable map of the admin
  API.
- **Supply chain:** committed `package-lock.json`; `npm ci --ignore-scripts`; pin the Node major; add
  `npm audit --audit-level=high` alongside the existing `dotnet list package --vulnerable` gate in
  `ci-reusable.yml`.
- **Artifact integrity:** the published bundle is built in CI from a tagged commit, never uploaded by
  hand and never committed.

---

## 14. CI/CD changes

> **Decision — build artifacts: CI/build-produced, never committed.** The repository already excludes
> build output (`.gitignore`: `**/bin/**`, `**/obj/**`), and committed bundles would conflict on every
> change, defeat review, and permit source/artifact drift on a security-sensitive surface. From M2,
> `src/33pol.App/wwwroot/admin/` becomes generated output and is gitignored; the reviewed source lives
> in `src/33pol.Admin.Web/`.

### `ci-reusable.yml`

- Add a `frontend` step **before** Restore/Build — the admin integration tests `GET
  /admin/index.html`, so the output must exist before `dotnet test` runs. **This ordering is the single
  most important detail in the CI change.**
- `actions/setup-node@v4` pinned to `node-version: '20'` with `cache: 'npm'` and
  `cache-dependency-path: src/33pol.Admin.Web/package-lock.json`.
- `npm ci --ignore-scripts` → `npm run typecheck` → `npm run test` → `npm run build` →
  `npm audit --audit-level=high`.
- Bundle-size gate: a small script comparing gzipped output against the §12 budgets. Blocking.

### MSBuild integration

A target in `33pol.App.csproj` that runs the frontend build before `Publish`, and on `Build` only when
the output manifest is missing or older than the source. Gate it behind `-p:SkipFrontendBuild=true` so
a backend developer without Node can still build and run the gateway against the last-built assets.
Document that escape hatch in `docs/admin-ui.md` — this is the change most likely to annoy the team,
and it deserves to be explicit rather than discovered.

### `Dockerfile`

Add a first stage `FROM node:20-alpine AS frontend`, copying `package.json`/`package-lock.json` first
so `npm ci` caches on the same principle the existing NuGet restore layer already uses, then copying
the built output into the publish stage with `--from=frontend`. The final `aspnet:10.0` runtime image
gains nothing — **no Node ships to production.**

### `release.yml`

No change needed beyond inheriting `ci-reusable.yml`, provided the MSBuild target runs on `Publish`.
Verify the tarball at `release.yml:56–66` contains `wwwroot/admin` with hashed filenames — worth one
explicit assertion, because a silently empty admin directory would ship a broken console.

---

## 15. Risk register

Prioritised. P = probability, I = impact.

| Risk | P | I | Detection | Mitigation | Rollback |
|---|---|---|---|---|---|
| **Realtime regression** — pause, pin, fallback or recovery subtly broken | Med | High | M4 unit suite; E2E scenarios 7–8; operator report | Extract and test the realtime layer **before** any UI depends on it (M4 precedes M7). Named test per behaviour in the §8 regression surface. | Revert M7 |
| **Behavioural regression** in a migrated page | High | Med | E2E per page; parity checklist derived from `docs/admin-ui.md` | One page per commit; the Alpine panel is deleted in the same commit so the diff shows exactly what changed. | Single revert |
| **No safety net exists yet** | High | High | Already true today | M3/M4 build tests first. Treat "start M6 before the suite is green" as the plan's primary failure mode. | n/a — prevent |
| **Build/deploy regression** — empty or stale `wwwroot/admin` | Med | High | Asset-manifest test (M2); release tarball assertion | Frontend build ordered before `dotnet test` in CI, so a broken build fails there rather than in a release. | Revert M2; restore assets from git |
| **Cache invalidation mistake** — operators pinned to a stale console | Med | High | Rewritten cache test (M2); warm-load scenario | `index.html` stays `no-store`; only content-hashed files get `immutable`. The hash *is* the invalidation. | Revert to `no-store` everywhere |
| **Frontend/backend version mismatch** | Low | High | Manifest consistency test; 404s on hashed assets | Both build from one commit and ship in one artifact. No independent frontend deploy exists — keep it that way. | Redeploy previous image |
| **CSP regression** introduced by tooling | Low | High | Retained CSP tests run on every milestone | Never let Vite inline scripts: set `build.modulePreload` and asset inlining so no inline `<script>` is emitted. Verify at M2, not M16. | Revert; tests block the merge |
| **Alpine/Solid DOM conflict** during coexistence | Med | Med | Boundary test (M6); console warnings | Strict rule: Alpine owns the shell, Solid owns whole panels. Never interleaved. Shared state is localStorage only. | Revert the offending page |
| **Table performance regression** as production data grows | Med | Med | Bounded-DOM assertions in scenarios 5–6 | Server paging for Keys and Usage rollups (M9, M11) — the two genuinely unbounded tables. | Lower the page size; server-side only |
| **CSS regression** from the M15 dead-rule sweep | Med | Low | Visual regression shots at M15 | Delete per page during M6–M14, not in one sweep. M15 only removes what coverage proves is orphaned. | Revert M15 alone |
| **Accessibility regression** in new primitives | Low | Med | Keyboard suite per primitive; axe in E2E | Behaviour implemented once in M5 and reused. Current state is worse than the target, so the floor only rises. | Revert the primitive |
| **Dependency / supply-chain surface** | Low | Med | `npm audit` gate; lockfile review | Keep the runtime dependency list to Solid plus the router. Every addition must state the problem it solves. | Remove the dependency |
| **Team learning curve** — a .NET team's first JS toolchain | High | Low | Review velocity across M6–M8 | M6 is deliberately the smallest page. If M6 and M7 feel slow, that is the signal to reconsider — and the matrix already names Preact as the lower-learning-cost fallback. | Stop after M2; the quick wins are already banked |

---

## 16. Definition of done

Alpine and the legacy console may be deleted when **all** of the following are objectively true.

1. All eight pages render from `src/33pol.Admin.Web/`; no `x-` directive remains in any served HTML.
2. `vendor/alpine-csp-3.14.9.min.js`, `admin-app.js`, `admin-store.js`, `admin-icons.js`,
   `admin-errors.js` and the legacy `index.html` are deleted from source.
3. Every §12 budget is met and enforced by a blocking CI gate — in particular **idle Overview CPU ≤ 3%**,
   **no long task > 50 ms while idle**, and **DOM node count returns to baseline after a full tab tour**.
4. The full E2E suite passes, including SSE disconnect/recovery, pause/resume, pinned-row retention and
   keyboard-only dialog and drawer operation.
5. The six retained security tests pass unchanged, and `style-src` no longer contains `'unsafe-inline'`.
6. The three Alpine-shaped guard tests are deleted, and `tsc --noEmit` is a blocking CI step in their place.
7. `dotnet publish` and the Docker image both produce a working console from a clean checkout, with no
   committed build output.
8. CSS coverage ≥ 85% after a full tab tour.
9. `docs/admin-ui.md` is rewritten: the CSP-expression rules removed, the build and the
   `SkipFrontendBuild` escape hatch documented.
10. One operator has run the new console against a production-scale gateway for a full working week with
    no regression reported.

---

## 17. First implementation batch

M0 + M1. Server-side and tooling only — **no frontend framework work, no Alpine changes.** Entirely
additive, immediately valuable, and 100% retained after migration.

### Objective

Make the admin console's asset delivery correct, and make every performance claim in this document
independently reproducible. Success is a warm reload that transfers ~20 KB instead of the measured
857 KB, with all 11 existing admin asset-security tests still green.

### Implementation sequence

**1. Commit the measurement harness**
`perf/frontend/measure.mjs` · `perf/frontend/package.json` · `perf/frontend/README.md`

> **Critical detail:** `.gitignore` contains `perf/*` with only `!perf/ci/` and `!perf/k6/` unignored.
> Add `!perf/frontend/` or the directory will be silently ignored.

- Playwright as a devDependency here only — not the app's build.
- Records: transferred bytes (cold and warm), cache-hit count, DOM nodes and bound attributes at auth
  gate / Overview / after a full tab tour, CDP task-duration deltas over 20 s windows, long tasks,
  listener count, JS heap, tab-switch latency, CSS/JS coverage.
- README documents the seeding procedure: Release build on `127.0.0.1:5080` with
  `GATEWAY_ADMIN_API_KEY` set, then ~60 POSTs to `/v1/chat/completions` to populate the feed, logs and
  error store.

**2. Split the static-asset cache policy**
`src/33pol.App/GatewayHostBuilderExtensions.cs:119–131`

- `index.html` (and any extensionless `/admin` path): keep `no-store, no-cache, must-revalidate`
  exactly as today.
- `/admin/vendor/**` — fonts and the version-named Alpine build — get
  `public, max-age=31536000, immutable`. These already carry their version in the filename, so this is
  safe *today*, before any content hashing exists.
- Everything else under `/admin` keeps `no-store` for now. Hashed assets join the immutable branch in M2.
- `AdminSecurityHeaders.Apply` must continue to run for **all** branches — do not let the refactor move
  it inside one.

**3. Register response compression**
`src/33pol.App/GatewayHostBuilderExtensions.cs`

- `AddResponseCompression` with Brotli then Gzip, `EnableForHttps = true`.
- MIME types: `text/html`, `text/css`, `text/javascript`, `application/javascript`, `image/svg+xml`,
  `application/json`.
- **Explicitly exclude `text/event-stream`** — compressing the live stream would buffer frames and
  break the Overview's realtime behaviour. This is the one way this step can cause a production incident.
- `UseResponseCompression()` must sit before `UseStaticFiles()`. Do not compress `font/woff2` — woff2
  is already compressed.

**4. Preload the body-critical fonts**
`src/33pol.App/wwwroot/admin/index.html:20`

- Add `<link rel="preload" as="font" type="font/woff2" crossorigin>` for `IBMPlexSans-400.woff2` and
  `IBMPlexSans-500.woff2` only, above the `fonts.css` link.
- **Do not** remove any `@font-face` rule in this batch. All four weights are referenced through the
  `--fw-*` tokens, and the measured 0% coverage of `fonts.css` is a tooling artifact, not evidence of
  disuse.
- Check `AdminAssetSecurityTests.EveryReferencedAsset_ResolvesFromThisOrigin` still passes — it regexes
  `src`/`href` attributes and will now see the preloads.

**5. Backoff, jitter and Retry-After**
`src/33pol.App/wwwroot/admin/admin-store.js:118 fetchWithRetry`

- Preserve the existing policy exactly: GET retries once, mutations never, and any error that already
  carries `title`/`global` rethrows untouched.
- Add: honour `Retry-After` on 429/503; otherwise back off exponentially from 250 ms with ±25% jitter.
- This is the one client-side change in the batch. It is small, isolated, and its logic ports verbatim
  to `api/client.ts` in M3 — so it is not throwaway work.

**6. Reduce the Logs default page size**
`src/33pol.App/wwwroot/admin/admin-app.js:196`

`logsPageSize: 200` → `50`. Measured: 130 log rows added 5,460 DOM nodes and 4,030 bindings that are
never released. The Errors tab already uses 50 with a pager, so this is alignment, not a new pattern.

**7. Add the caching regression test**
`tests/33pol.Integration.Tests/Admin/AdminAssetCachingTests.cs` *(new)*

- `AdminIndex_IsNeverCached` — `/admin/index.html` still carries `no-store`.
- `VendoredAssets_AreImmutablyCacheable` — fonts and the Alpine build carry `max-age=31536000` and
  `immutable`.
- `AdminAssets_AreCompressed` — a request with `Accept-Encoding: br` for `admin.css` returns
  `Content-Encoding: br`.
- `LiveStream_IsNotCompressed` — `/admin/api/live` has no `Content-Encoding`. This is the guard on
  step 3's one dangerous edge.
- `AdminAssets_StillCarrySecurityHeaders` — CSP and the supporting headers survive on **both** cache
  branches.

### Tests to run

`dotnet test tests/33pol.Integration.Tests` — specifically `AdminAssetSecurityTests` (all 11),
`AdminWallboardAssetTests`, `Phase5/AdminUiIntegrationTests`, `Phase5/AdminUiSecurityTests`, and the new
`AdminAssetCachingTests`. Then `node perf/frontend/measure.mjs` against a locally seeded gateway, twice:
once on `main` to confirm the baseline reproduces, once on the branch.

### Acceptance criteria

- Warm reload transfers **≤ 20 KB** (baseline: 857 KB) with fonts and Alpine served from cache.
- Cold transfer **≤ 350 KB** (baseline: 796 KB) with text assets Brotli-encoded.
- `/admin/api/live` still streams frames with no `Content-Encoding`, and the Overview's live badge still
  reads `stream`.
- All existing admin tests green, unchanged.
- Logs tab mounts **≤ 2,600 DOM nodes** at 50 rows (baseline: 9,636 at 130 rows).
- Idle Overview CPU is **unchanged** at roughly 30% — this batch deliberately does not touch it, and a
  change here would mean something unintended happened.

### What NOT to change yet

- **No `package.json`, Vite, TypeScript or Solid.** That is M2.
- **No content-hashed filenames** — they require the build system, and
  `AdminIndex_CacheBustsEveryLocalAsset` would fail.
- **Do not touch the 500 ms tick**, `requestRows`, `mdl`, `x-show`/`x-if`, or the icon pipeline. All are
  deleted by the migration; changing them now spends review effort on code with a known end date and
  risks a regression in the console's most delicate area.
- **Do not delete any CSS or `@font-face` rule.** Coverage data is not sufficient evidence on its own.
- **Do not change CSP.** Tightening `style-src` is M16 and depends on Alpine being gone.
- **Do not add a Node stage to the Dockerfile or CI.** Nothing in this batch needs it; the perf harness
  is run manually.

---

*Prepared against commit `afeb6e0`, branch `main`. All measurements taken on 2026-09-13 from a Release
build on `127.0.0.1:5080` with a seeded SQLite store, in headless Chromium 1243 via the Chrome DevTools
Protocol. No production code was modified in producing this report.*
