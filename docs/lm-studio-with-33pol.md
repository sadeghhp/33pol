# Use LM Studio with 33pol (step-by-step)

This guide walks you through running **33pol in Docker** and routing OpenAI-style API traffic to **LM Studio** on your Mac or PC. Your apps talk to 33pol on port **8080**; 33pol forwards requests to LM Studio on the host.

## What you will have when done

```text
Your app / curl / OpenAI SDK
        │
        ▼  http://localhost:8080/v1/...
   ┌─────────────┐
   │ 33pol       │  (Docker)
   │ gateway     │
   └──────┬──────┘
          │  http://host.docker.internal:1234/v1/...
          ▼
   ┌─────────────┐
   │ LM Studio   │  (on your machine)
   │ local API   │
   └─────────────┘
```

| URL | Purpose |
|-----|---------|
| http://localhost:8080 | Gateway (inference + admin) |
| http://localhost:8080/admin | Browser admin UI |
| http://127.0.0.1:1234 | LM Studio API (host only) |

---

## Before you start

| Requirement | Notes |
|-------------|--------|
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or Docker Engine + Compose v2) | `docker compose version` should work |
| [LM Studio](https://lmstudio.ai/) | With a model downloaded |
| This repository cloned | e.g. `git clone …` then `cd 33pol` |
| ~5 minutes | First `docker compose up --build` downloads images |

**Important networking rule:** When 33pol runs **inside Docker**, it cannot reach your host LLM at `localhost` or `127.0.0.1`. Use **`http://host.docker.internal:1234`** in the 33pol model registry instead.

---

## Step 1 — Start the 33pol stack

From the **repository root**:

```bash
cp .env.example .env   # includes COMPOSE_PROFILES=full for mock + Grafana stack
docker compose up -d --build
```

Wait until the gateway is healthy (about 30–60 seconds on first build):

```bash
curl -s http://localhost:8080/health/live
```

You should see JSON with `"status":"Healthy"` (or similar).

Optional check:

```bash
bash perf/ci/verify-compose-health.sh
```

**Defaults you will use later:**

| Setting | Default value |
|---------|----------------|
| Gateway URL | `http://localhost:8080` |
| Admin API key | `sk-33pol-dev-admin-key` (from `.env` → `GATEWAY_ADMIN_API_KEY`) |

Use **`http://`**, not `https://`, unless you added TLS yourself.

---

## Step 2 — Start LM Studio’s local API server

1. Open **LM Studio**.
2. Load a model (e.g. a small instruct model for testing).
3. Open the **Developer** tab (or **Local Server**, depending on your LM Studio version).
4. Turn **Start server** on (default port is usually **1234**).
5. In server settings, enable **Serve on Local Network** (wording may vary).

   Without this, LM Studio often listens only on loopback and **Docker cannot connect**, even with `host.docker.internal`.

### Verify LM Studio on the host (not through 33pol yet)

```bash
curl -s http://127.0.0.1:1234/v1/models
```

You should get JSON listing at least one model. Note the **`id`** of the model you want (e.g. `qwen2.5-7b-instruct-1m-gguf`). You will use that name when talking to 33pol.

---

## Step 3 — Register LM Studio in 33pol (Admin UI)

1. Open **http://localhost:8080/admin** in your browser.
2. Paste the **admin** API key: `sk-33pol-dev-admin-key` (unless you changed it in `.env`).
3. Go to the **Models** tab.
4. Under **Add model**, fill in:

| Field | Example | Notes |
|-------|---------|--------|
| **Model ID** | Same as LM Studio’s model `id` from Step 2 | 33pol forwards this name to LM Studio in the request body |
| **Upstream URL** | `http://host.docker.internal:1234` | Base URL only — **do not** add `/v1` |
| **Max context** | `8192` (or your model’s limit) | Shown in `GET /v1/models` |
| **Aliases** | `local-llm, my-chat` (optional) | Extra names clients can use in the `"model"` field |

5. Click **Add model**.

6. Open the **Backends** tab → **Refresh**. Status should be **healthy** if LM Studio is running and reachable.

Changes are saved to `deploy/docker/config/models.json` on your machine.

### Alternative: edit `models.json` by hand

Edit `deploy/docker/config/models.json`:

```json
{
  "models": [
    {
      "id": "mock-gpt",
      "url": "http://mock-upstream:8080",
      "maxContextLength": 8192,
      "aliases": ["gpt-mock", "mock"]
    },
    {
      "id": "YOUR_LM_STUDIO_MODEL_ID",
      "url": "http://host.docker.internal:1234",
      "maxContextLength": 8192,
      "aliases": ["local-llm"]
    }
  ]
}
```

Then in the admin UI **Dashboard** → **Reload config file**.

---

## Optional — Public access (no 33pol API key)

For clients that always send a dummy API key, enable **Allow use without 33pol API key** when adding/editing the model in the admin UI (sets `"publicAccess": true` in `models.json`). Inference to that model then accepts any placeholder bearer token or no key at all; rate limits still apply.

A key 33pol *issued* and has since revoked or expired is still rejected, so this is not a way to keep a retired key working. If you run the gateway behind a reverse proxy, also set `Gateway:ForwardedHeaders` (see [security.md](security.md)) — otherwise every keyless caller shares one rate-limit bucket.

## Step 4 — Create an inference API key

The bootstrap admin key (`sk-33pol-dev-admin-key`) works for **`/admin` only**. OpenAI endpoints under **`/v1/*`** need an **Inference** (or **Both**) key unless the target model has `publicAccess: true`.

1. In the admin UI, go to **API keys**.
2. **Create key** → Role: **Inference**.
3. **Copy the secret immediately** (shown once).
4. Click **Models** on that key and select which registry models it may use, then **Save**. New keys have **no model access** until you assign at least one model.

Example:

```bash
export GATEWAY_KEY="sk-xxxxxxxx"   # paste your new inference key
```

---

## Step 5 — List models through 33pol

Do **not** open `http://localhost:8080/v1/models` in the browser address bar alone — the browser will not send your API key.

```bash
curl -s http://localhost:8080/v1/models \
  -H "Authorization: Bearer $GATEWAY_KEY"
```

You should see only models assigned to that key in **Models** (not the full registry).

---

## Step 6 — Send a chat completion

Replace `YOUR_LM_STUDIO_MODEL_ID` with the **Model ID** you registered (or an alias):

```bash
curl -s http://localhost:8080/v1/chat/completions \
  -H "Authorization: Bearer $GATEWAY_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_LM_STUDIO_MODEL_ID",
    "messages": [{"role": "user", "content": "Say hello in one sentence."}],
    "max_tokens": 64
  }'
```

Streaming:

```bash
curl -sN http://localhost:8080/v1/chat/completions \
  -H "Authorization: Bearer $GATEWAY_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "YOUR_LM_STUDIO_MODEL_ID",
    "messages": [{"role": "user", "content": "Count to three."}],
    "stream": true
  }'
```

---

## Step 7 — Use the OpenAI Python SDK

```bash
pip install openai
```

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:8080/v1",
    api_key="sk-your-inference-key",  # Inference key from Step 4
)

response = client.chat.completions.create(
    model="YOUR_LM_STUDIO_MODEL_ID",  # or an alias you configured
    messages=[{"role": "user", "content": "Hello from 33pol + LM Studio"}],
)
print(response.choices[0].message.content)
```

Environment variables (optional):

```bash
export OPENAI_BASE_URL=http://localhost:8080/v1
export OPENAI_API_KEY=sk-your-inference-key
```

More client examples: [integrations.md](./integrations.md).

---

## How it works (short)

1. Your client sends `Authorization: Bearer <inference-key>` to 33pol.
2. 33pol validates the key (against the embedded SQLite database in the Docker stack).
3. The JSON `"model"` field is matched to an entry in the registry (`models.json`).
4. 33pol proxies the request to the **Upstream URL** (`host.docker.internal:1234`) with an OpenAI-compatible path (`/v1/chat/completions`, etc.).
5. LM Studio runs inference and returns the response; 33pol passes it back to the client.

The built-in **mock-gpt** backend (`http://mock-upstream:8080`) stays available for tests without LM Studio.

---

## Troubleshooting

| Symptom | Likely cause | What to do |
|---------|----------------|------------|
| `invalid_api_key` on `/v1/models` | No key, wrong key, or admin key used | Create an **Inference** key (Step 4); use `Authorization: Bearer …` |
| Browser shows auth error on `/v1/models` | Browser does not send API keys | Use `curl` or the Python SDK, not the address bar alone |
| `https://localhost:8080` fails | Gateway serves HTTP only | Use `http://localhost:8080` |
| Admin **Add model** fails (500 / busy) | Old image or bad mount | `docker compose up -d --build --force-recreate gateway` |
| Backend **unhealthy** | LM Studio off or not on network | Start server; enable **Serve on Local Network** |
| Chat 502 / upstream error from Docker | Used `localhost` in upstream URL | Use `http://host.docker.internal:1234` |
| LM Studio rejects model name | Registry **Model ID** ≠ LM Studio model id | Set Model ID to match `curl http://127.0.0.1:1234/v1/models` |
| Empty or slow first reply | Model loading in LM Studio | Wait until the model is fully loaded in LM Studio |

### Quick diagnostic commands

```bash
# 33pol alive
curl -s http://localhost:8080/health/live

# LM Studio alive (host)
curl -s http://127.0.0.1:1234/v1/models

# 33pol → registry (needs inference key)
curl -s http://localhost:8080/v1/models -H "Authorization: Bearer $GATEWAY_KEY"

# Admin registry (admin key)
curl -s http://localhost:8080/admin/api/backends \
  -H "X-API-Key: sk-33pol-dev-admin-key"
```

---

## Stop the stack

From the repo root:

```bash
docker compose down
```

To remove database volumes as well:

```bash
docker compose down -v
```

---

## Related docs

- [deploy/docker/README.md](../deploy/docker/README.md) — full Docker stack
- [admin-ui.md](./admin-ui.md) — admin browser UI
- [integrations.md](./integrations.md) — OpenAI SDK, LangChain, LiteLLM
- [README.md](../README.md) — project overview
