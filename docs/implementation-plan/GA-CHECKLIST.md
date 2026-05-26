# 33pol v2 — GA Checklist

**Release:** 2.0.0  
**Sign-off:** _pending implementation_

---

## Phase completion

| Phase | Exit criteria met | Sign-off | Date |
|-------|-------------------|----------|------|
| P1 Platform | [ ] | | |
| P2 Data plane | [ ] | | |
| P3 Security & resilience | [ ] | | |
| P4 Policy & observability | [ ] | | |
| P5 FinOps & GA | [ ] | | |

---

## Functional

- [ ] OpenAI SDK (Python) chat completion against gateway  
- [ ] Streaming SSE chat completion  
- [ ] Embeddings path  
- [ ] Model aliases and canonical rewrite  
- [ ] Hot reload `models.json` (authenticated)  
- [ ] API key create/revoke (admin)  
- [ ] Rate limit returns 429 with stable `code`  
- [ ] Quota enforcement (hard and/or soft)  
- [ ] FinOps usage export  
- [ ] (Optional) Operator console WP4.9 complete **or** explicitly deferred with sign-off — HTTP admin required either way ([08-operator-console.md](./08-operator-console.md))

---

## Quality

- [ ] `dotnet test` green on `main`  
- [ ] Coverage ≥ targets in [02-testing-strategy.md](./02-testing-strategy.md)  
- [ ] No critical/high vulnerabilities in dependencies  
- [ ] Architecture tests pass  

---

## Performance

- [ ] k6 `smoke.js` CI green  
- [ ] k6 `inference-rps.js` meets thresholds on staging  
- [ ] k6 `streaming-concurrent.js` meets thresholds  
- [ ] Gateway overhead report in `perf/reports/`  
- [ ] Soak test completed (4h) without memory growth  

---

## Observability

- [ ] Prometheus scrape successful  
- [ ] Grafana dashboard imported  
- [ ] Alert rules fire in test (amtool/promtool validate)  
- [ ] OTel traces end-to-end in staging  
- [ ] Runbooks exist for top 5 alerts  

---

## Security

- [ ] No anonymous admin endpoints  
- [ ] API keys stored hashed only  
- [ ] TLS validation on upstream (production config)  
- [ ] CORS restricted in production  
- [ ] Secrets not in repository  

---

## Deployment

- [ ] Docker image builds and runs  
- [ ] Helm install on test cluster  
- [ ] Compose stack brings up gateway + Postgres + Prometheus + Grafana  
- [ ] Liveness/readiness probes configured  

---

## Documentation

- [ ] `README.md` quick start  
- [ ] `docs/errors.md` complete  
- [ ] `docs/integrations.md` complete  
- [ ] `docs/observability.md` complete  
- [ ] `docs/finops.md` complete  
- [ ] `docs/security.md` complete  

---

## Approvals

| Role | Name | Date |
|------|------|------|
| Engineering | | |
| Operations | | |
| Product | | |
