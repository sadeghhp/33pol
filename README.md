# 33pol

OpenAI-compatible, high-performance LLM gateway for .NET 10.

## Status

**Phases 1–5 code-complete**; **GA sign-off pending** (staging perf, SDK smoke run, Compose E2E, approvals). See [docs/implementation-plan/README.md](./docs/implementation-plan/README.md), [gap report](./docs/implementation-plan-gap-report.md), and [GA checklist](./docs/implementation-plan/GA-CHECKLIST.md).

## Quick start (local)

```bash
dotnet build 33pol.sln
dotnet test 33pol.sln -c Release

# Terminal 1 — mock upstream
python3 perf/scripts/mock-upstream.py

# Terminal 2 — gateway (no DB → auth off for local smoke)
export ASPNETCORE_ENVIRONMENT=Development
export Gateway__ModelsConfigPath=config/models.ci.json
export Gateway__OperatorConsole__Enabled=false
export ConnectionStrings__GatewayDb=
dotnet run --project src/33pol.App --urls http://localhost:8080

# Terminal 3 — smoke test (requires [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/))
bash perf/ci/run-smoke.sh
```

- Health: `GET http://localhost:8080/health/live`
- Admin UI: `http://localhost:8080/admin` (set `Gateway:Bootstrap:AdminApiKey` when using Postgres)
- CI: `build-test` + `k6-smoke` on PR/main; [docker-image](./.github/workflows/docker-image.yml) on `main`; [k6 nightly](./.github/workflows/k6-nightly.yml)
- Integrations: [docs/integrations.md](./docs/integrations.md), [deploy/README.md](./deploy/README.md)
- GA sign-off: [docs/ga-signoff.md](./docs/ga-signoff.md), [GA checklist](./docs/implementation-plan/GA-CHECKLIST.md)

## Build and test

```bash
dotnet build 33pol.sln
dotnet test 33pol.sln -c Release
dotnet test 33pol.sln -c Release --collect:"XPlat Code Coverage"
bash build/check-coverage.sh TestResults
```

## Local stack (Docker Compose)

All-in-one stack (gateway, Postgres, mock upstream, Prometheus, Grafana):

```bash
cp .env.example .env
docker compose up -d --build
bash perf/ci/verify-compose-health.sh
```

See [deploy/docker/README.md](./deploy/docker/README.md) for ports, admin key, and service details.

## References

- v1 behavior specification: [docs/old-version/](./docs/old-version/)
- Performance test plan: [perf/README.md](./perf/README.md) — k6 thresholds in [perf/k6/thresholds.json](./perf/k6/thresholds.json)
- Deployment layout: [deploy/README.md](./deploy/README.md)
- Testing strategy: [docs/implementation-plan/02-testing-strategy.md](./docs/implementation-plan/02-testing-strategy.md)
