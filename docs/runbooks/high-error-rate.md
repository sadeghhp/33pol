# Runbook: High gateway error rate

## Symptoms

- Prometheus alert `GatewayHighErrorRate` or elevated `gateway_inference_errors_total`
- Clients see 502/503 from `/v1/chat/completions`

## Checks

1. `GET /admin/api/summary` — error count and per-model breakdown
2. `GET /admin/api/backends` — unhealthy backends
3. `GET /health` — aggregate backend status
4. Grafana dashboard `33pol-gateway` — RED panels

## Common causes

| Cause | Action |
|-------|--------|
| All backends down | Restore upstream; verify `models.json` URLs |
| Circuit open | Wait for half-open or reduce upstream failures |
| Gateway draining | New deploy in progress; wait for ready |
| Rate limit storm | Expected 429; tune limits or client backoff |

## Mitigation

- Reload registry: `POST /admin/api/config/reload` (admin key)
- Remove bad model: `DELETE /admin/api/models/{id}`
- Scale replicas (Helm `autoscaling.enabled`) if CPU-saturated

## Escalation

Capture `X-Request-Id` from failing responses and correlate with structured logs (Serilog + trace id).
