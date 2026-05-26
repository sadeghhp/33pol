# Testing Strategy — Unit-First, Massive Coverage

## Policy

**No production behavior ships without unit tests.** Implementation and tests are one deliverable (see `.cursor/rules/unit-test-coverage.mdc`).

This document defines **what** to test, **how** to structure tests, and **when** each test type runs in the five phases.

---

## Test pyramid

```text
                    ┌─────────────┐
                    │  k6 load    │  Phase 5 (GA gates)
                    │  (few)      │
                ┌───┴─────────────┴───┐
                │ Integration + E2E    │  Phase 2+
                │ (WebApplicationFactory)│
            ┌───┴─────────────────────┴───┐
            │     Unit tests (many)        │  Every phase
            └─────────────────────────────┘
```

| Layer | Project | Scope | When |
|-------|---------|-------|------|
| **Unit** | `33pol.*.Tests` per library | Classes, pure logic, middleware isolated | Every PR |
| **Integration** | `33pol.Integration.Tests` | HTTP pipeline, mock upstream | Phase 2+ |
| **Architecture** | `33pol.Architecture.Tests` | Dependency rules | Phase 1+ |
| **Conformance** | `33pol.Conformance.Tests` | OpenAI shapes, error golden files | Phase 5 (GA) |
| **Contract** | Integration or Conformance | OpenAI response shapes, error JSON | Phase 3+ |
| **Load** | `perf/k6` | RPS, latency, TTFT | Phase 2 baseline, Phase 5 GA |

---

## Unit test standards

### Frameworks

- **xUnit** — test runner  
- **FluentAssertions** — readable assertions  
- **NSubstitute** — mocks (prefer interfaces from `33pol.Core`)  
- **coverlet** + ReportGenerator — coverage in CI  

### Naming

```text
{MethodName}_{State}_{ExpectedResult}
```

Example: `TryGetModel_UnknownAlias_ReturnsFalse`

### Structure

- Arrange / Act / Assert  
- One logical behavior per test  
- Use `[Theory]` + `[InlineData]` for matrix cases (aliases, error codes, limits)  

### File layout

Mirror production:

```text
src/33pol.Registry/ModelRegistryService.cs
tests/33pol.Registry.Tests/ModelRegistryServiceTests.cs
```

### What must have unit tests

| Area | Examples |
|------|----------|
| Registry | Load, alias map, empty file, invalid JSON, concurrent read |
| Router | Path classification, model extraction, health gate decisions |
| Transformer | JSON model rewrite (all spacing variants) |
| Auth | Missing key, invalid key, expired, exempt paths |
| Rate limit resolver | RPM, burst, concurrency |
| Quota | Hard stop vs soft warning |
| Errors | Each `code` maps to correct HTTP + JSON shape |
| Billing | Token aggregation, idempotency key, event mapping |
| Health | Probe fallback order, optimistic vs strict |
| Config reload | Semaphore, hash change detection, file watch debounce |
| `IModelRegistryWriter` | Add/update/remove → file on disk + immediate lookup ([13-live-model-registry.md](./13-live-model-registry.md) §10) |
| Control plane commands | `ControlPlaneCommands` — reload, summary, backends, **models CRUD** (`IControlPlaneCommands` + fakes) |
| Operator console | Command parser, option validation, refresh throttle; **not** Spectre markup |

### What to avoid testing

- Framework behavior (Kestrel itself)  
- Third-party library internals  
- Trivial auto-properties with no logic  
- Spectre.Console layout, colors, or `AnsiConsole.Live` frame rendering  

### Operator console

- Test **`IControlPlaneCommands`** and command tokenizer/parser with NSubstitute fakes — same bar as other libraries.
- Assert **DTOs** passed to render helpers, not ANSI strings.
- Integration host: `Gateway:OperatorConsole:Enabled` = `false` by default in `WebApplicationFactory`.
- Optional: `IOperatorConsoleTestHarness` (`InternalsVisibleTo` integration tests) dispatches command strings without Spectre — see [08-operator-console.md](./08-operator-console.md) §10.

### `Program.cs` / DI

- No tests on `Program.cs` directly  
- Extract registration to `ServiceCollectionExtensions` if wiring grows; test extension methods lightly or rely on integration tests  

### Internals

- Prefer public API on services  
- `InternalsVisibleTo` only for middleware hooks that cannot be public  

---

## Coverage gates (CI)

| Assembly | Line coverage target (guide) |
|----------|----------------------------|
| `33pol.Core` | ≥ 95% |
| `33pol.Registry` | ≥ 90% |
| `33pol.Proxy` | ≥ 90% |
| `33pol.Security` | ≥ 90% |
| `33pol.Policy` | ≥ 90% |
| `33pol.Observability` | ≥ 85% |
| `33pol.Billing` | ≥ 90% |
| `33pol.Persistence` | ≥ 85% |
| `33pol.Api` | Thin endpoints; covered via integration tests + no business logic in endpoints |
| `33pol.OperatorConsole` | ≥ 90% on command handlers; Spectre rendering excluded |
| `33pol.App` | N/A (composition); integration-covered |
| `33pol.Integration.Tests` | N/A (test harness) |

**Fail CI** if coverage drops below threshold on `main` (Phase 1 sets baseline; enforce from Phase 2).

```bash
dotnet test --collect:"XPlat Code Coverage"
```

---

## Integration tests

### Harness

- `WebApplicationFactory<Program>` with `WithWebHostBuilder` overrides  
- `TestServer` + `HttpClient` for inference calls  
- **Mock upstream:** `WireMock.Net` or custom `DelegatingHandler` returning SSE fixtures  

### Scenarios (grow per phase)

| Phase | Scenarios |
|-------|-----------|
| 2 | POST chat completions → 200; unknown model → 404; unhealthy → 502; stream headers |
| 3 | 401 without key; admin blocked; reload requires admin key |
| 4 | 429 rate limit; metrics increment; trace header present; console disabled in test host |
| 5 | Usage row written; export API returns CSV |

### Testcontainers (Phase 3+)

- PostgreSQL for migration + repository tests  
- Optional Redis for distributed rate limit tests (Phase 4)  

---

## Test data and fixtures

**Convention:** mirror production namespaces under each test project; commit small JSON/SSE/text fixtures; load via `File.ReadAllText` or `EmbeddedResource` — never hard-code large payloads in test methods.

| Asset | Location |
|-------|----------|
| `models.json` samples | `tests/33pol.Registry.Tests/TestData/` |
| Registry edge cases | `tests/33pol.Registry.Tests/TestData/` (invalid JSON, empty file) |
| SSE streams | `tests/33pol.Integration.Tests/Fixtures/*.sse` |
| OpenAI error golden files | `tests/33pol.Integration.Tests/Fixtures/errors/` |
| Shared constants (optional) | `tests/33pol.Core.Tests/TestData/` |

Use **golden file** comparison for error JSON and `/v1/models` responses.

**v1 parity:** Tag integration tests `[Trait("Category", "V1Parity")]` per [09-v1-parity-spec.md](./09-v1-parity-spec.md) §13.

---

## Regression policy

Every bug fix includes a test that **fails without the fix**.

---

## CI pipeline (Phase 1)

```yaml
# Conceptual steps
- dotnet restore
- dotnet build -c Release
- dotnet test -c Release --no-build --collect:"XPlat Code Coverage"
- coverage gate (threshold)
- (Phase 5) optional: k6 smoke on PR labeled load-test
```

---

## Phase-specific test deliverables

| Phase | Test deliverable |
|-------|------------------|
| 1 | All test projects compile; sample test + NetArchTest pass |
| 2 | Registry + Proxy unit tests; integration proxy suite |
| 3 | Auth + resilience unit tests; DB integration tests |
| 4 | Policy + metrics unit tests; OTel smoke integration; operator console handler tests |
| 5 | Billing unit tests; k6 thresholds; inference conformance suite; full regression run |

---

## Definition of done (per story)

1. Production code merged  
2. Unit tests added/updated  
3. `dotnet test` green locally and on CI  
4. Coverage not regressed on affected assemblies  
5. Integration test updated if HTTP contract changed  
6. Taiga task commented with test evidence  
