# GA sign-off guide

Operational steps to close [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md) after code-complete Phase 5.

## 1. Local / CI (automated)

```bash
dotnet test 33pol.sln -c Release
bash perf/ci/run-smoke.sh
```

CI on `main`: `build-test`, `k6-smoke`, dependency vulnerability audit.

**Production release:** after GA criteria are met, follow [release.md](./release.md) (tag `v*`, GHCR semver image, GitHub Release tarball).

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

With gateway running (auth off or valid inference key):

```bash
pip install openai
export OPENAI_BASE_URL=http://localhost:8080/v1
export OPENAI_API_KEY=sk-your-key   # inference key when Postgres is enabled
export MODEL=gpt-local              # or mock-gpt when using deploy/docker Compose
python3 perf/scripts/sdk-smoke.py
```

**Compose note:** WireMock upstream does not emit SSE chunks; for streaming step 3 use `perf/scripts/mock-upstream.py` or record a sign-off exception. See [perf/reports/ga-local-2026-05-27.md](../perf/reports/ga-local-2026-05-27.md).

## 4. Docker Compose

```bash
cp .env.example .env && docker compose up -d --build
bash perf/ci/verify-compose-health.sh
bash perf/ci/run-compose-e2e.sh   # G-04 formal sign-off (health + inference + stream)
```

## 5. Dependencies

```bash
dotnet list 33pol.sln package --vulnerable --include-transitive
```

Pinned in `Directory.Packages.props`: `OpenTelemetry.Api` 1.15.3, `System.Security.Cryptography.Xml` 10.0.8.

## 6. Approvals

Fill the Approvals table in [GA-CHECKLIST.md](./implementation-plan/GA-CHECKLIST.md), then close Taiga epic **EPIC-P5-finops-ga**.
