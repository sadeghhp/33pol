# Phase 2 performance baseline

**Project:** 33pol  
**Date:** _YYYY-MM-DD_  
**Environment:** _local / docker-compose / ci-smoke_  
**Gateway version:** _from `GET /` or assembly version_  
**Commit:** _sha_

## Setup

| Item | Value |
|------|--------|
| `BASE_URL` | |
| Mock upstream | WireMock (`deploy/docker`) or integration `MockOpenAiUpstreamHandler` |
| `models.json` models | |
| k6 version | |

## Commands

```bash
# Docker stack (mock upstream on :8081, gateway on :8080 when profile enabled)
docker compose -f deploy/docker/docker-compose.yml up -d wiremock

# k6 smoke (gateway must route to mock)
k6 run perf/k6/scripts/smoke.js -e BASE_URL=http://localhost:8080
```

## Results

| Scenario | VUs | Duration | p95 latency | Error rate | Pass |
|----------|-----|----------|-------------|------------|------|
| `smoke.js` | 1 | 60s | | | |

Thresholds reference: `perf/k6/thresholds.json` → `smoke`.

## Coverage (Registry + Proxy)

| Assembly | Line % | Branch % | Gate (≥90%) |
|----------|--------|----------|-------------|
| `33pol.Registry` | | | |
| `33pol.Proxy` | | | |

```bash
dotnet test 33pol.sln -c Release --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

## Notes

- _Streaming TTFT, gateway overhead differential: Phase 5_
- _Operator console overhead: Phase 4+_

## Sign-off

- [ ] `dotnet test` green (including `Category=V1Parity`)
- [ ] k6 smoke thresholds met
- [ ] Registry + Proxy coverage ≥ 90%
