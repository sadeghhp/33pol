# Performance & Load Testing

Planned scenarios and thresholds for 33pol v2. **Scripts are added in Phase 2 (smoke) and Phase 5 (GA suite).**

See [implementation-plan/03-performance-and-load-testing.md](../implementation-plan/03-performance-and-load-testing.md).

| File | Status |
|------|--------|
| `k6/thresholds.json` | Threshold definitions (planning) |
| `k6/scripts/smoke.js` | Phase 2 smoke (1 VU, 60s) |
| `k6/scripts/inference-rps.js` | Phase 5 GA — ramp RPS |
| `k6/scripts/streaming-concurrent.js` | Phase 5 GA — concurrent SSE |
| `k6/scripts/rate-limit-storm.js` | Phase 5 GA — 429 behavior |
| `k6/scripts/soak.js` | Phase 5 GA — long soak (manual staging, default 4h) |
| `k6/lib/helpers.js` | Shared k6 helpers |
| `ci/run-smoke.sh` | Mock upstream + gateway + smoke (used in CI) |
| `ci/run-ga-local.sh` | Shortened GA scripts against running gateway |
| `ci/run-overhead-compare.sh` | Direct mock vs gateway p99 comparison |
| `k6/scripts/overhead-compare.js` | Dual-scenario overhead script |
| `reports/` | CI/staging artifacts ([k6-smoke-ci.md](reports/k6-smoke-ci.md), [ga-2026-05-26.md](reports/ga-2026-05-26.md)) |

**Staging GA:** `.github/workflows/k6-ga-staging.yml` (workflow_dispatch) runs full `inference-rps`, `streaming-concurrent`, and `rate-limit-storm` against a provided `base_url`.

| Script | Purpose |
|--------|---------|
| `scripts/sdk-smoke.py` | OpenAI Python SDK manual GA (chat + stream + models list) |
| `ci/verify-compose-health.sh` | Full Compose stack (gateway + mock + Prometheus + Grafana) |

Sign-off steps: [docs/ga-signoff.md](../docs/ga-signoff.md).
