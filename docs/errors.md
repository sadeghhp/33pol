# 33pol Gateway Error Catalog

Machine-readable error codes for OpenAI-compatible clients and SDKs.

## Envelope

All gateway errors use this JSON shape:

```json
{
  "error": {
    "message": "Human-readable description",
    "type": "authentication_error",
    "code": "invalid_api_key",
    "param": "authorization",
    "details": {}
  }
}
```

`param` and `details` are omitted when empty.

## Response headers

| Header | When |
|--------|------|
| `X-Request-Id` | Every response (echoes client value when provided, otherwise `req_<guid>`) |
| `X-33pol-Error-Code` | Error responses; mirrors `error.code` |
| `Retry-After` | `429` responses when retry timing is known |

Every code below is implemented. `Phase` in `GatewayErrorDefinition` records which delivery phase introduced the code and is used by `GatewayErrorCatalog.IsPhase3`; it does not indicate readiness.

## Phase 3 codes

| HTTP | `type` | `code` |
|------|--------|--------|
| 400 | `invalid_request_error` | `invalid_json` |
| 400 | `invalid_request_error` | `request_incomplete` |
| 400 | `invalid_request_error` | `missing_model` |
| 400 | `invalid_request_error` | `model_not_allowed` |
| 400 | `invalid_request_error` | `request_too_large` |
| 401 | `authentication_error` | `invalid_api_key` |
| 401 | `authentication_error` | `expired_api_key` |
| 403 | `permission_error` | `insufficient_scope` |
| 404 | `invalid_request_error` | `model_not_found` |
| 502 | `backend_error` | `backend_unhealthy` |
| 502 | `backend_error` | `upstream_error` |
| 502 | `backend_error` | `circuit_open` |
| 503 | `service_unavailable` | `gateway_draining` |
| 503 | `service_unavailable` | `not_ready` |

### Grant vs policy

| Situation | HTTP | `code` |
|-----------|------|--------|
| API key lacks **model grant** for the resolved model | 403 | `insufficient_scope` |
| Model blocked by **plan/feature/policy** (not scope) | 400 | `model_not_allowed` |

## Phase 4 codes

| HTTP | `type` | `code` | When |
|------|--------|--------|------|
| 429 | `rate_limit_error` | `rate_limit_exceeded` | Tenant/key RPM or burst budget exhausted (WP4.1) |
| 429 | `rate_limit_error` | `quota_exceeded` | Monthly quota hard limit (WP4.2) |
| 429 | `rate_limit_error` | `concurrency_limit_exceeded` | Streaming concurrency cap (WP4.1) |

Responses include `Retry-After` when retry timing is known (RPM window reset).

## Implementation notes

- Canonical definitions live in `GatewayErrorCatalog` (`33pol.Core`).
- Responses are produced by `OpenAiErrorResponseWriter` and written through `IErrorResponseWriter`.
- Golden JSON fixtures for every Phase 3 code are in `tests/33pol.Core.Tests/TestData/`.
- `GatewayExceptionHandlingMiddleware` is the pipeline's terminal handler, so a failure no other
  middleware catches still produces one of these bodies rather than a bare status line from the
  server. It maps a server-side payload-too-large rejection to `request_too_large` and anything else
  unhandled to `upstream_error`. This is what makes an oversized body answer the same way whether the
  client declared a `Content-Length` (caught up front) or streamed it chunked (caught mid-read).
- Once the response has started nothing can be rewritten, so a mid-response failure aborts the
  connection instead of appending a second, contradictory status.
