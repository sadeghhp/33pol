# GA sign-off guide

Operational steps to close [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md) after code-complete Phase 5.

## 1. Local / CI (automated)

```bash
dotnet test 33pol.sln -c Release
bash perf/ci/run-smoke.sh
```

CI on `main`: `build-test`, `k6-smoke`, dependency vulnerability audit.

## 2. Staging performance

1. Deploy gateway + mock or vLLM upstream.
2. Run [k6-ga-staging.yml](../.github/workflows/k6-ga-staging.yml) (workflow_dispatch) with staging `base_url` and `model`.
3. Optional 4h soak: `SOAK_DURATION=4h k6 run perf/k6/scripts/soak.js` (see [perf/README.md](../perf/README.md)).
4. Record results in `perf/reports/ga-YYYY-MM-DD.md`.

Local shortened suite (gateway already up):

```bash
bash perf/ci/run-ga-local.sh
bash perf/ci/run-overhead-compare.sh   # requires mock on :18080
```

## 3. OpenAI Python SDK smoke

With gateway running (auth off or valid key):

```bash
pip install openai
export OPENAI_BASE_URL=http://localhost:8080/v1
export OPENAI_API_KEY=sk-your-key
export MODEL=gpt-local
python3 perf/scripts/sdk-smoke.py
```

## 4. Docker Compose

```bash
cd deploy/docker && cp .env.example .env && docker compose up -d
bash perf/ci/verify-compose-health.sh
docker compose --profile gateway up -d --build   # optional full gateway
```

## 5. Dependencies

```bash
dotnet list 33pol.sln package --vulnerable --include-transitive
```

Pinned in `Directory.Packages.props`: `OpenTelemetry.Api` 1.15.3, `System.Security.Cryptography.Xml` 10.0.8.

## 6. Approvals

Fill the Approvals table in [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md), then close Taiga epic **EPIC-P5-finops-ga**.
