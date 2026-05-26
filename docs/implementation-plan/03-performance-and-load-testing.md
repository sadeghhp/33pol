# Performance & Load Testing Plan

## Objectives

1. **Prove** gateway overhead is acceptable vs direct upstream access.  
2. **Validate** streaming (SSE) does not buffer full responses.  
3. **Find** saturation points (connections, streams, CPU, thread pool).  
4. **Gate GA** with repeatable k6 scenarios and documented SLOs.  

Load testing is **planned from Phase 2** (baselines) and **mandatory in Phase 5** (GA).

### Operator console (Phase 4 optional)

When WP4.9 is in scope, run a **smoke** alongside the Phase 2 proxy baseline:

- **A:** k6 steady load, console **disabled** (production default).  
- **B:** Same load, console enabled with `watch summary` at default `RefreshInterval` (1 Hz).  

**Gate (guide):** Gateway overhead p99 delta (B − A) ≤ **1 ms** on `local-perf` with mock upstream. Full sign-off optional in Phase 5. See [08-operator-console.md](./08-operator-console.md) §6 (P7).

---

## Environments

| Environment | Purpose | Upstream |
|-------------|---------|----------|
| **local-perf** | Developer quick checks | Mock (WireMock) or dockerized mock SSE |
| **ci-smoke** | PR optional; short k6 | Mock upstream container |
| **staging** | Realistic load | Real vLLM (small model) or dedicated perf backend |
| **pre-prod** | GA gate | Production-like hardware |

---

## Service level objectives (targets)

| SLI | Target (initial) | Measured by |
|-----|------------------|-------------|
| Availability | 99.9% monthly | Non-5xx / total (excl. 429) |
| Gateway overhead | p99 &lt; 5 ms vs direct | Differential in k6 |
| Inference latency | p99 &lt; 60 s (model-dependent) | `gateway_inference_duration_seconds` |
| TTFT (streaming) | p95 &lt; 2 s (mock upstream) | Custom k6 / metric |
| Error rate | &lt; 0.1% at 50% max load | Prometheus |

*Adjust after first baseline run; document actuals in `perf/reports/`.*

---

## Tooling

| Tool | Role |
|------|------|
| **k6** | Primary load tests (HTTP, SSE checks) |
| **BenchmarkDotNet** | Micro-benchmarks hot paths (registry lookup, JSON parse) — optional |
| **dotnet-counters** | GC, thread pool under load |
| **Prometheus** | Scrape during tests; Grafana snapshots |

### Repository layout

```text
perf/
├── k6/
│   ├── scripts/
│   │   ├── smoke.js              # Phase 2: 1 VU, 1 min
│   │   ├── inference-rps.js      # Phase 5: ramp RPS
│   │   ├── streaming-concurrent.js
│   │   └── rate-limit-storm.js
│   ├── lib/
│   │   └── helpers.js
│   └── thresholds.json
├── benchmarks/                   # optional BenchmarkDotNet project
└── reports/
    └── .gitkeep                  # CI uploads HTML/JSON artifacts
```

---

## Scenarios

### 1. Smoke (`smoke.js`) — Phase 2+

- 1 VU, 60s  
- POST `/v1/chat/completions` non-stream  
- Assert 200, p95 &lt; 500 ms (mock upstream)  

**CI:** Optional on `main` nightly.

### 2. Inference RPS (`inference-rps.js`) — Phase 5

- Stages: 10 → 50 → 100 → 200 VUs over 10 min  
- Non-stream requests  
- Thresholds: `http_req_failed < 1%`, `p(99)<30000`  

### 3. Concurrent streams (`streaming-concurrent.js`) — Phase 5

- 50–500 concurrent `stream: true` connections  
- Validate: `Content-Type` includes `text/event-stream`; chunks arrive &lt; 2s apart on mock  
- Monitor `gateway_active_streams` gauge  

### 4. Rate limit storm (`rate-limit-storm.js`) — Phase 5

- Single API key exceeds RPM  
- Expect 429 with `code: rate_limit_exceeded`  
- No 5xx under rejection load  

### 5. Soak test — Phase 5 (manual)

- 4 hours at 70% of max RPS  
- Memory stable (no unbounded growth)  
- Connection pool not exhausted  

### 6. Gateway overhead comparison — Phase 5

- k6 hits **direct mock upstream** vs **via gateway** same path  
- Record delta at p50/p95/p99  

---

## k6 thresholds (`perf/k6/thresholds.json`)

Canonical keys in repo:

| Key | Use |
|-----|-----|
| `smoke` | Phase 2+ CI smoke |
| `ga_inference_rps` | Phase 5 `inference-rps.js` |
| `ga_streaming` | Phase 5 `streaming-concurrent.js` |
| `ga` | Alias of `ga_inference_rps` (scripts may reference either) |

See `perf/k6/thresholds.json` for values; scripts should use the named keys above.

---

## Micro-benchmarks (optional)

| Target | Hypothesis to validate |
|--------|------------------------|
| `ModelRegistryService.TryGetModel` | &lt; 100 ns after warm cache |
| `ExtractModelFromBody` | Utf8JsonReader faster than full DOM for large bodies |
| Error writer | Pre-serialized templates |

Run in `perf/benchmarks/` with BenchmarkDotNet; not blocking CI initially.

---

## Observability during load tests

- Scrape `/metrics` every 15s during k6  
- Capture Grafana dashboard PDF in `perf/reports/{date}-load.pdf`  
- Enable OTel in staging to verify trace sampling under load  

---

## Phase schedule

| Phase | Performance activity |
|-------|------------------------|
| **1** | Add `perf/` folder structure; CI job stub (commented) |
| **2** | `smoke.js` + mock upstream in Integration.Tests; first baseline doc |
| **3** | Auth overhead micro-benchmark (optional) |
| **4** | Metrics validation under medium k6 (50 VU) |
| **5** | Full GA suite + soak + report + SLO sign-off |

---

## Acceptance (Phase 5 GA)

- [ ] `smoke.js` passes in CI on `main`  
- [ ] `inference-rps.js` meets `thresholds.json` on staging  
- [ ] `streaming-concurrent.js` passes concurrency target  
- [ ] No memory leak in 4h soak (document methodology)  
- [ ] Gateway overhead report published in `perf/reports/`  
- [ ] Runbook updated with max supported RPS per instance size  

---

## Risks

| Risk | Mitigation |
|------|------------|
| Mock upstream unrealistic | Staging run against real vLLM before GA |
| k6 SSE validation flaky | Use k6 `text` checks + server-side chunk metrics |
| CI resource limits | Nightly staging pipeline, not every PR |
