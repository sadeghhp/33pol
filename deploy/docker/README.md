# Docker Compose — local 33pol stack

Runs **gateway**, **Postgres**, **Prometheus**, **Grafana**, and a **WireMock** OpenAI-compatible mock upstream in one command.

## Prerequisites

- Docker Engine 24+ with Compose v2 (`docker compose`)
- No local .NET SDK required (gateway image is built by Compose)

## Quick start (recommended — repo root)

From the repository root:

```bash
cp .env.example .env
docker compose up -d --build
bash perf/ci/verify-compose-health.sh
```

| Service        | URL / port (defaults)        |
|----------------|------------------------------|
| Gateway        | http://localhost:8080        |
| Admin UI       | http://localhost:8080/admin (key from `GATEWAY_ADMIN_API_KEY`) |
| Mock upstream  | http://localhost:18080       |
| Prometheus     | http://localhost:9090        |
| Grafana        | http://localhost:3000 (admin / admin) — **33pol Gateway** dashboard auto-provisioned |
| PostgreSQL     | localhost:5432               |

Test the mock:

```bash
curl -s http://localhost:18080/v1/models
curl -s -X POST http://localhost:18080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d "{\"model\":\"mock-gpt\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"
```

Test the gateway (via registry → mock):

```bash
curl -s http://localhost:8080/health/live
curl -s http://localhost:8080/v1/models -H "Authorization: Bearer <api-key>"
```

`models.json` is mounted from `deploy/docker/config/models.json` and points at `http://mock-upstream:8080`.

`host.docker.internal` is configured so backends running on the host machine are reachable from the gateway container.

**Operator console:** Disabled in the gateway container (`Gateway:OperatorConsole:Enabled=false`). Use HTTP `/admin` and Grafana. For a TTY-only local experiment, see [docs/implementation-plan/08-operator-console.md](../../docs/implementation-plan/08-operator-console.md).

## Alternate path (this directory)

Equivalent stack when you prefer to run Compose from `deploy/docker/`:

```bash
cd deploy/docker
cp .env.example .env
docker compose up -d --build
```

## Layout

| Path | Purpose |
|------|---------|
| `docker-compose.yml` | Service definitions (also included from repo root) |
| `config/models.json` | Registry sample (volume-mounted into gateway) |
| `config/prometheus.yml` | Scrape config (gateway job) |
| `wiremock/` | Mock upstream mappings |
| `../grafana/` | Provisioning + dashboard JSON (Phase 4+) |
| `../prometheus/alerts/` | Alert rules for `promtool` / Prometheus |
| `../../docker-compose.yml` | Root entry point (`include` of this file) |

## Stop and reset

```bash
docker compose down
docker compose down -v   # also removes volumes
```

Run from repo root or `deploy/docker/` depending on where you started the stack.
