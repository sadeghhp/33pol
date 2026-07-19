# Install profiles

| Profile | `COMPOSE_PROFILES` | Compose services |
|---------|-------------------|------------------|
| `gpu-gateway` | (empty) | `gateway` (embedded SQLite, no external DB) |
| `gpu-observability` | `observability` | above + `prometheus`, `grafana` |
| `full-stack` | `full` | above + `mock-upstream` (Prometheus/Grafana included via `full`) |

Set via `./scripts/install-33pol.sh install` or manually in `.env`.
