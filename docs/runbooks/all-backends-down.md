# Runbook: All backends unhealthy

## Symptoms

- `GET /health` or `GET /admin/api/backends` shows all models **unhealthy**
- Clients receive **502** / `backend_unhealthy` on inference
- Alert `GatewayAllBackendsDown` (if configured)

## Checks

1. `GET /admin/api/backends` — URL and health per model  
2. `GET /admin/api/config/status` — model count and paths  
3. From gateway pod/network: `curl` each upstream `models.json` / health URL  
4. Verify `config/models.json` (or live registry) points to reachable hosts  

## Common causes

| Cause | Action |
|-------|--------|
| Upstream process stopped | Restart vLLM / mock / WireMock |
| Wrong URL in registry | Fix `url` via admin UI or `PATCH /admin/api/models/{id}` |
| Network / DNS in K8s | Check Service names, `host.docker.internal` only applies in Compose |
| TLS mismatch | Set `Gateway:Resilience:ValidateUpstreamTls` appropriately |
| Health probe too aggressive | Review backend health store thresholds |

## Mitigation

1. Restore at least one healthy backend for a critical model.  
2. `POST /admin/api/config/reload` after fixing file-based registry.  
3. Temporarily remove broken models: `DELETE /admin/api/models/{id}`.  
4. Scale is not the fix if all backends are down — fix upstream first.

## Escalation

Include registry snapshot (`GET /admin/api/models`) and recent errors from `GET /admin/api/summary`.
