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
| Grafana        | http://localhost:3000 (admin / admin) — folder **33pol**: [33pol Gateway](http://localhost:3000/d/33pol-gateway/33pol-gateway) (RED, backends), [Traffic & tokens](http://localhost:3000/d/33pol-gateway-traffic/33pol-gateway-traffic) — see [observability.md](../../docs/observability.md) |
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

The whole `deploy/docker/config/` directory is mounted at `/app/config` (including `models.json`) so the Admin UI (**Models** tab) can persist registry changes. A single-file bind mount cannot be atomically replaced on Docker Desktop (EBUSY).

### OpenRouter (cloud)

1. Add to `.env` (optional): `OPENROUTER_API_KEY=sk-or-...` — passed into the gateway container when set.
2. Restart stack: `docker compose up -d --build`
3. Admin UI → **Models** → **Fetch OpenRouter models** → **Use** → **Add model** (upstream auth is prefilled).
4. Create an **Inference** API key; clients use the gateway URL + that key (not the OpenRouter key).

See [docs/integrations.md](../../docs/integrations.md#openrouter).

### LM Studio (host LLM)

Full walkthrough (LM Studio setup, admin UI, inference keys, curl + Python, troubleshooting):

**[docs/lm-studio-with-33pol.md](../../docs/lm-studio-with-33pol.md)**

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
