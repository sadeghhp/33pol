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
| `cache.mjs` | Does caching actually work? Cold vs. return visits using a **persistent** browser profile |
| `attribute.mjs` | *Which mechanism* owns idle CPU — disables the 500 ms tick, then polling and SSE, and re-samples |
| `accum.mjs` | Does anything ever get unmounted? Walks every tab and tracks nodes, listeners and heap |
| `coverage.mjs` | Per-panel DOM weight, plus how much of the shipped CSS/JS is ever used |

> **Use `cache.mjs`, not `measure.mjs`, to judge caching.** Playwright's default context is
> incognito-style with no persistent disk cache, so `measure.mjs`'s "warm reload" re-downloads every
> font and makes a correct `immutable` policy look broken. `cache.mjs` drives a real on-disk profile
> and closes the browser between visits, so a cache hit there is a real one.

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

## Reading the numbers

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

| File | Commit | What it captures |
|---|---|---|
| `2026-09-13-afeb6e0-pre-m1.json` | `afeb6e0` | Pre-M1: 668 KB cold, 857 KB warm with zero cache hits, every asset `no-store` and uncompressed, 30.7% idle Overview CPU, 103-row Logs at 8,475 DOM nodes |
| `2026-09-13-m1-asset-delivery.json` | M1 | After the cache split, compression, font preload and the Logs page size: 307 KB cold, 50-row Logs at 6,251 nodes |
| `2026-09-13-m1-cache.json` | M1 | Return-visit behaviour on a persistent profile: 343 KB first visit → 197 KB on every return, fonts fully cached |
