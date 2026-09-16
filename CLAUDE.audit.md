# 33pol Audit Remediation Protocol

Primary remediation specification:

`audit-2026-09-13.md`

The audit defines the reported findings, intended behavior, risk, acceptance criteria, and remediation direction.

The repository defines the current truth.

Never blindly implement an audit claim. Verify every requested finding against the current repository before changing production code.

# Objective

Remediate audit findings with:

* minimal safe diffs;
* production-correct behavior;
* strong regression protection;
* preserved compatibility where practical;
* preserved hot-path performance;
* explicit validation evidence.

Optimize for correctness, safety, maintainability, and verifiability.

Do not optimize for number of findings closed.

---

# Scope rule

Work only on the finding or finding group explicitly requested in the current task.

Do not automatically:

* start the next finding;
* start the next audit phase;
* perform unrelated cleanup;
* perform speculative refactoring;
* expand scope because nearby code could be improved.

Stop when the requested scope has been implemented, validated, reviewed, and reported.

---

# Per-finding execution protocol

For every requested finding:

## 1. Read the specification

Read only the relevant audit material:

* ranked finding entry;
* detailed finding section if present;
* executable backlog entry;
* related root-cause section when useful;
* relevant verified-sound constraints.

Do not repeatedly reread the entire audit.

## 2. Inspect current state

Before editing:

* run `git status`;
* inspect the affected implementation;
* inspect dependency registration/composition;
* inspect configuration;
* inspect relevant tests;
* inspect important callers and consumers;
* identify unrelated working-tree changes and preserve them.

## 3. Verify the finding

Prove that the reported defect still exists in the current repository.

Prefer evidence in this order:

1. deterministic reproduction or regression test;
2. executable trace;
3. direct code proof;
4. strong static reasoning when reproduction is impractical.

If repository evidence contradicts the audit:

* do not force a code change;
* classify the result as `AUDIT CONTRADICTION`;
* provide concrete evidence.

## 4. Define the invariant

Before implementing the fix, determine the property that must remain true.

Examples:

* Production authentication fails closed.
* Ledger and rollups cannot become permanently inconsistent.
* Accepted usage is persisted or explicitly recorded as dropped.
* A stale breaker lease cannot mutate a newer breaker epoch.
* Per-model registries converge to live-model state.
* Production artifacts contain no development fixture.

Fix the invariant, not merely the observed symptom.

## 5. Implement the smallest correct fix

Prefer:

* existing sound abstractions;
* explicit invariants;
* bounded state;
* idempotent recovery;
* atomic transitions;
* deterministic behavior.

Avoid:

* broad refactors;
* speculative frameworks;
* unrelated cleanup;
* duplicate abstractions;
* hidden fallback behavior.

Introduce a new abstraction only when:

1. the existing design cannot safely express the invariant; or
2. multiple confirmed findings share one demonstrated root cause and a small abstraction removes that defect class.

## 6. Add regression protection

Add or strengthen a test that would fail against the previous incorrect behavior.

A test that only executes the new implementation without distinguishing old from new behavior is insufficient.

## 7. Validate efficiently

Development loop:

`focused test → affected project tests`

Then, when the requested remediation unit is complete:

`affected integration tests → Release build`

Reserve the full repository test suite for major checkpoints unless the finding specifically requires it.

## 8. Perform production-shaped validation

Where applicable, validate real behavior using:

* Production environment;
* real SQLite;
* actual publish output;
* container/image contents;
* streaming requests;
* shutdown behavior;
* concurrency;
* fault injection;
* previous-release migrations;
* Helm rendering;
* Docker Compose configuration;
* Prometheus rules.

Do not substitute unit-test evidence for a production-shaped acceptance criterion.

## 9. Inspect the final diff

Before reporting completion, review the entire diff.

Check specifically for:

* unrelated edits;
* weakened assertions;
* swallowed exceptions;
* accidental default changes;
* public-contract changes;
* new hot-path allocations;
* extra database operations;
* synchronous blocking;
* unbounded state;
* increased metric cardinality;
* unnecessary configuration churn.

## 10. Report status

Use only:

* `COMPLETE`
* `PARTIAL`
* `BLOCKED`
* `AUDIT CONTRADICTION`

Never use `COMPLETE` while required validation remains outstanding.

---

# Security invariants

Never weaken:

* authentication;
* authorization;
* tenant isolation;
* SSRF protection;
* grant enforcement;
* secret handling;
* fail-closed behavior;

merely to preserve existing tests.

Production security-sensitive behavior must fail closed.

`Admin` and `Operator` authorization must not succeed merely because authentication is globally disabled.

Anonymous Production operation must require an explicit intentional configuration contract.

Do not expose raw substrings of newly generated secrets in persisted metadata.

Cryptographic changes must preserve existing credentials whenever technically feasible.

Do not silently invalidate:

* existing API keys;
* stored upstream credentials.

Use versioned formats where cryptographic derivation or ciphertext representation changes.

---

# Billing and data-integrity invariants

Billing correctness has priority over convenience.

A logical billing operation must not become permanently partially applied.

Retry behavior must remain idempotent.

Crash recovery must preserve accounting correctness.

A persisted ledger event must not become permanently absent from rollups used for budget enforcement.

Dropped usage must not leave budget reservations stranded.

Every accepted usage event must eventually be:

* persisted; or
* explicitly accounted for as dropped.

Estimated usage must remain distinguishable from authoritative usage after persistence.

Do not use logging alone as evidence that accounting loss is acceptable.

---

# SQLite rules

Use real SQLite when validating:

* transactions;
* unique constraints;
* foreign keys;
* collation;
* migration;
* concurrent writers;
* WAL behavior;
* crash consistency.

EF InMemory is not valid evidence for those properties.

When transaction boundaries materially change:

* run the repository's WAL/concurrency benchmark;
* compare against the previous baseline;
* check for increased `SQLITE_BUSY`;
* check writer latency and throughput.

---

# Concurrency rules

Define the concurrency invariant before editing shared state.

Avoid:

* unversioned read-modify-write where concurrent writers are legitimate;
* process-wide locks on hot paths;
* stale leases mutating newer state;
* unbounded dictionaries or registries;
* timing-dependent correctness.

Use deterministic synchronization in concurrency tests wherever practical.

Do not use arbitrary sleeps when a barrier, signal, fake clock, controlled task, or injected fault can prove the behavior.

---

# Lifecycle rules

Maintain a coherent relationship between:

* readiness drain period;
* host shutdown timeout;
* forward timeout;
* billing drain/flush deadline;
* Kubernetes termination grace;
* Docker stop grace.

Do not solve lifecycle defects by arbitrarily making every timeout very large.

Where lifecycle behavior is changed, validate with real streaming traffic during process termination.

Every accepted billing event during shutdown must either:

* persist; or
* be explicitly counted as dropped.

---

# Test policy

Never:

* disable a test merely to obtain green;
* add `Skip` to hide a failure;
* weaken an assertion without technical justification;
* suppress an exception to preserve a test;
* change Production semantics merely because an old test encoded a bug.

When a test breaks after a fix, determine whether:

1. the test encoded the defect;
2. production code regressed;
3. the test harness differs materially from Production;
4. the test is nondeterministic.

Security-sensitive integration tests should exercise real authentication and authorization unless the test explicitly targets an isolated lower-level component.

Database-semantic tests should use real SQLite.

Prefer deterministic tests over wall-clock timing.

---

# Performance guardrails

For changes touching:

* inference forwarding;
* routing;
* streaming;
* resilience;
* rate limiting;
* billing capture;
* SQLite writes;

inspect for:

* extra allocations;
* repeated parsing;
* additional body buffering;
* synchronous waits;
* additional database access;
* per-chunk timer/CTS multiplication;
* new global synchronization;
* unbounded caches;
* high-cardinality metrics.

Do not claim "no performance regression" without evidence when changing a performance-sensitive path.

---

# Observability rules

Do not treat logs alone as sufficient observability for critical failure states.

Critical operational failures should have a machine-observable signal where appropriate.

Metrics must use bounded cardinality.

Alerts must have:

* a clear condition;
* operational meaning;
* stable dimensions;
* a runbook where the audit requires one.

Do not design readiness behavior that makes the only service instance unreachable exactly when operators need diagnostic access.

---

# Compatibility rules

Preserve existing public behavior unless a finding requires changing it.

For operator-visible changes, identify:

* previous behavior;
* new behavior;
* required migration/configuration change;
* failure mode if configuration is not updated.

Pay particular attention to:

* DB-less Production operation;
* existing API keys;
* encrypted upstream credentials;
* Helm values;
* Compose environment variables;
* metrics authentication;
* CORS;
* shutdown semantics;
* webhook contracts;
* published artifacts.

---

# Git discipline

Before editing:

* inspect `git status`;
* identify unrelated user changes;
* preserve them.

During work:

* inspect `git diff` regularly.

Never:

* reset unrelated changes;
* overwrite user work;
* rewrite Git history;
* perform broad cleanup;
* combine unrelated findings into one refactor.

Do not create commits unless explicitly instructed.

---

# Validation cadence

Use this cadence unless the finding requires stronger validation.

During development:

`focused tests → affected project tests`

At remediation-unit completion:

`affected integration tests → Release build`

At major audit gates:

`full Release suite + required operational validation`

Do not repeatedly run the full suite after every small edit.

When a test command produces large output, inspect only:

* failures;
* relevant stack traces;
* summary counts.

Do not fill context with repeated full logs.

---

# Definition of COMPLETE

A finding may be marked `COMPLETE` only when all applicable conditions are satisfied:

* defect verified against current code;
* root cause understood;
* target invariant defined;
* production implementation corrected;
* regression test added or strengthened;
* focused tests pass;
* affected broader tests pass;
* audit acceptance criteria are satisfied;
* production-shaped validation performed where required;
* performance impact assessed where relevant;
* compatibility impact documented;
* final diff reviewed;
* no required validation remains outstanding.

Otherwise use:

`PARTIAL`, `BLOCKED`, or `AUDIT CONTRADICTION`.

---

# Required report

For each requested finding:

| ID | Status | Root cause | Change | Regression protection | Validation | Residual risk |
| -- | ------ | ---------- | ------ | --------------------- | ---------- | ------------- |

Then include only:

## Important commands

Important command plus result.

## Compatibility impact

Only when behavior, configuration, persistence, deployment, or public contract changed.

## Newly discovered issues

Only defects directly exposed by the current remediation work.

Keep reporting concise.

Do not repeat the audit.

---

# Final principle

The goal is not to make the audit checklist disappear.

The goal is to make the important security, billing, reliability, lifecycle, and data-integrity guarantees structurally true and demonstrably protected by executable evidence.
