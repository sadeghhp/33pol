# v1 Parity Specification (normative for v2)

**Baseline:** LLM Gateway v1.2.0 — [`docs/old-version/`](../old-version/)  
**Applies to:** Phase 2 proxy/registry exit criteria; regression tests tagged `V1Parity`  
**Status:** Normative for implementers; deviations require explicit **BREAKING** note in this file.

---

## How to use this document

| Tag | Meaning |
|-----|---------|
| **MUST** | Required for v2 data-plane parity unless marked BREAKING |
| **SHOULD** | Strong default; document reason if omitted |
| **BREAKING** | Intentional v2 change from v1 |
| **v2+** | Not in v1; deferred to later phase |

Full narrative remains in v1 docs; this file is the **acceptance checklist** for Phase 2+.

---

## 1. Inference routes

| Method | Path | v2 behavior |
|--------|------|-------------|
| POST | `/v1/chat/completions` | **MUST** proxy via `IHttpForwarder` |
| POST | `/v1/completions` | **MUST** |
| POST | `/v1/embeddings` | **MUST** |
| GET | `/v1/models` | **MUST** — gateway-generated (Phase 2) |
| GET | `/v1/models/{model}` | **MUST** — alias lookup (Phase 2) |

**BREAKING / out of scope (v2.0 GA):** `/v1/responses`, audio, images, batches, Azure `api-version` shim.

**Upstream path preservation:** Client `POST /v1/chat/completions` → upstream `POST {backendBaseUrl}/v1/chat/completions` (**MUST**).

---

## 2. Passthrough prefixes (router)

Prefix match, case-insensitive → `next()` (do **not** forward):

```text
/health
/health/live
/health/ready
/stats
/metrics
/admin/
/admin/api/
/v1/models
```

**BREAKING:** v1 `/hubs/` (SignalR) removed — use SSE `/admin/api/events/stream` (Phase 4, optional).

v2 **MUST** use `/admin/api/` for control-plane HTTP (v1 used `/admin/reload`, `/admin/status`).

---

## 3. Routable inference (router)

**MUST** be **POST** and path suffix (case-insensitive):

```text
/v1/chat/completions
/v1/completions
/v1/embeddings
```

All other requests → `next()` unless handled by minimal APIs.

---

## 4. Router processing algorithm (Phase 2)

Implement the v1 decision flow ([02-core-proxy-and-routing.md §2.4](../old-version/02-core-proxy-and-routing.md)) with v2 error codes (Phase 3+):

| Step | Condition | Action |
|------|-----------|--------|
| 1 | Passthrough prefix | `next()` |
| 2 | Not routable or not POST | `next()` |
| 3 | — | `Request.EnableBuffering()` **MUST** |
| 4 | JSON parse fails | 400 — Phase 2: `invalid_request_error`; Phase 3: `invalid_json` |
| 5 | `model` missing/empty | 400 — Phase 3: `missing_model` |
| 6 | Registry miss | 404 — Phase 3: `model_not_found` |
| 7 | Backend unhealthy | 502 — Phase 3: `backend_unhealthy` |
| 8 | — | `Body.Position = 0` **MUST** before forward |
| 9 | — | Start request tracking; if `stream==true` increment active-stream gauge |
| 10 | — | `IHttpForwarder.SendAsync` with transformer |
| 11 | Forwarder error | 502 if response not started; record failure |
| 12 | `finally` | Decrement stream gauge; record recent request (Phase 4 `IRecentRequestStore`) |

**Body parsing:** Extract `model` (string) and `stream` (bool, default `false`). Prefer `Utf8JsonReader` in v2; `JsonDocument` acceptable if position rewind is correct.

**Model grant (v2+ Phase 3):** After step 6, before step 7 — `IModelGrantService` → 403 `insufficient_scope` if grant missing.

---

## 5. `HttpMessageInvoker` / `SocketsHttpHandler` (Phase 2)

| Setting | Value | MUST |
|---------|-------|------|
| `UseProxy` | false | ✓ |
| `AllowAutoRedirect` | false | ✓ |
| `AutomaticDecompression` | None | ✓ |
| `UseCookies` | false | ✓ |
| `EnableMultipleHttp2Connections` | true | ✓ |
| `PooledConnectionLifetime` | 10 minutes | ✓ |
| `PooledConnectionIdleTimeout` | 5 minutes | ✓ |
| `ResponseDrainTimeout` | 5 seconds | ✓ |

---

## 6. `StreamingHttpTransformer` (Phase 2)

### Request

- Clear/proxy `Host` header per YARP forwarder rules.
- **Model rewrite (MUST):** If client sent alias ≠ canonical id, rewrite JSON `model` property to canonical id using structured JSON (not naive string replace). Cover spacing variants v1 handled: `"model":"x"` and `"model": "x"`.

### Response (streaming)

When `stream: true`:

- Remove `Content-Length` where appropriate for chunked/SSE.
- `Cache-Control: no-cache`
- `X-Accel-Buffering: no`

---

## 7. `models.json` schema (Phase 2)

**MUST** match v1 ([01-overview §8.2](../old-version/01-overview-and-architecture.md)):

```json
{
  "models": [
    {
      "id": "canonical-model-id",
      "url": "http://backend:8000",
      "maxContextLength": 40960,
      "aliases": ["alias-a"]
    }
  ]
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `id` | Yes | Canonical id for health, metrics, rewrite target |
| `url` | Yes | Base URL (no trailing slash required) |
| `maxContextLength` | No | `0` if omitted; exposed as `max_model_len` on models API |
| `aliases` | No | Case-insensitive lookup keys |

**Load behavior (MUST):**

- Empty `models` array on reload → warn, **do not clear** existing registry.
- Invalid JSON on startup → fail start.
- Invalid JSON on hot reload → keep previous registry; return error to caller.

**v2 improvement (SHOULD):** Hash via SHA256 for reload detection (not `GetHashCode()`).

**Live registry (MUST — v2):** See [13-live-model-registry.md](./13-live-model-registry.md). Summary:

- Admin/console/UI mutations **MUST** persist `models.json` and apply in-memory in one operation.
- Manual file edits **MUST** be picked up via debounced file watch (when enabled) or poll (default interval **2 s** in Production).
- Operators **MUST NOT** need a process restart for new backends to be routable.

---

## 8. `GET /v1/models` response (Phase 2)

**MUST** match v1 shape ([03-api §6](../old-version/03-api-operations-and-observability.md)):

- `object: "list"`, `data[]` with `id`, `object`, `created`, `owned_by`, `permission`, `root`, `parent`.
- Extension fields: `max_model_len` (from `maxContextLength`), `available` (health).
- **List:** only models where backend is **healthy**.
- **Detail `GET /v1/models/{model}`:** return even if unhealthy; `available` reflects health; 404 if unknown; response `id` is **canonical**, not alias.

Synthetic `permission` array (v1 parity) **SHOULD** be preserved for client compatibility.

---

## 9. Admin registry & config reload (Phase 2 open → Phase 3 secured)

**Registry CRUD (Phase 4):** `GET/POST/PATCH/DELETE /admin/api/models` — normative contract in [13-live-model-registry.md](./13-live-model-registry.md) §5. Phase 2 delivers file reload/status and writer foundation only.

### File reload & status (Phase 2+)

### v2 paths

| v1 | v2 |
|----|-----|
| `POST /admin/reload` | `POST /admin/api/config/reload` |
| `GET /admin/status` | `GET /admin/api/config/status` |

### Response bodies (MUST preserve semantics)

**Reload success — 200:**

```json
{
  "status": "success",
  "message": "Configuration reloaded successfully",
  "previousModelCount": 3,
  "currentModelCount": 4,
  "models": ["model-a", "model-b"],
  "timestamp": "2025-12-04T17:39:28Z"
}
```

**Reload failure — 500:**

```json
{
  "status": "error",
  "message": "Failed to reload: <reason>"
}
```

**Reload in progress — 409 or 500 (pick one, document in OpenAPI):**

```json
{
  "status": "error",
  "message": "Reload already in progress"
}
```

v1 used 500 with message; v2 **MAY** use 409 — if so, mark **BREAKING** here.

**Status — 200:**

```json
{
  "hotReloadEnabled": true,
  "watchEnabled": true,
  "lastReload": "2025-12-04T17:39:28Z",
  "modelCount": 4,
  "models": [
    { "id": "...", "url": "...", "aliases": ["..."] }
  ]
}
```

`watchEnabled` reflects `Gateway:RegistryWatchEnabled` (see [13-live-model-registry.md](./13-live-model-registry.md) §3.4).

**Side effects:** Active streaming connections **MUST NOT** be terminated on reload.

---

## 10. Service info `GET /` (Phase 1+)

v1 returned service metadata when keys configured. v2 **SHOULD** expose `GET /` with name, version, and doc links; auth policy per [10-identity-data-model.md](./10-identity-data-model.md) (typically same as inference when keys enabled).

---

## 11. Auth timing (Phase 2 vs 3)

| Phase | Inference | `/v1/models` | Admin reload |
|-------|-----------|--------------|--------------|
| 2 | Open (no DB keys) | Open | Open (**debt**) |
| 3+ | API key **MUST** when configured | API key **MUST** when configured | Admin credential **MUST** |

---

## 12. YARP (MUST NOT)

- **MUST NOT** use `MapReverseProxy` / route-table forwarding for inference.
- **MUST** use `AddHttpForwarder()` + `IHttpForwarder.SendAsync` only.

---

## 13. Phase 2 exit tests

Minimum **V1Parity** integration coverage:

- [ ] POST chat/completions non-stream 200 (mock upstream)
- [ ] POST stream SSE chunks + headers
- [ ] POST embeddings
- [ ] Alias in body rewritten to canonical upstream body
- [ ] Unknown model 404; unhealthy 502
- [ ] `GET /v1/models` golden JSON; unhealthy omitted from list
- [ ] Hot reload success/failure JSON shapes
- [ ] `IModelRegistryWriter.AddModel` → immediate `TryGetModel` + file on disk ([13-live-model-registry.md](./13-live-model-registry.md) §10)
- [ ] Passthrough: `/metrics`, `/health`, `/admin/api/config/status` not proxied

See [02-testing-strategy.md](./02-testing-strategy.md) for harness details.
