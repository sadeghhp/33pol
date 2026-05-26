# 33pol

OpenAI-compatible, high-performance LLM gateway for .NET 10.

## Status

**Phase 1 complete** — solution skeleton, core stubs, host, CI (GitHub Actions), test pyramid + NetArchTest. **Next: Phase 2** (registry, proxy, data plane). Implementation plan:

- [docs/implementation-plan/README.md](./docs/implementation-plan/README.md)
- [Phase 1 — platform foundation](./docs/implementation-plan/phases/phase-1-platform-foundation.md)

## Build and test

```bash
dotnet build 33pol.sln
dotnet test 33pol.sln
dotnet test 33pol.sln -c Release --collect:"XPlat Code Coverage"
dotnet run --project src/33pol.App
```

Health: `GET http://localhost:5080/health/live`

CI runs on push/PR to `main` (see [.github/workflows/ci.yml](./.github/workflows/ci.yml)).

## Local stack (Docker Compose)

```bash
cd deploy/docker
cp .env.example .env
docker compose up -d
```

See [deploy/docker/README.md](./deploy/docker/README.md) for ports, mock upstream, and the optional gateway profile.

## References

- v1 behavior specification: [docs/old-version/](./docs/old-version/)
- Performance test plan: [perf/README.md](./perf/README.md) — k6 thresholds in [perf/k6/thresholds.json](./perf/k6/thresholds.json)
- Deployment layout: [deploy/README.md](./deploy/README.md)
- Testing strategy: [docs/implementation-plan/02-testing-strategy.md](./docs/implementation-plan/02-testing-strategy.md)
