# Live Model Registry — Normative Specification

**Status:** Planning authoritative (2026-05-26)  
**Applies from:** Phase 2 (foundation) → Phase 4 (admin CRUD APIs) → Phase 5 (admin UI)  
**Related:** [09-v1-parity-spec.md](./09-v1-parity-spec.md) §7–9, [08-operator-console.md](./08-operator-console.md), [11-ha-and-scaling.md](./11-ha-and-scaling.md)

---

## 1. Product requirement

Operators **MUST** be able to register, update, and remove LLM backends (e.g. vLLM, OpenAI-compatible servers) so that:

1. Changes appear on **running** gateway endpoints (`GET /v1/models`, inference POSTs) **without process restart**.
2. The **source of truth** remains `models.json` on disk (GitOps-friendly, manual edit supported).
3. The same behavior applies whether the change comes from **manual file edit**, **admin HTTP API**, **operator console**, or **admin web UI**.
4. **Inference latency and in-flight streams** are not materially degraded by registry updates.

This document supersedes the weaker v1-only “poll + optional reload button” story where operators had to wait or call reload explicitly.

---

## 2. Non-goals

- **Per-model HTTP routes** on the gateway (clients always use OpenAI paths + `model` in body).
- **Separate “provider plugin” types** (OpenAI vs Azure vs vLLM) — only `models[]` entries with `id`, `url`, `aliases`.
- **Cluster-wide automatic sync** across pods without shared storage or orchestration (see [11-ha-and-scaling.md](./11-ha-and-scaling.md)).
- **Synchronous health proof** before listing — new models may appear on detail API immediately; list API still filters by health per [09-v1-parity-spec.md](./09-v1-parity-spec.md) §8.

---

## 3. Architecture

### 3.1 Single write pipeline

All mutations **MUST** flow through **`IModelRegistryWriter`** (implemented in `33pol.Registry`, orchestrated by `IControlPlaneCommands`):

```text
                    ┌─────────────────────────────────────┐
  Manual edit       │  IModelRegistryWriter              │
  Admin API    ────►│  1. Validate (schema, duplicates)  │
  Console      ────►│  2. Persist models.json (atomic)   │
  Admin UI     ────►│  3. Apply in-memory (atomic swap) │
                    └─────────────────────────────────────┘
                                      │
                    ┌─────────────────┴─────────────────┐
                    ▼                                   ▼
            IModelRegistry (read)              ConfigReloadService
            GET /v1/models, router               (file watch / poll fallback)
```

**Normative rule:** Admin API and console **MUST NOT** write `models.json` and rely on a separate poll to update memory. Step 3 runs in the **same operation** as step 2.

**Read path:** `IModelRegistry` remains read-only for inference and `/v1/models`. Reload-from-file (without mutation) **MAY** call `IModelRegistry.LoadModelsAsync` only.

### 3.2 Interfaces (`33pol.Core`)

| Interface | Responsibility | Phase |
|-----------|----------------|-------|
| `IModelRegistry` | Lookup, snapshots, `LoadModelsAsync` from file | 2 |
| `IModelRegistryWriter` | `AddModel`, `UpdateModel`, `RemoveModel`, `ReplaceAll` (optional) — validate + persist + apply | 2 (impl) / 4 (HTTP) |
| `IConfigReload` | `TriggerReloadAsync`, `GetStatusAsync` — reload file without CRUD; status DTO | 2 |
| `IControlPlaneCommands` | Delegates registry CRUD + reload to writer/reload services | 4 |

### 3.3 Apply semantics (MUST)

| Rule | Detail |
|------|--------|
| Atomic swap | Build new lookup dictionary under lock; swap references; hold lock **microseconds**, not during upstream I/O |
| In-flight requests | Keep resolved model/URL for the lifetime of that request |
| New requests | See new registry immediately after successful apply |
| Active SSE streams | **MUST NOT** be cancelled on apply |
| Empty `models` on apply | Warn; **do not clear** existing registry (v1 parity) |
| Invalid JSON on apply | Reject mutation; keep previous registry; return error to caller |
| Invalid JSON on startup | Fail host start |
| File persist | Write to temp file in same directory, then `File.Move` replace (atomic on same volume) |

### 3.4 Live file detection (manual edit)

| Mode | Config | Behavior |
|------|--------|----------|
| **Watch** (default Development) | `Gateway:RegistryWatchEnabled` = `true` | `FileSystemWatcher` on `models.json` parent + debounce **500 ms** → `LoadModelsAsync` |
| **Poll** (default Production/Docker) | `RegistryWatchEnabled` = `false` | Poll interval `ConfigReloadIntervalSeconds` (default **2**, min 1, max 300) + SHA-256 content hash |
| **Force** | — | `POST /admin/api/config/reload` always available |

**Rationale:** v1 avoided watchers for broken Docker bind mounts; v2 uses watch where enabled and poll elsewhere. Operators editing via API/CLI get **immediate** in-memory apply regardless.

**Visibility SLO:** After a successful write (API, CLI, or detected file change), a new model **MUST** be routable for inference within **1 s** (p99). `GET /v1/models` list may lag health probe by `HealthCheckIntervalSeconds` unless strict mode is off (optimistic default).

---

## 4. `models.json` schema

Unchanged from [09-v1-parity-spec.md](./09-v1-parity-spec.md) §7. Example (vLLM):

```json
{
  "models": [
    {
      "id": "vllm-qwen-7b",
      "url": "http://10.0.0.12:8000",
      "aliases": ["local-qwen"],
      "maxContextLength": 32768
    }
  ]
}
```

---

## 5. Admin HTTP API (Phase 4, secured Phase 3+)

Base path: `/admin/api/models`  
Auth: admin credential when `RequireAdminApiKey` is enabled ([10-identity-data-model.md](./10-identity-data-model.md)).

| Method | Path | Action |
|--------|------|--------|
| `GET` | `/admin/api/models` | List all registry entries (id, url, aliases, maxContextLength, health summary) |
| `POST` | `/admin/api/models` | Add model (body = single model object); 409 if `id` exists |
| `PATCH` | `/admin/api/models/{id}` | Partial update (url, aliases, maxContextLength) |
| `DELETE` | `/admin/api/models/{id}` | Remove model; 404 if missing |

Legacy reload/status (retained):

| Method | Path | Action |
|--------|------|--------|
| `POST` | `/admin/api/config/reload` | Re-read file from disk (idempotent; no CRUD) |
| `GET` | `/admin/api/config/status` | `hotReloadEnabled`, `lastReload`, `modelCount`, `models[]`, `watchEnabled` |

**Audit:** All mutating registry calls **MUST** emit `IAuditLogger` with actor (`admin-api`, `operator-console`, `admin-ui`).

**OpenAPI:** Document in Phase 4 control-plane spec.

---

## 6. Operator console (Phase 4)

Promoted from optional Phase 5 — **MUST** use `IModelRegistryWriter` via `IControlPlaneCommands`:

| Command | Action |
|---------|--------|
| `models list` | Registry + health (existing) |
| `models add` | Prompt id, url, aliases → validate → persist + apply |
| `models edit <id>` | Prompt fields → PATCH equivalent |
| `models remove <id>` | Confirmation → DELETE equivalent |
| `reload` | File-only reload (unchanged) |

See [08-operator-console.md](./08-operator-console.md).

---

## 7. Admin web UI (Phase 5)

**MUST** include a **Models** page ([phase-5](./phases/phase-5-finops-ui-ecosystem-and-ga.md) WP5.3):

- Table of models (id, url, aliases, health, actions).
- **Add model** form (calls `POST /admin/api/models`).
- **Edit** / **Delete** per row.
- Optional **raw JSON** editor with client-side schema validation → `POST` full replace or per-row APIs only (prefer per-row APIs for audit clarity).
- Reload button remains for file-only recovery.

Browser UI **MUST NOT** duplicate registry logic — only `/admin/api/models` and status endpoints.

---

## 8. Performance and isolation (normative)

Extends [08-operator-console.md](./08-operator-console.md) §6:

| ID | Requirement |
|----|-------------|
| R1 | Registry apply **MUST NOT** block inference thread pool; file I/O async where possible |
| R2 | Lock during swap only; no lock held across `IHttpForwarder.SendAsync` |
| R3 | File watcher debounce **MUST** coalesce rapid saves (single apply) |
| R4 | Admin/registry code **MUST NOT** run on inference hot path except O(1) registry read |
| R5 | Load test: 1000 RPS inference during 10 registry adds/min → p99 overhead vs baseline **≤ +2 ms** |
| R6 | Console/UI registry writes are **rare**; no polling of full file on inference path |

---

## 9. Deployment

| Environment | `models.json` mount | Watch | Notes |
|-------------|---------------------|-------|-------|
| Local dev | Writable path under `config/` | On | Instant manual edit |
| Docker Compose | Writable volume | On or poll 2s | Document in `deploy/docker/` |
| Kubernetes | ConfigMap volume **read-only** | Off (poll only) | Registry mutations via **admin API** only unless writable emptyDir + sync sidecar documented |
| Multi-replica | Shared RWX volume **or** API fan-out | Per pod | See [11-ha-and-scaling.md](./11-ha-and-scaling.md) |

---

## 10. Testing (MUST)

| Test | Phase |
|------|-------|
| `AddModel` → `TryGetModel` immediate | 2 |
| Persisted file matches memory after add | 2 |
| File watcher / poll applies manual edit | 2 |
| Concurrent apply + inference (stress) | 2 or 5 |
| `POST /admin/api/models` → `GET /v1/models/{id}` 200 | 4 |
| Console `models add` same as HTTP (shared `IControlPlaneCommands`) | 4 |
| Admin UI add → list shows model (manual or Playwright) | 5 |
| Apply during active SSE stream does not reset stream | 2 integration |

See [02-testing-strategy.md](./02-testing-strategy.md).

---

## 11. Phase checklist

| Phase | Deliverable |
|-------|-------------|
| **P2** | `IModelRegistryWriter`, atomic persist+apply, watch/poll `ConfigReloadService`, reload/status APIs |
| **P3** | Secured admin auth on all registry + reload routes |
| **P4** | `/admin/api/models` CRUD, console `models add/edit/remove`, audit |
| **P5** | Admin UI Models page, HA runbook for multi-pod registry |

---

## 12. Migration from v1 mental model

| v1 behavior | v2 behavior |
|-------------|-------------|
| Edit file → wait 5s poll | Edit file → watch debounce ≤500ms **or** poll 2s **or** API apply instant |
| Only `POST /admin/reload` | Reload still exists; CRUD APIs apply immediately |
| Console N/A | Console + UI write same pipeline |
| Read-only mount common | Document API-first mutation in K8s |
