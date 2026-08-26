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
| Circuit open | Wait for half-open or reduce upstream failures. A half-open probe holds the model shut only until `Gateway:Resilience:CircuitBreakerHalfOpenProbeTimeoutSeconds` (default 30) elapses; a "permit was reclaimed" warning in the log means probes are running longer than that |
| Backend unhealthy while the model server is merely busy | The sweep marks a backend down after `Gateway:HealthCheckUnhealthyThreshold` (default 2) consecutive failed probes at `Gateway:HealthCheckTimeoutSeconds` (default 15) each. A saturated model server answers `/v1/models` slowly, so raise the timeout or the threshold before suspecting the backend |
| Gateway draining | New deploy in progress; wait for ready |
| Rate limit storm | Expected 429; tune limits or client backoff |

## Slow, not failing

`48 in flight · 4 streaming · 44 buffered` with no *queued* suffix on the Overview means the bulkhead is not the constraint: those requests are waiting on the model server, not on the gateway. Check the upstream's own queue depth (vLLM's scheduler, `OLLAMA_MAX_QUEUE`) before tuning anything here. The gateway's job in that state is to degrade gracefully rather than to start refusing traffic — the breaker and health thresholds above are what decide which of the two happens.

## Mitigation

- Reload registry: `POST /admin/api/config/reload` (admin key)
- Remove bad model: `DELETE /admin/api/models/{id}`
- Scale **vertically** (raise CPU/memory `resources`) if saturated. The gateway runs single-instance on embedded SQLite; horizontal scaling / `autoscaling.enabled` is rejected by the Helm chart.

## Escalation

Capture `X-Request-Id` from failing responses and correlate with structured logs (Serilog + trace id).
