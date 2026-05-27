# Grafana Business & Traffic Metrics (Post-GA)

**Story:** Taiga US #591  
**Depends on:** Phase 4 metrics (`GatewayMeters`), Phase 5 Compose/Grafana  
**Out of scope:** Dollar cost, forecasts, billing exports (Admin + `GatewayDb`)

---

## 1. Goals

| In Grafana (real-time) | Out of Grafana |
|------------------------|----------------|
| Traffic RPS, errors, latency, streams | Daily cost rollups |
| **Prompt vs completion token rates** | Rate-card pricing |
| Routing: route, alias → canonical model | Budget hard-stop accounting |
| Policy: rate limits, quota, circuit, bulkhead | `/admin/api/usage/events` history |

---

## 2. Prerequisite gap (fixed in T1)

`IUsageRecorder.Enqueue` was registered but **never called from `33pol.Proxy`**. Token counters and billing persistence require usage capture after upstream response.

---

## 3. Metric catalog (additions)

### Tokens (T2)

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_tokens_total` | Counter | `model`, `direction` = `input` \| `output` \| `total` |

Record on `Enqueue` (low lag). Worker commits quota/DB only.

### Routing (T3)

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_inference_route_total` | Counter | `route` (`chat` \| `completions` \| `embeddings`), `stream` |
| `gateway_model_resolve_total` | Counter | `result` (`resolved` \| `not_found` \| `alias`) |
| `gateway_forward_attempts_total` | Counter | `model`, `outcome` |

Label rules: no raw API keys; prefer `plan_slug` over tenant UUID if tenant labels are added later.

### Resilience (T4)

| Metric | Type | Labels |
|--------|------|--------|
| `gateway_circuit_breaker_state` | Observable gauge | `model` — 0 closed, 1 half_open, 2 open |
| `gateway_circuit_breaker_transitions_total` | Counter | `model`, `to_state` |
| `gateway_bulkhead_rejections_total` | Counter | `model` |

---

## 4. Implementation hooks

| Event | Component |
|-------|-----------|
| Usage JSON / SSE tail | `StreamingHttpTransformer` → `IUsageRecorder` |
| Token counters | `ChannelUsageRecorder.Enqueue` → `GatewayMeters` |
| Route / forward | `ModelRouterMiddleware` |
| Circuit state | `CircuitBreaker` / `ModelCircuitBreakerRegistry` |

**Architecture:** `33pol.Proxy` → `33pol.Core` only. `UsageJsonParser` lives in **Core**; metrics in **Observability**.

---

## 5. Grafana (T5)

| Dashboard | UID | Content |
|-----------|-----|---------|
| 33pol Gateway (existing) | `33pol-gateway` | SRE RED, writer, backends |
| **33pol Gateway — Traffic & tokens** (new) | `33pol-gateway-traffic` | Tokens in/out, routing, policy |

Compose: `deploy/grafana/dashboards/`, provisioned via `deploy/grafana/provisioning/`.

---

## 6. Tasks (Taiga #591)

| Ref | Task |
|-----|------|
| T1 | Wire usage capture on inference hot path |
| T2 | Token metrics `input` / `output` |
| T3 | Routing and forward outcome counters |
| T4 | Circuit breaker and bulkhead metrics |
| T5 | Grafana traffic dashboard |
| T6 | Docs, alerts, integration scrape test |

---

## 7. Verification

```bash
dotnet test 33pol.sln -c Release
promtool check rules deploy/prometheus/alerts/*.yml
# Compose: traffic → Grafana dashboard shows input/output token rates
```

---

## Related

- [12-metrics-and-runtime-contracts.md](./12-metrics-and-runtime-contracts.md)
- [observability.md](../observability.md)
