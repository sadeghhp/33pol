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
| `reports/` | CI/staging run artifacts ([k6-smoke-ci.md](reports/k6-smoke-ci.md)) |
