# Phase 2 performance baseline

**Date:** 2026-05-26  
**Environment:** local macOS — Python mock upstream + `33pol.App` Release DLL  
**Gateway:** `33pol.App` @ `9b39fb1`  
**Mock upstream:** `perf/scripts/mock-upstream.py` @ `http://127.0.0.1:18080` (WireMock compose optional; Docker pull failed on this host)

## Verification

| Check | Result |
|-------|--------|
| `dotnet test 33pol.sln -c Release` | Passed (all test projects) |
| Coverage gate (`build/check-coverage.sh`) | Registry **91.2%**, Proxy **93.1%** (threshold 90%) |
| V1Parity integration | 19 tests in 4 classes (see §13 matrix below) |
| `IRequestTracker` | Wired in `ModelRouterMiddleware` via `BeginInferenceRequest` (no-op scope) |

## k6 smoke (`perf/k6/scripts/smoke.js`)

**Run (30s, 1 VU):**

```bash
# Mock (GET /health + POST chat)
python3 perf/scripts/mock-upstream.py &

# Gateway
mkdir -p /tmp/33pol-k6/config
# models.json → url http://127.0.0.1:18080, alias gpt-local
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:8080 \
  ConnectionStrings__GatewayDb= \
  Gateway__ModelsConfigPath=/tmp/33pol-k6/config/models.json \
  dotnet src/33pol.App/bin/Release/net10.0/33pol.App.dll &

# k6 (macOS Docker: use host.docker.internal)
docker run --rm -v "$PWD/perf/k6:/scripts" \
  -e BASE_URL=http://host.docker.internal:8080 -e MODEL=gpt-local \
  grafana/k6:latest run /scripts/scripts/smoke.js --duration 30s
```

| Metric | Threshold | Actual | Pass |
|--------|-----------|--------|------|
| `http_req_failed` | &lt; 1% | **0.00%** (0/30) | ✓ |
| `http_req_duration` p95 | &lt; 500 ms | **5.18 ms** | ✓ |

k6 summary (2026-05-26): `checks_succeeded=100%`, `http_reqs=30`, `iterations=30`.

## §13 V1Parity integration classes

| Class | Tests |
|-------|------:|
| `InferenceProxyEndpointTests` | 8 |
| `PassthroughEndpointTests` | 2 |
| `LiveRegistryIntegrationTests` | 5 |
| `ModelsEndpointTests` | 4 |

Admin reload/status also covered in `ConfigAdminEndpointTests` (fixture-based).

## Notes

- Smoke uses non-streaming `POST /v1/chat/completions` only (Phase 5 adds RPS + streaming suites).
- Registry watch mode: FileSystemWatcher + debounce + **≤5s** hash poll fallback when `RegistryWatchEnabled` is true.
- Docker `wiremock/wiremock:3.9.1` pull may fail with containerd layer errors; use `perf/scripts/mock-upstream.py` for local smoke.
