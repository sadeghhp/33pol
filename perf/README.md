# Performance & Load Testing

k6 scenarios, mock upstreams, and the scripted verification suite used to check a build before release.

## k6 scripts

| File | Purpose |
|------|---------|
| `k6/scripts/smoke.js` | 1 VU short smoke — runs in CI on every push |
| `k6/scripts/inference-rps.js` | Ramping RPS against the inference path |
| `k6/scripts/streaming-concurrent.js` | Concurrent SSE streams |
| `k6/scripts/rate-limit-storm.js` | 429 behavior under deliberate limit pressure |
| `k6/scripts/soak.js` | Long soak (default 4h) — memory and WAL growth |
| `k6/scripts/overhead-compare.js` | Direct-to-mock vs through-gateway p99 |
| `k6/thresholds.json` | Shared threshold definitions |
| `k6/lib/helpers.js` | Shared helpers |

## Runners

| Script | What it does |
|--------|--------------|
| `ci/run-smoke.sh` | Mock upstream + gateway + `smoke.js` (the CI `k6-smoke` job) |
| `ci/run-ga-local.sh` | Shortened `inference-rps` / `streaming-concurrent` / `rate-limit-storm` against an already-running gateway |
| `ci/run-ga-compose-k6.sh` | Same suite against the full Docker Compose stack |
| `ci/run-ga-compose-local.sh` | Compose suite variant for a local (non-CI) host |
| `ci/run-soak-local.sh` | Shortened soak (`SOAK_DURATION`, default 10m) |
| `ci/run-overhead-compare.sh` | Gateway overhead comparison (needs mock on `:18080`) |
| `ci/run-compose-e2e.sh` | Compose end-to-end: health + inference + streaming |
| `ci/verify-compose-health.sh` | All Compose services running and healthy |
| `ci/verify-compose-health-gpu.sh` | Same, for the remote-GPU Compose profile |
| `ci/verify-grafana-dashboards.sh` | Grafana file-provisioned dashboards + Prometheus datasource |
| `ci/verify-observability-local.sh` | `/metrics` scrape path, Prometheus targets, Grafana health |

| Helper | Purpose |
|--------|---------|
| `scripts/mock-upstream.py` | Threaded Python mock OpenAI upstream, answers instantly (k6 smoke) |
| `scripts/concurrent-mock-upstream.py` | asyncio mock with configurable latency (`LATENCY`, streamed `TOKENS`); serves any number of requests at once and reports peak concurrency at `/__stats` — for "is it parallel?" tests |
| `scripts/concurrency-bench.py` | Fires N requests at once (stdlib only) and prints wall-clock vs single-request latency, TTFT, and which admission limit produced any 429s. Run against the gateway and against the model server directly to attribute a bottleneck |
| `scripts/sdk-smoke.py` | OpenAI Python SDK check — models list, chat, streaming chat |

## Full local verification

Run from the repo root with the Compose stack up. This is the sequence to run before cutting a release:

```bash
docker compose up -d --build
bash perf/ci/verify-compose-health.sh
bash perf/ci/run-compose-e2e.sh
python3 perf/scripts/sdk-smoke.py    # set OPENAI_BASE_URL, OPENAI_API_KEY, MODEL=mock-gpt
bash perf/ci/run-ga-compose-k6.sh
bash perf/ci/run-soak-local.sh       # SOAK_DURATION to lengthen
dotnet test 33pol.sln -c Release
```

The gateway's `mock-gpt` upstream is WireMock. It answers both streaming and non-streaming chat, but it is optimistic for time-to-first-token — treat TTFT numbers from Compose as a floor, not a measurement.

## Staging / production-like runs

`.github/workflows/k6-ga-staging.yml` (workflow_dispatch) runs `inference-rps`, `streaming-concurrent`, and `rate-limit-storm` against a supplied `base_url`, `model`, and `api_key`.

The soak is manual:

```bash
export BASE_URL=https://staging.example API_KEY=sk-... SOAK_DURATION=4h SOAK_VUS=5
k6 run perf/k6/scripts/soak.js
```

Numbers from a real upstream (vLLM or a cloud provider) are the only ones worth quoting for capacity planning — the mock saturates its own accept queue long before the gateway does.

## Reports

`reports/` holds run records worth keeping. Two are current:

- [k6-smoke-ci.md](reports/k6-smoke-ci.md) — what the CI smoke job runs and how to reproduce it locally.
- [concurrency-2026-08-16.md](reports/concurrency-2026-08-16.md) — does the request path serialize? (No: 64 concurrent 2 s requests finish in 2 s.) What made it *look* serial — bulkhead 64, per-tenant stream cap 50, fixed-window `Retry-After` up to 59 s — and the changes that followed.
- [sqlite-wal-2026-07-19.md](reports/sqlite-wal-2026-07-19.md) — SQLite WAL behavior under concurrent write load, and the pragma tuning conclusions that follow from it. Read this before changing `busy_timeout`, `synchronous`, or WAL checkpointing.

Ad-hoc run records go here too, but delete them once the build they describe is several releases old — a stale report reads as a current claim.
