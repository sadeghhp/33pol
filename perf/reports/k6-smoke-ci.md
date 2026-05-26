# k6 smoke — CI reference

The GitHub Actions job **k6-smoke** runs `perf/ci/run-smoke.sh` after `dotnet build`:

- Mock upstream: `perf/scripts/mock-upstream.py` (port 18080)
- Gateway: `config/models.ci.json`, no database (auth off)
- k6: `perf/k6/scripts/smoke.js` with `SMOKE_DURATION=30s` in CI

## Local reproduction

```bash
dotnet build src/33pol.App/33pol.App.csproj -c Release
# install k6: https://grafana.com/docs/k6/latest/set-up/install-k6/
bash perf/ci/run-smoke.sh
```

## GA soak (manual, staging)

Run on staging with a valid inference API key when auth is enabled:

```bash
export BASE_URL=https://staging.example
export API_KEY=sk-...
export SOAK_DURATION=4h
export SOAK_VUS=5
k6 run perf/k6/scripts/soak.js
```

Record results under `perf/reports/` and sign off **GA checklist → Soak test**.
