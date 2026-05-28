# Install profiles

| Profile | `COMPOSE_PROFILES` | Compose services |
|---------|-------------------|------------------|
| `gpu-gateway` | (empty) | `postgres`, `gateway` |
| `full-stack` | `full` | above + `mock-upstream`, `prometheus`, `grafana` |

Set via `./scripts/install-33pol.sh install` or manually in `.env`.
