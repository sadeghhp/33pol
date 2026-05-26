# Docker Compose — local 33pol stack

Runs **Postgres**, **Prometheus**, **Grafana**, and a **WireMock** OpenAI-compatible mock upstream. The optional **gateway** profile builds `33pol.App` (Phase 1 host: `/health/live`, `/`; OpenAI proxy routing is Phase 2+).

## Prerequisites

- Docker Engine 24+ with Compose v2 (`docker compose`)
- .NET 10 SDK (only when building the `gateway` profile)

## Quick start (observability + mock upstream)

From this directory:

```bash
cp .env.example .env
docker compose up -d
```

| Service        | URL / port (defaults)        |
|----------------|------------------------------|
| Mock upstream  | http://localhost:18080       |
| Prometheus     | http://localhost:9090        |
| Grafana        | http://localhost:3000 (admin / admin) |
| PostgreSQL     | localhost:5432               |

Test the mock:

```bash
curl -s http://localhost:18080/v1/models
curl -s -X POST http://localhost:18080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d "{\"model\":\"mock-gpt\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"
```

## Gateway profile

When the Phase 1 host is enough (health + root metadata; `/metrics` and inference in later phases):

```bash
docker compose --profile gateway up -d --build
```

Gateway: http://localhost:8080  
`models.json` is mounted from `config/models.json` and points at `http://mock-upstream:8080`.

`host.docker.internal` is configured so backends running on the host machine are reachable from the gateway container.

**Operator console:** The in-process Spectre.Console operator console is **disabled by default** in the gateway container (`Gateway:OperatorConsole:Enabled=false`). Production and Compose ops should use HTTP `/admin/api/*` and Grafana. For local experiments with a TTY only, see [docs/implementation-plan/08-operator-console.md](../../docs/implementation-plan/08-operator-console.md).

## Layout

| Path | Purpose |
|------|---------|
| `docker-compose.yml` | Service definitions |
| `config/models.json` | Registry sample (volume-mounted into gateway) |
| `config/prometheus.yml` | Scrape config (gateway job when profile is active) |
| `wiremock/` | Mock upstream mappings |
| `../grafana/` | Provisioning + dashboard JSON (Phase 4+) |
| `../prometheus/alerts/` | Alert rules for `promtool` / Prometheus |

## Stop and reset

```bash
docker compose down
docker compose down -v   # also removes volumes
```
