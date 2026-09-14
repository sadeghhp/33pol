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

- **Objective** Correct cache and compression semantics for `/admin`; remove the measured 857 KB
  warm-reload cost without touching rendering behaviour.
- **Scope** Static-file cache policy, response compression, font preload, HTTP retry policy, Logs
  default page size. **No** content hashing, **no** CSP change, **no** build system.
- **Files** `src/33pol.App/GatewayHostBuilderExtensions.cs`,
  `src/33pol.App/wwwroot/admin/index.html`, `src/33pol.App/wwwroot/admin/admin-store.js`,
  `src/33pol.App/wwwroot/admin/admin-app.js`,
  `tests/33pol.Integration.Tests/Admin/AdminAssetCachingTests.cs`.
- **Dependencies** M0 (so the effect is measurable).
- **Acceptance** Cold ≤ 350 KB · return visit substantially reduced with fonts fully cached · text
  assets Brotli-encoded · `text/event-stream` never compressed and still streaming · all pre-existing
  admin tests green · Overview rendering untouched.
- **Measured result**

  | | Pre-M1 | Post-M1 |
  |---|---:|---:|
  | Cold transfer | 668 KB | **307 KB** |
  | Return visit (persistent profile) | 857 KB, 0 cached | **197 KB**, fonts 0 B |
  | `admin-app.js` over the wire | 315 KB | **112 KB** |
  | `admin.css` / `index.html` | 105 / 161 KB | **33 / 43 KB** |
  | Logs tab DOM | 8,475 nodes (103 rows) | **6,251 nodes** (50 rows) |

- **Verification** Full integration suite (368 passed) + 15 new caching tests + live header
  inspection + SSE frame-arrival timing under compression + `measure.mjs` / `cache.mjs` before/after.
- **Deviation from the plan** §17 set "warm reload ≤ 20 KB" as an M1 criterion. That is not reachable
  in M1: what still transfers on a return visit is `index.html` plus the hand-versioned `?v=N`
  assets, which must stay `no-store` until content hashing makes their URL identity trustworthy.
  §17's own "what NOT to change yet" forbids content hashing here, so the ≤ 20 KB target belongs to
  M2. M1 delivers 857 KB → 197 KB.
- **Rollback boundary** Single revert; server-side plus two small client edits, no structural change.
- **Security impact** Neutral-to-positive. CSP and security headers unchanged and still applied on
  every cache branch (asserted). Compression enabled on header-authenticated responses only — see
  the BREACH note in `GatewayHostBuilderExtensions.cs`.
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
