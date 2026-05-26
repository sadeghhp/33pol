# 33pol

OpenAI-compatible, high-performance LLM gateway for .NET 10.

## Status

**Planning phase** — implementation has not started. See the v2 implementation plan:

- [implementation-plan/README.md](./implementation-plan/README.md)

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
