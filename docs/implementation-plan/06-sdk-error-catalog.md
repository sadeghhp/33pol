# SDK Error Catalog (v2) — Planning Reference

Stable machine-readable codes for client SDKs. **Implement in Phase 3–4**; publish as `docs/errors.md` at GA.

## Envelope

```json
{
  "error": {
    "message": "Human-readable description",
    "type": "rate_limit_error",
    "code": "rate_limit_exceeded",
    "param": "model",
    "details": { }
  }
}
```

## Response headers

| Header | When |
|--------|------|
| `X-Request-Id` | All responses |
| `X-33pol-Error-Code` | Error responses (mirrors `error.code`) |
| `Retry-After` | 429 when retry is meaningful |

## Catalog

| HTTP | `type` | `code` | Phase |
|------|--------|--------|-------|
| 400 | `invalid_request_error` | `invalid_json` | P3 |
| 400 | `invalid_request_error` | `missing_model` | P3 |
| 400 | `invalid_request_error` | `model_not_allowed` | P3 |
| 400 | `invalid_request_error` | `request_too_large` | P3 |
| 401 | `authentication_error` | `invalid_api_key` | P3 |
| 401 | `authentication_error` | `expired_api_key` | P3 |
| 403 | `permission_error` | `insufficient_scope` | P3 |
| 404 | `invalid_request_error` | `model_not_found` | P3 |
| 429 | `rate_limit_error` | `rate_limit_exceeded` | P4 |
| 429 | `rate_limit_error` | `quota_exceeded` | P4 |
| 429 | `rate_limit_error` | `concurrency_limit_exceeded` | P4 |
| 502 | `backend_error` | `backend_unhealthy` | P3 |
| 502 | `backend_error` | `upstream_error` | P3 |
| 502 | `backend_error` | `circuit_open` | P3 |
| 503 | `service_unavailable` | `gateway_draining` | P3 |
| 503 | `service_unavailable` | `not_ready` | P3 |

### Code selection (grant vs policy)

| Situation | HTTP | `code` |
|-----------|------|--------|
| API key lacks **model grant** for the resolved model | 403 | `insufficient_scope` |
| Model blocked by **plan/feature/policy** (not scope) | 400 | `model_not_allowed` |

## Unit test requirement

- **Phase 1:** Every defined `GatewayErrorCode` enum value serializes to a stable string (catalog grows later).
- **Phase 3:** Golden JSON + unit tests for every row marked **P3** in the catalog table above.
- **Phase 4:** Same for **P4** rows (429 codes) plus `Retry-After` header tests.
- **Phase 4:** `Retry-After` header tests apply to 429 codes only.
