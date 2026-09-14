# Admin frontend migration — executable milestone map

Operational companion to [admin-frontend-migration-plan.md](admin-frontend-migration-plan.md). The
plan holds the evidence and the reasoning; this file is the execution checklist. One row per
milestone, each independently reviewable and revertible.

**Status legend:** ✅ done · 🔄 in progress · ⬜ not started

| # | Milestone | Status |
|---|---|---|
| M0 | Measurement harness | ✅ |
| M1 | Asset delivery (cache split, compression, preload, backoff, logs page size) | ✅ |
| M2 | Solid + Vite + TypeScript foundation | ⬜ |
| M3 | API / domain extraction | ⬜ |
| M4 | Realtime + data layer extraction | ⬜ |
| M5 | Shared UI primitives | ⬜ |
| M6 | Logs panel migration | ⬜ |
| M7 | Overview panel migration | ⬜ |
| M8–M13 | Errors · Keys · Routing · Usage · Settings · Rate limits | ⬜ |
| M14 | Alpine removal | ⬜ |
| M15 | CSS / dead-code cleanup | ⬜ |
| M16 | CSP tightening (`style-src`) | ⬜ |
| M17 | Final performance verification | ⬜ |

---

## M0 — Measurement harness ✅

- **Objective** Make every performance claim reproducible before changing anything that could affect
  performance.
- **Scope** `perf/frontend/` only. No application code.
- **Files** `perf/frontend/{measure,attribute,coverage,accum}.mjs`, `perf/frontend/lib/harness.mjs`,
  `perf/frontend/package.json`, `perf/frontend/README.md`, `perf/frontend/baseline/`, `.gitignore`.
- **Dependencies** None.
- **Acceptance** Harness runs against a locally seeded gateway and emits machine-readable JSON;
  recorded baseline reproduces within ±15%.
- **Verification** `node perf/frontend/measure.mjs`; `git check-ignore` confirms `perf/frontend/` is
  tracked.
- **Rollback boundary** Delete `perf/frontend/` and revert the `.gitignore` line.
- **Security impact** None — dev tooling, never shipped, never referenced by the app.
- **Performance impact** None (measurement only).

## M1 — Asset delivery ✅

- **Objective** Correct cache and compression semantics for `/admin`; remove the measured
  925 KB-per-visit delivery cost without touching rendering behaviour.
- **Scope** Static-file cache policy, response compression (scoped to `/admin`), font preload, HTTP
  retry policy and its poll guards, Logs default page size. **No** content hashing, **no** CSP
  change, **no** build system.
- **Files** `src/33pol.App/GatewayHostBuilderExtensions.cs`,
  `src/33pol.App/wwwroot/admin/index.html`, `src/33pol.App/wwwroot/admin/admin-store.js`,
  `src/33pol.App/wwwroot/admin/admin-app.js`,
  `tests/33pol.Integration.Tests/Admin/AdminAssetCachingTests.cs`.
- **Dependencies** M0 (so the effect is measurable).
- **Acceptance** Cold ≤ 350 KB · return visit substantially reduced with fonts fully cached · text
  assets Brotli-encoded · `text/event-stream` never compressed and still streaming · all pre-existing
  admin tests green · Overview rendering untouched.
- **Measured result** Both builds measured with the same scripts, the same seeded data and the same
  machine; `afeb6e0` is the pre-M1 commit. Figures are KiB, as the harness prints them.

  The two scripts answer different questions and their totals are **not** interchangeable, so each
  row names its own: `measure.mjs` uses an incognito context and stops at the auth gate; `cache.mjs`
  drives a persistent on-disk profile with the console signed in, which is what an operator's return
  visit actually is.

  | | Harness | Pre-M1 (`afeb6e0`) | Post-M1 |
  |---|---|---:|---:|
  | The 8 text assets both builds fetch cold | `measure.mjs` | 652.5 KB | **218.8 KB** (−66.5%) |
  | First visit, signed in | `cache.mjs` | 925.2 KB | **491.5 KB** |
  | Return visit, signed in | `cache.mjs` | 925.2 KB, 0 cached | **199.1 KB**, fonts 0 B, 10 cached |
  | `admin-app.js` over the wire | both | 315.1 KB | **112.5 KB** |
  | `admin.css` / `index.html` | both | 104.9 / 160.8 KB | **32.7 / 43.1 KB** |
  | Logs tab DOM | `measure.mjs` | 8,475 nodes (103 rows) | **6,251 nodes** (50 rows) |

  Pre-M1, all three `cache.mjs` visits transferred 925.2 KB byte for byte with not one cache hit:
  every asset was `no-store`, so a persistent profile bought nothing. Post-M1 a return visit keeps
  all 272.8 KB of fonts and the Alpine bundle in cache and re-fetches only `index.html` and the
  `?v=N` assets — which is the 199.1 KB, and which M2's content hashing is what finally removes.

  The **first row is deliberately a subset, not a total.** `measure.mjs` ends its cold recording at
  `networkidle`, and which on-demand faces land inside that window varies run to run — pre-M1 caught
  none (8 requests), post-M1 catches two to four because they are now preloaded (10–12 requests). So
  its raw cold totals compare different sets of assets and the per-asset comparison above is the
  meaningful one. `cache.mjs` waits a fixed interval after the shell and recorded 17 requests on both
  builds, which is why the whole-page numbers are taken from it.

  **No idle-CPU claim is made.** Five consecutive 20 s samples of identical code spanned
  31.5%–43.6% on this machine, so M1's effect on CPU — if any — is inside the noise. That number is
  M7's to move and M7's to measure.

- **Verification** Full integration suite + 17 caching tests + live header inspection on a Release
  gateway + SSE frame-arrival timing under compression + `measure.mjs` / `cache.mjs` run against
  pre-M1 and post-M1 gateways side by side. Each of the two regression tests added for the fixes
  below was confirmed to fail against the behaviour it guards against before being kept.
- **Deviation from the plan** §17 set "warm reload ≤ 20 KB" as an M1 criterion. That is not reachable
  in M1: what still transfers on a return visit is `index.html` plus the hand-versioned `?v=N`
  assets, which must stay `no-store` until content hashing makes their URL identity trustworthy.
  §17's own "what NOT to change yet" forbids content hashing here, so the ≤ 20 KB target belongs to
  M2. M1 delivers 925.2 KB → 199.1 KB on a return visit.
- **Rollback boundary** Single revert; server-side plus two small client edits, no structural change.
- **Security impact** Neutral-to-positive. CSP and security headers unchanged and still applied on
  every cache branch (asserted). Compression enabled on header-authenticated responses only — see
  the BREACH note in `GatewayHostBuilderExtensions.cs` — and scoped to `/admin`, so the inference
  data path is untouched (`NonAdminResponses_AreNotCompressed`).
- **Two rules worth keeping straight** (both have a regression test, both were got wrong first):
  immutable caching keys on the *URL path* identifying the content, never on the directory —
  `vendor/fonts.css` lives beside the immutable faces but is `?v=N` source, so it stays `no-store`
  (`HandVersionedVendorCss_IsNotImmutablyCached`). And compression is an admin asset-delivery
  measure: registered unscoped it also wraps the proxy's data path, which costs CPU on a hot path
  for nothing, since a compressed upstream is passed through untouched and a streamed token chunk is
  too small to compress.
- **Performance impact** Measured below.

## M2 — Solid + Vite + TypeScript foundation ⬜

- **Objective** Establish the build with **zero UI change**. The console must be byte-for-byte
  equivalent in the browser.
- **Scope** New `src/33pol.Admin.Web/`; current Alpine assets move to `legacy/` and are copied to the
  output verbatim; Vite emits to `src/33pol.App/wwwroot/admin/`, which becomes generated and
  gitignored. MSBuild target on `Publish` with `-p:SkipFrontendBuild=true` escape hatch. CI Node job
  ordered **before** `dotnet test`. Docker `node:20-alpine` stage.
- **Acceptance** Identical rendered console · `dotnet publish` and Docker both produce a working
  `wwwroot/admin` · all admin tests green · no inline `<script>` emitted (CSP).
- **Verification** Byte-diff of served assets vs. pre-M2; full admin test suite; `docker build`.
- **Rollback boundary** Revert commit, restore `wwwroot/admin` from git, drop the CI job.
- **Security impact** Must hold `script-src 'self'`. Source maps generated but **not** published to
  `wwwroot`. `npm ci --ignore-scripts`, committed lockfile, pinned Node major.
- **Watch** Content hashing lands here, so `AdminIndex_CacheBustsEveryLocalAsset` must be rewritten
  **in the same commit** — its regex accepts only `?v=N` or a version-in-filename and will fail on
  `app-a1b2c3d4.js`. Hashed assets then join the immutable cache branch added in M1.

## M3 — API / domain extraction ⬜

- **Objective** Typed, framework-free boundaries. No UI wiring.
- **Scope** `types/`, `api/` (client, `classifyError` ported verbatim, retry policy from M1),
  `domain/` (sort with precomputed keys, filters, formatters, freshness).
- **Acceptance** `tsc --noEmit` clean · Vitest ≥ 90% on `domain/` · ported `classifyError` matches the
  original across the status/body matrix incl. the six 409 lifecycle codes.
- **Rollback boundary** Revert — dead code until M6.

## M4 — Realtime + data layer extraction ⬜

- **Objective** Isolate SSE, polling, reconnect, freshness and recent-arrival state from rendering.
- **Requirements** Testable without a DOM · explicit subscription lifecycle and teardown · no
  duplicate polling · no duplicate SSE connections · **no global 500 ms invalidation clock**.
- **Acceptance** Every behaviour in the plan's §8 regression surface has a named passing test; a fake
  `ReadableStream` drives connect → interrupt → backoff → poll fallback → recover → poll shutdown;
  duplicate and out-of-order frames rejected via `frame.version`.
- **Rollback boundary** Revert — still unused.

## M5 — Shared UI primitives ⬜

- **Scope** Dialog, Drawer, Tabs, DisclosureRow, Menu, Field, Alert, Toast, Button, Select; compiled
  SVG icons; tokens ported from `admin.css:8–264`.
- **Acceptance** Per-primitive keyboard suite (focus trap, initial focus, restore, Escape, `inert`
  background) · axe clean · visual parity.

## M6 — Logs panel ⬜ (first UI migration)

- **Why first** Smallest useful vertical slice — 67 markup nodes, yet exercises resource lifecycle,
  polling cadence, server pagination, a filter, a detail disclosure and three primitives.
- **Gate** Behaviour, data loading, filters and refresh preserved · correct mount/unmount · no leaked
  listeners · no duplicate subscriptions or polling · CSP valid · styling correct · tests pass ·
  **Alpine no longer owns the Logs surface**.
- **New invariant test** No Alpine directive (`x-*`, `:`, `@`) inside a `[data-solid-root]` subtree.
- **Rule** Do not scale the pattern to another panel until Logs proves it.

## M7 — Overview panel ⬜

- **Why second** 100% of the measured idle CPU lives here; M4 has already de-risked the realtime layer.
- **Gate** `_nowTick` whole-row invalidation eliminated · only genuinely time-dependent values update
  with time · stable request data stays stable · realtime updates correct · timers minimal and
  explicitly owned · deterministic teardown.
- **Measure before and after** idle CPU, long tasks, DOM nodes, transferred bytes, cache behaviour,
  active timers, active subscriptions. Target: **idle CPU ≤ 3%**, zero long tasks > 50 ms.

## M8–M13 — Errors · Keys · Routing · Usage · Settings · Rate limits ⬜

One panel per milestone, one commit each, in that order. Keys adds server-side paging/filtering (the
table most likely to grow unbounded). Rate limits is last: ~22% of `admin-app.js` and the best
code-splitting candidate (lazy chunk ≤ 40 KB gzip).

## M14 — Alpine removal ⬜

Solid takes the shell, router, auth gate, toasts and global alert. Delete Alpine, `legacy/`, and the
three Alpine-shaped guard tests (`UsesOnlyExpressionsTheCspEvaluatorCanResolve`,
`BindsOnlyToNamesDeclaredOnAdminApp`, `WrapsMultiRowLoopTemplatesInTheirOwnTbody`) — `tsc` replaces
them. Last point at which the old console still exists in git history.

## M15 — CSS / dead-code cleanup ⬜

Delete rules orphaned by M6–M14; move the 94 literal `style=` attributes into classes. Acceptance: CSS
coverage ≥ 85% after a full tab tour, no visual regression. **Never** delete CSS or `@font-face` rules
on coverage numbers alone.

## M16 — CSP tightening ⬜

Drop `'unsafe-inline'` from `style-src`. **Blocked until** no remaining code mutates inline styles —
i.e. after M14 and M15. Update `AdminAssets_CarryARestrictiveContentSecurityPolicy` to assert the
stricter policy.

## M17 — Final performance verification ⬜

Every budget in the plan's §12 met and enforced by a blocking CI gate. Byte and DOM assertions block;
CPU is tracked as a trend (shared runners are too noisy to gate on).
