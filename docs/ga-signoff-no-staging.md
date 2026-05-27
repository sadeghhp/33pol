# GA sign-off without a staging environment

Use this path when **no staging URL** exists (solo dev, pre-prod only). It does **not** replace production capacity sign-off; it closes **2.0 code-complete** gates on **Docker Compose** with documented exceptions.

## Run (repo root, stack up)

```bash
docker compose up -d --build
bash perf/ci/verify-compose-health.sh
bash perf/ci/run-compose-e2e.sh
python3 perf/scripts/sdk-smoke.py   # OPENAI_BASE_URL, OPENAI_API_KEY, MODEL=mock-gpt
bash perf/ci/run-ga-compose-k6.sh
bash perf/ci/run-soak-local.sh      # default SOAK_DURATION=10m (not 4h)
dotnet test 33pol.sln -c Release
```

Record results in `perf/reports/ga-local-YYYY-MM-DD.md`.

## Gate mapping (G-01–G-09)

| Gate | Local substitute | Staging still needed later? |
|------|------------------|----------------------------|
| G-01 k6 GA | `run-ga-compose-k6.sh` (short VUs/duration) | Yes — full `k6-ga-staging.yml` before prod load sign-off |
| G-02 soak | `run-soak-local.sh` (default 10m) | Yes — 4h soak on prod-like env |
| G-03 SDK | `sdk-smoke.py` on Compose | Optional re-run on staging |
| G-04 Compose | `run-compose-e2e.sh` | No (same stack) |
| G-05 approvals | Fill [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md) | Names/dates required |
| G-06 OTel traces | **Waiver** — gateway exports **metrics** via `/metrics` + Prometheus/Grafana in Compose; **OTLP traces not wired** in host yet | Yes when trace export is added |
| G-07 registry poll | Edit `deploy/docker/config/models.json`, verify ≤3s | Optional |
| G-08 FinOps export | `GET /admin/api/usage/export?format=csv` | Optional |
| G-09 console | Defer — console disabled in Compose | Optional |

## Observability (G-06 waiver)

Verified locally:

- `curl -sf http://localhost:8080/metrics | head`
- Prometheus `http://localhost:9090/-/healthy` scrapes gateway
- Grafana dashboards **33pol Gateway** and **33pol Gateway — Traffic & tokens** load (`perf/ci/verify-grafana-dashboards.sh`)

Not verified (deferred): end-to-end **trace** export to an OTLP collector. Sample collector config: [deploy/otel-collector/config.yaml](../deploy/otel-collector/config.yaml).

## When staging exists later

1. Run [k6-ga-staging.yml](../.github/workflows/k6-ga-staging.yml) with `base_url` + `model` + `api_key`.
2. `SOAK_DURATION=4h k6 run perf/k6/scripts/soak.js` on staging.
3. OTel trace smoke after OTLP tracing is enabled on the gateway.
4. Append to `perf/reports/ga-staging-*.md` — do not delete local report.
