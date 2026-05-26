# 33pol

OpenAI-compatible, high-performance LLM gateway for .NET 10.

## Status

**Phase 1 (platform foundation)** — multi-project solution skeleton, core stubs, and minimal host. See:

- [docs/implementation-plan/README.md](./docs/implementation-plan/README.md)

## Build and test

```bash
dotnet build 33pol.sln
dotnet test 33pol.sln
dotnet run --project src/33pol.App
```

Health: `GET http://localhost:5080/health/live`

## Local stack (Docker Compose)

```bash
cd deploy/docker
cp .env.example .env
docker compose up -d
```

See [deploy/docker/README.md](./deploy/docker/README.md) for ports, mock upstream, and the optional gateway profile.

## References

- v1 behavior specification: [docs/old-version/](./docs/old-version/)
- Performance test plan: [perf/README.md](./perf/README.md)
- Deployment layout: [deploy/README.md](./deploy/README.md)
