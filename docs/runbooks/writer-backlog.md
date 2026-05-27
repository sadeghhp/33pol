# Runbook: Usage writer backlog / dropped events

## Symptoms

- Prometheus alert `GatewayUsageWriterQueueHigh` or `GatewayUsageWriterDroppedEvents`
- FinOps rollups lagging; `gateway_usage_writer_queue_depth` elevated
- `gateway_usage_writer_dropped_total` increasing

## Checks

1. Grafana panel: usage writer queue depth and drop rate  
2. `GET /admin/api/summary` — inference still succeeding?  
3. Postgres connectivity and disk space (billing persistence)  
4. Gateway logs for persistence / batch writer errors  

## Common causes

| Cause | Action |
|-------|--------|
| Traffic spike | Scale gateway replicas; writer is per-process — consider lowering burst or enabling drop policy awareness |
| DB slow or unavailable | Fix Postgres; check connection pool and migrations |
| Saturated channel (10k, DropOldest) | Expected under extreme load — increase capacity or add replicas with shared store (post-GA) |
| Misconfigured `ConnectionStrings:GatewayDb` | Fix secret; readiness should fail if DB required |

## Mitigation

1. Restore database health first.  
2. Reduce load temporarily (ingress rate limit, client backoff).  
3. Restart gateway pods after DB recovery (in-flight queue is lost — idempotent `request_id` on usage rows limits duplicates).  
4. Reconcile FinOps from billing events if gaps are unacceptable (operational export / SQL).

## Prevention

- Monitor `deploy/prometheus/alerts/33pol-writer.yml`  
- Load-test writer with `perf/k6/scripts/inference-rps.js` before major launches  
- See [finops.md](../finops.md) for writer hardening notes  
