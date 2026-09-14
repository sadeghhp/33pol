# Admin console performance harness

Browser-side measurement for `/admin`, used to produce the before/after numbers in
[../../docs/admin-frontend-migration-plan.md](../../docs/admin-frontend-migration-plan.md).

This is **development tooling**. It is never shipped, never referenced by the gateway, and adds no
production dependency — `playwright` is a devDependency of this directory alone and is not part of
the .NET build or the Docker image.

Every script drives a real gateway over the Chrome DevTools Protocol and writes machine-readable JSON
to `results/` (gitignored). Nothing imports application source, so the same scripts keep working
across the Alpine → Solid migration — which is the point: the same harness produces both sides of
every comparison.

## Scripts

| Script | Answers |
|---|---|
| `measure.mjs` | The full baseline: cold transfer, compression, DOM and binding counts, idle CPU, long tasks, tab-switch latency |
| `cache.mjs` | Does caching actually work? First vs. return visits, **signed in**, on a **persistent** browser profile |
| `attribute.mjs` | *Which mechanism* owns idle CPU — disables the 500 ms tick, then polling and SSE, and re-samples |
| `accum.mjs` | Does anything ever get unmounted? Walks every tab and tracks nodes, listeners and heap |
| `coverage.mjs` | Per-panel DOM weight, plus how much of the shipped CSS/JS is ever used |

> **Use `cache.mjs`, not `measure.mjs`, to judge caching.** Playwright's default context is
> incognito-style with no persistent disk cache, so `measure.mjs`'s "warm reload" re-downloads every
> font and makes a correct `immutable` policy look broken. `cache.mjs` drives a real on-disk profile
> and closes the browser between visits, so a cache hit there is a real one.
>
> **The two scripts are not interchangeable, and their byte figures must never be compared to each
> other.** `measure.mjs` reports a cold load that stops at the auth gate; `cache.mjs` signs in, so
> every visit it reports carries the full console — several more faces and the icon module. A
> before/after pair has to come from the same script or it is measuring two different pages. The
> committed baselines below are labelled accordingly.

## Setup

```bash
cd perf/frontend
npm install
npx playwright install chromium     # once per machine
```

## Running

The harness needs a gateway with **representative data** — an empty gateway renders empty tables and
measures nothing useful.

```bash
# 1. Build and start the gateway (Release: Debug distorts CPU numbers)
dotnet build src/33pol.App/33pol.App.csproj -c Release
GATEWAY_ADMIN_API_KEY=sk-33pol-dev-local-unsafe \
ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/33pol.App/33pol.App.csproj -c Release --no-build

# 2. Seed the request feed, log ring and error store.
#    These POSTs are expected to fail with 502 — there is no upstream. The failures are what
#    populate all three surfaces, which is exactly what the console needs to render.
K=sk-33pol-dev-local-unsafe
for i in $(seq 1 60); do
  curl -s -o /dev/null -X POST http://127.0.0.1:5080/v1/chat/completions \
    -H "X-API-Key: $K" -H 'Content-Type: application/json' \
    -d "{\"model\":\"local-mock\",\"messages\":[{\"role\":\"user\",\"content\":\"perf probe $i\"}]}" &
done; wait

# 3. Measure
cd perf/frontend && npm run measure
```

### Configuration

| Variable | Default | Meaning |
|---|---|---|
| `ADMIN_BASE_URL` | `http://127.0.0.1:5080` | Gateway origin |
| `GATEWAY_ADMIN_API_KEY` | `sk-33pol-dev-local-unsafe` | Admin key used at the sign-in gate |
| `IDLE_WINDOW_MS` | `20000` | Length of each idle-CPU sampling window |
| `RESULT_DIR` | `./results` | Where JSON output is written |
| `CACHE_PROFILE_DIR` | `$TMPDIR/33pol-admin-cache-profile` | `cache.mjs` only. **`cache.mjs` deletes this directory** to produce a cold first visit, so its name must start with `33pol-admin-cache-profile` — the script refuses to run otherwise rather than trust the value. |

Comparing two builds means one gateway per build on its own port, seeded identically, and one
`CACHE_PROFILE_DIR` each — a shared profile would serve the second build out of the first's cache.

## Reading the numbers

- **"KB" everywhere here means KiB** (bytes ÷ 1024), which is what the scripts print. Quoting one
  figure in KiB and another in decimal kB makes a 2.5% difference that looks like a real change —
  M1's first write-up compared a decimal-kB "before" against a KiB "after" and overstated the win.
- **`measure.mjs`'s cold total is not a stable metric.** It stops recording at `networkidle`, and
  which on-demand faces land inside that window shifts between runs and between builds (pre-M1: 8
  requests and no fonts; post-M1: 10–12, because two faces are now preloaded). Compare its
  *per-asset* figures, or the shared subset; take whole-page totals from `cache.mjs`, which waits a
  fixed interval after the shell and recorded the same 17 requests on both builds.
- **`cpuPct`** is CDP `TaskDuration` over the sampling window. 30 means the page burned roughly a
  third of one core while nobody touched it.
- **`boundAttrs`** counts `x-*`, `:*` and `@*` attributes in the live DOM. It is deliberately
  framework-agnostic: a migrated panel should trend toward zero, so the same metric stays meaningful
  on both sides of the migration.
- **`monotonic: true`** in `accum.mjs` means DOM only ever grew — nothing was unmounted. Flipping this
  to `false` is the acceptance criterion for the first migrated panel.
- **CPU is noisy — more than you would guess.** Five consecutive 20 s samples of the *same* code on
  one developer box spanned **31.5% – 43.6%** (12.1 points). Treat any single reading as indicative
  only: compare medians of at least three runs, and gate CI on the deterministic metrics (transferred
  bytes, DOM nodes, listener counts) rather than on CPU. Use `attribute.mjs` for questions of the form
  "which mechanism is responsible", since its differential design cancels most of that noise.
- **`fonts.css` always reports 0% CSS coverage.** Chrome does not count `@font-face` as used. This is a
  tooling artifact — never delete a font face on the strength of it.

## Baselines

`baseline/` holds committed reference runs. Keep the filename convention
`<date>-<commit>-<label>.json` and add a row below rather than overwriting, so regressions are
attributable to a specific change.

| File | Script | Commit | What it captures |
|---|---|---|---|
| `2026-09-13-afeb6e0-pre-m1.json` | `measure.mjs` | `afeb6e0` | Pre-M1: 652.5 KB cold at the gate (8 requests, no fonts yet), 837.1 KB signed-in reload with zero cache hits, every asset `no-store` and uncompressed, 30.7% idle Overview CPU, 103-row Logs at 8,475 DOM nodes |
| `2026-09-13-m1-asset-delivery.json` | `measure.mjs` | M1 | After the cache split, compression, font preload and the Logs page size: 307.1 KB cold at the gate (10 requests — the preloaded faces are now inside the window), 50-row Logs at 6,251 nodes |
| `2026-09-14-afeb6e0-pre-m1-cache.json` | `cache.mjs` | `afeb6e0` | Pre-M1 return visits: 925.2 KB on all three, byte for byte, 0 served from cache — `no-store` meant a persistent profile bought nothing |
| `2026-09-14-m1-cache-signed-in.json` | `cache.mjs` | M1 | The matched pair for the row above: 491.5 KB first visit → **199.1 KB** on every return, all 272.8 KB of fonts served from cache |
| `2026-09-13-m1-cache.json` | `cache.mjs` (pre-sign-in) | M1 | **Superseded.** Taken before `cache.mjs` signed in, so it measures the auth gate rather than the console, and has no pre-M1 counterpart. Kept for provenance only — do not compare it to anything. |
