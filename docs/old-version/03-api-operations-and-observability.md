# 03 — API, Operations, and Observability

## Scope

This document specifies **everything the gateway exposes or runs in the background** beyond the core proxy path: HTTP endpoints, metrics, configuration reload, real-time admin (SignalR), optional PostgreSQL persistence, deployment behavior, known implementation gaps, and guidance for a v2 rewrite.

Proxy routing logic is in [02-core-proxy-and-routing.md](./02-core-proxy-and-routing.md). Host startup and configuration are in [01-overview-and-architecture.md](./01-overview-and-architecture.md).

## Audience

Developers implementing operational APIs, monitoring integrations, admin UI backends, or persistence for a gateway rewrite.

---

## 1. HTTP endpoint catalog

### 1.1 Summary table

| Method | Path | Auth (if keys set) | Handler |
|--------|------|-------------------|---------|
| GET | `/` | Required | Service info JSON |
| GET | `/health` | Public | Gateway + backend health |
| GET | `/stats` | Public | In-memory statistics JSON |
| GET | `/metrics` | Public | Prometheus text exposition |
| GET | `/v1/models` | Required | OpenAI model list |
| GET | `/v1/models/{model}` | Required | OpenAI model detail |
| POST | `/v1/chat/completions` | Required | Model router (proxy) |
| POST | `/v1/completions` | Required | Model router (proxy) |
| POST | `/v1/embeddings` | Required | Model router (proxy) |
| POST | `/admin/reload` | Required | Hot reload `models.json` |
| GET | `/admin/status` | Required | Config status |
| WebSocket | `/hubs/admin` | Required | SignalR admin hub |

When `ApiKeys` is empty, all endpoints are anonymous.

---

## 2. Service info — `GET /`

**Response:** `200 OK` JSON

```json
{
  "service": "LLM Gateway",
  "version": "1.2.0",
  "status": "running",
  "endpoints": [
    "GET /health - Health check",
    "GET /stats - Gateway statistics",
    "..."
  ],
  "features": [
    "Hot reload enabled - edit models.json or call POST /admin/reload",
    "Real-time admin dashboard via SignalR WebSocket"
  ]
}
```

---

## 3. Health — `GET /health`

### 3.1 Response model

```json
{
  "status": "healthy | degraded",
  "uptime": "2025-12-04T12:00:00.0000000Z",
  "totalBackends": 3,
  "healthyBackends": 2,
  "unhealthyBackends": 1,
  "backends": [
    {
      "modelId": "Qwen/Qwen3-4B",
      "url": "http://gpu-1:8000",
      "isHealthy": true,
      "lastChecked": "2025-12-04T12:00:00Z",
      "error": null
    }
  ]
}
```

### 3.2 Status rules

| Condition | `status` field | HTTP status |
|-----------|----------------|-------------|
| `healthyBackends > 0` | `"healthy"` | 200 |
| `healthyBackends == 0` | `"degraded"` | 503 |

Lists **all configured models** from registry, merging last probe results from `HealthCheckService`. If a backend has not been probed yet, `isHealthy` defaults to `true` in the merged view when no entry exists.

---

## 4. Statistics — `GET /stats`

Returns `GatewayStats` from `MetricsService.GetStats()`:

```json
{
  "uptime": "00.02:15:30",
  "uptimeSeconds": 8130,
  "totalRequests": 1542,
  "requestsPerModel": {
    "Qwen/Qwen3-4B": 1200,
    "meta-llama/Llama-3-8B": 342
  },
  "averageLatencyMs": 245.67,
  "activeConnections": 3,
  "errorsPerModel": {
    "Qwen/Qwen3-4B": 5
  }
}
```

| Field | Meaning |
|-------|---------|
| `uptime` | Formatted `dd.hh:mm:ss` since service start |
| `uptimeSeconds` | Total seconds |
| `totalRequests` | All completed tracked requests |
| `requestsPerModel` | Counter per canonical model id |
| `averageLatencyMs` | Mean of last **100** request durations (ms) |
| `activeConnections` | Sum of active streaming connections |
| `errorsPerModel` | Error counter per model |

---

## 5. Prometheus — `GET /metrics`

Exposed via `UseMetricServer("/metrics")`.

### 5.1 Metric definitions

| Name | Type | Labels | Description |
|------|------|--------|-------------|
| `llm_gateway_requests_total` | Counter | `model`, `status` | `status` = `success` or `error` |
| `llm_gateway_active_streams` | Gauge | `model` | Current streaming connections |
| `llm_gateway_request_duration_seconds` | Histogram | `model` | Request duration |
| `llm_gateway_errors_total` | Counter | `model`, `type` | Error classification string |

### 5.2 Histogram buckets (seconds)

```text
0.1, 0.5, 1, 2, 5, 10, 30, 60, 120, 300
```

### 5.3 `RequestTracker` pattern

```text
using (tracker = metrics.StartRequest(modelId)) {
    // forward request
    if failure: tracker.MarkFailed()
}
// On Dispose: RecordRequest(durationMs, success)
```

Increments Prometheus counters/histogram and in-memory rolling latency window.

---

## 6. OpenAI models API

### 6.1 `GET /v1/models`

**Response shape:**

```json
{
  "object": "list",
  "data": [
    {
      "id": "Qwen/Qwen3-4B",
      "object": "model",
      "created": 1733328000,
      "owned_by": "llm-gateway",
      "permission": [ { "...": "..." } ],
      "root": "Qwen/Qwen3-4B",
      "parent": null,
      "max_model_len": 40960,
      "available": true
    }
  ]
}
```

**Filtering:** Only models where `HealthCheckService.IsBackendHealthy(model.Id)` is **true** appear in `data`.

**Extension fields** (not in official OpenAI spec):

| Field | Source |
|-------|--------|
| `max_model_len` | `ModelConfig.MaxContextLength` |
| `available` | Health status |

`permission` array is synthetic (allows sampling, view, etc.).

### 6.2 `GET /v1/models/{model}`

- Lookup by **canonical id or alias**.
- **404** if not in registry:

```json
{
  "error": {
    "message": "Model 'unknown' not found",
    "type": "invalid_request_error",
    "code": "model_not_found"
  }
}
```

- Returns model **even if unhealthy**; `available` reflects health.
- Always uses canonical `id` in response, not the alias used in the URL.

---

## 7. Admin operations

### 7.1 `POST /admin/reload`

Triggers `ConfigReloadService.ReloadConfigAsync()`.

**Success — 200:**

```json
{
  "status": "success",
  "message": "Configuration reloaded successfully",
  "previousModelCount": 3,
  "currentModelCount": 4,
  "models": ["model-a", "model-b", "..."],
  "timestamp": "2025-12-04T17:39:28Z"
}
```

**Failure — 500:**

```json
{
  "status": "error",
  "message": "Failed to reload: <reason>"
}
```

**Concurrency:** If reload already in progress (semaphore timeout 5s):

```json
{
  "status": "error",
  "message": "Reload already in progress"
}
```

**Side effects on success:**

1. `ModelRegistryService.LoadModelsAsync`
2. `DynamicYarpConfigProvider.BuildConfig()` (v1)

Active streaming connections are **not** terminated.

### 7.2 `GET /admin/status`

```json
{
  "hotReloadEnabled": true,
  "lastReload": "2025-12-04T17:39:28Z",
  "modelCount": 4,
  "models": [
    {
      "id": "Qwen/Qwen3-4B",
      "url": "http://gpu-1:8000",
      "aliases": ["qwen-4b"]
    }
  ]
}
```

`lastReload` is `DateTime.MinValue` until first successful reload (including initial load does not set it in v1 — only `ReloadConfigAsync` updates it).

---

## 8. Hot reload (`ConfigReloadService`)

### 8.1 Automatic reload (polling)

| Parameter | Value |
|-----------|-------|
| Poll interval | 5 seconds |
| Change detection | `hash = "{fileLength}:{content.GetHashCode()}"` |
| File watcher | **Not used** (Docker bind mounts) |

On hash change → `ReloadConfigAsync()`.

### 8.2 Manual reload

`POST /admin/reload` → same `ReloadConfigAsync()` path.

### 8.3 Config path resolution

```text
if ModelsConfigPath is absolute → use as-is
else if exists at AppContext.BaseDirectory + path → use that
else → Path.GetFullPath(relative path)
```

### 8.4 `ReloadResult` type

| Field | Type |
|-------|------|
| `Success` | bool |
| `Message` | string |
| `PreviousModelCount` | int |
| `CurrentModelCount` | int |
| `Models` | `List<string>` (canonical ids) |
| `Timestamp` | DateTime UTC |

---

## 9. Real-time admin (SignalR) — v1

### 9.1 Hub endpoint

- Path: `/hubs/admin`
- CORS policy: `"SignalR"` (credentials allowed)
- Hub class: `AdminHub`

### 9.2 Server → client messages

| Method name | Payload type | When sent |
|-------------|--------------|-----------|
| `ReceiveInitialState` | `DashboardState` | On connect; on `RequestState` |
| `ReceiveMetrics` | `RealTimeMetrics` | Every 1s (broadcast); on `RequestMetrics` |
| `ReceiveHealthBatch` | `List<RealTimeHealthStatus>` | Every 5s; on `RequestHealth` |
| `ReceiveRequest` | `RealTimeRequest` | After each proxied request completes |
| `ReceiveLog` | `RealTimeLog` | On Serilog event (Information+) |

### 9.3 Client → server methods

| Method | Effect |
|--------|--------|
| `RequestState` | Sends `ReceiveInitialState` to caller |
| `RequestMetrics` | Sends `ReceiveMetrics` to caller |
| `RequestHealth` | Sends `ReceiveHealthBatch` to caller |

### 9.4 `RealTimeBroadcastService` (background)

| Task | Interval |
|------|----------|
| Broadcast metrics to all clients | 1 second |
| Broadcast health batch | 5 seconds |
| On `RequestEventService.OnRequestReceived` | Immediate `ReceiveRequest` + optional DB queue |

**Dead code path:** Subscribes to `OnLogReceived` but `RequestEventService.RecordLog()` is never called from Serilog. Logs reach clients via `SignalRLogSink` directly, not via `OnLogReceived`.

### 9.5 `RequestEventService`

| Feature | Detail |
|---------|--------|
| Recent requests buffer | `ConcurrentQueue`, max **100** entries |
| RPS calculation | Once per second: `(count - lastCount) / elapsed` |
| `OnRequestReceived` | Fired from `RecordRequest` after each proxy completion |

### 9.6 `SignalRLogSink` (Serilog)

On each log event ≥ configured minimum (Information):

1. Build `RealTimeLog` (truncate properties to 10 keys).
2. Fire-and-forget `Clients.All.SendAsync("ReceiveLog", log)`.
3. `LogPersistenceService?.QueueLog(log)` if DB enabled.
4. Swallow all exceptions (logging must not break the app).

---

## 10. DTO reference (`RealTimeModels`)

### 10.1 `RealTimeRequest`

| Field | Type | Description |
|-------|------|-------------|
| `Id` | string | First 8 chars of Guid hex |
| `Model` | string | Canonical model id |
| `Endpoint` | string | Request path |
| `IsStreaming` | bool | From body `stream` |
| `DurationMs` | double | Wall time for forward |
| `StatusCode` | int | Response status |
| `Success` | bool | Status &lt; 400 and no forwarder error |
| `ErrorType` | string? | Forwarder error or `"exception"` |
| `Timestamp` | DateTime UTC | Start/completion time |

### 10.2 `RealTimeMetrics`

| Field | Type |
|-------|------|
| `Uptime` | string (formatted) |
| `UptimeSeconds` | long |
| `TotalRequests` | long |
| `RequestsPerSecond` | double |
| `AverageLatencyMs` | double |
| `ActiveConnections` | int |
| `HealthyBackends` | int |
| `TotalBackends` | int |
| `RequestsPerModel` | `Dictionary<string, long>` |
| `ErrorsPerModel` | `Dictionary<string, long>` |
| `Timestamp` | DateTime UTC |

### 10.3 `RealTimeHealthStatus`

| Field | Type |
|-------|------|
| `ModelId` | string |
| `Url` | string |
| `IsHealthy` | bool |
| `StatusCode` | int? |
| `Error` | string? |
| `LastChecked` | DateTime |
| `MaxContextLength` | int |
| `Aliases` | `List<string>` |

### 10.4 `RealTimeLog`

| Field | Type |
|-------|------|
| `Id` | string (8 char) |
| `Level` | string (Serilog level name) |
| `Message` | string (rendered) |
| `Source` | string? (`SourceContext` property) |
| `Properties` | `Dictionary<string, object>?` |
| `Timestamp` | DateTime UTC |

### 10.5 `DashboardState`

| Field | Type |
|-------|------|
| `Metrics` | `RealTimeMetrics` |
| `HealthStatuses` | `List<RealTimeHealthStatus>` |
| `RecentRequests` | `List<RealTimeRequest>` |

---

## 11. PostgreSQL persistence (optional)

### 11.1 Activation

Connection string name: `LogsDb`.

Registers:

- `LogDbContext` (EF Core, Npgsql)
- `LogPersistenceService` as singleton + hosted service

### 11.2 Schema

#### Table `logs`

| Column | Type | Notes |
|--------|------|-------|
| `id` | BIGSERIAL PK | Auto-generated |
| `level` | VARCHAR(20) | Required |
| `message` | TEXT | Required |
| `source` | VARCHAR(255) | Nullable |
| `properties` | JSONB | Nullable serialized dict |
| `timestamp` | TIMESTAMPTZ | Event time |
| `created_at` | TIMESTAMPTZ | Default NOW() |

Indexes: `timestamp DESC`, `level`.

#### Table `requests`

| Column | Type | Notes |
|--------|------|-------|
| `id` | UUID PK | See §11.4 |
| `model` | VARCHAR(255) | Canonical id |
| `endpoint` | VARCHAR(100) | |
| `is_streaming` | BOOLEAN | |
| `duration_ms` | DOUBLE PRECISION | |
| `status_code` | INT | |
| `success` | BOOLEAN | |
| `error_type` | VARCHAR(100) | Nullable |
| `timestamp` | TIMESTAMPTZ | |
| `created_at` | TIMESTAMPTZ | Default NOW() |

Indexes: `timestamp DESC`, `model`, `success`.

### 11.3 `LogPersistenceService` behavior

| Setting | Value |
|---------|-------|
| Channel capacity | 10,000 items |
| Full mode | `DropOldest` |
| Batch size | 100 items |
| Batch timeout | 1 second |

**Startup:** `EnsureCreatedAsync()`; on failure, execute raw `CREATE TABLE IF NOT EXISTS` SQL.

**No read API** in v1 — data is written for external analytics or future admin features only.

### 11.4 Request ID mapping quirk

`RealTimeRequest.Id` is an 8-character string. Persistence uses `Guid.TryParse(request.Id)` which **usually fails**, so a **new random Guid** is stored per row.

---

## 12. Deployment notes

### 12.1 Ports

| Artifact | Typical port |
|----------|--------------|
| Standalone `Dockerfile` | 8080 (`ASPNETCORE_URLS=http://+:8080`) |
| Sample `docker-compose` gateway service | 11444 |

Document and standardize one port in v2.

### 12.2 `models.json`

- Mounted read-only into container (e.g. `/app/models.json`).
- Not copied into runtime image in compose setup.

### 12.3 PostgreSQL in compose

Gateway may `depends_on` Postgres `service_healthy` even though **proxying does not require DB** — only persistence does.

### 12.4 `host.docker.internal`

Compose may add `extra_hosts` so containers reach LLM backends on the host machine.

---

## 13. Known gaps and implementation debt (v1)

| Issue | Impact |
|-------|--------|
| YARP `MapReverseProxy` never called | Clusters unused; confusing dependency |
| `RequestTimeoutSeconds` not wired to forwarder | Config misleading |
| `EnableCors` flag ignored | Always registers permissive CORS |
| Alias body rewrite via string replace | Brittle JSON handling |
| Health unknown → treated healthy | Traffic before first probe |
| Reload hash uses `GetHashCode()` | Possible missed updates (collision) |
| Empty `models.json` on reload | Warning only; may leave stale registry |
| Postgres write-only | No history API for admin |
| `OnLogReceived` never fired | Dead subscription in broadcast service |
| API keys empty → open admin reload | Security risk |
| `DangerousAcceptAnyServerCertificate` in YARP cluster config | MITM risk if YARP ever used |
| 8-char request id vs UUID in DB | Poor correlation |

---

## 14. Minimal rewrite checklist

### 14.1 Core (required for parity)

- [ ] ASP.NET Core 8 host + Kestrel streaming settings
- [ ] Load `models.json` into thread-safe registry with aliases
- [ ] API key middleware (optional keys)
- [ ] Model router for three POST paths
- [ ] `IHttpForwarder` + streaming transformer
- [ ] OpenAI error JSON format
- [ ] `GET /v1/models` and `/v1/models/{model}`
- [ ] `MetricsService` + `/stats` + `/metrics`
- [ ] `HealthCheckService` + `/health`
- [ ] `ConfigReloadService` + `/admin/reload` + `/admin/status`
- [ ] Serilog request logging (no body)

### 14.2 Optional layers (v1 parity)

- [ ] SignalR hub + broadcast service
- [ ] Serilog SignalR sink
- [ ] PostgreSQL persistence service

### 14.3 Recommended v2 changes

- [ ] Single host: static Alpine.js admin + REST `/admin/api/*`
- [ ] Remove unused YARP reverse proxy registration
- [ ] Wire request timeout to forwarder HTTP handler
- [ ] JSON-based model rewrite (parse/serialize) instead of string replace
- [ ] Separate admin API key from client API keys
- [ ] Postgres read endpoints for historical logs/requests (if DB kept)
- [ ] Replace SignalR with polling or SSE for simpler admin UI

---

## 15. v2 admin API mapping (suggested)

Replace SignalR payloads with REST equivalents:

| v1 SignalR / behavior | v2 REST suggestion |
|-----------------------|-------------------|
| `ReceiveMetrics` | `GET /admin/api/summary` |
| `ReceiveHealthBatch` | `GET /admin/api/backends` |
| `ReceiveRequest` / recent list | `GET /admin/api/requests?limit=50` |
| `ReceiveLog` | `GET /admin/api/logs?level=&limit=` |
| `POST /admin/reload` | Keep or move to `POST /admin/api/config/reload` |
| `GET /admin/status` | `GET /admin/api/config/status` |

Serve Alpine.js from `wwwroot` at `/admin` with same-origin API calls (no CORS complexity).

---

## 16. Related documents

| Document | Contents |
|----------|----------|
| [01-overview-and-architecture.md](./01-overview-and-architecture.md) | Product overview, startup, DI, configuration |
| [02-core-proxy-and-routing.md](./02-core-proxy-and-routing.md) | Registry, router, forwarding, auth, health |
| [README.md](./README.md) | Index and read order |
