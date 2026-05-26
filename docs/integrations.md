# 33pol Gateway — Integrations

## OpenAI-compatible clients

Point any OpenAI SDK at the gateway base URL and use your gateway API key.

### Python (openai SDK)

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:8080/v1",
    api_key="sk-your-gateway-key",
)

response = client.chat.completions.create(
    model="gpt-local",
    messages=[{"role": "user", "content": "Hello"}],
)
print(response.choices[0].message.content)
```

Streaming: set `stream=True` on the same call. The gateway preserves SSE semantics end-to-end.

### Environment variables

| Variable | Purpose |
|----------|---------|
| `OPENAI_BASE_URL` | `http://gateway:8080/v1` |
| `OPENAI_API_KEY` | Gateway inference key |

## Kubernetes

- **Helm:** `deploy/helm/33pol/` — set `postgresql.enabled` and secrets for production DB.
- **Probes:** liveness `/health/live`, readiness `/health/ready` (no auth).
- **Multi-replica:** in-memory rate limits are per-pod unless Redis store is configured. Fan out admin registry changes or use a shared `models.json` volume. See [implementation-plan/11-ha-and-scaling.md](./implementation-plan/11-ha-and-scaling.md).

## LangChain / LiteLLM

Use the OpenAI-compatible provider with `openai_api_base` set to `https://<gateway>/v1` and `openai_api_key` set to your gateway key. Model names must exist in the gateway registry (`GET /v1/models`).

## Load testing (k6)

From repo root with mock upstream (Compose or WireMock):

```bash
BASE_URL=http://localhost:8080 MODEL=gpt-local k6 run perf/k6/scripts/smoke.js
BASE_URL=http://localhost:8080 API_KEY=sk-admin k6 run perf/k6/scripts/inference-rps.js
```

See [perf/README.md](../perf/README.md) and [implementation-plan/03-performance-and-load-testing.md](./implementation-plan/03-performance-and-load-testing.md).

## Admin automation

All control-plane operations use `/admin/api/*` with an **admin** API key (`X-API-Key` or Bearer). Prefer HTTP over the operator console for CI/CD.
