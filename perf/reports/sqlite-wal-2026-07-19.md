# SQLite WAL concurrency check — 2026-07-19

Focused load test of the embedded SQLite write path (the concern raised by the
Postgres → single-instance SQLite migration): does WAL with `busy_timeout=5000` and
`synchronous=NORMAL` hold up when concurrent inference drives per-request usage/billing
writes through the single writer?

## Setup

- Gateway run directly (`ASPNETCORE_ENVIRONMENT=Production`, file-backed SQLite), strong
  secrets, rate limits raised so requests reach the DB write path.
- Threaded Python mock OpenAI upstream (single process, GIL-bound).
- Driver: `ab` (ApacheBench) against `POST /v1/chat/completions`, non-streaming.
- Host: 4 vCPU.
- Pragmas in effect (from `SqliteConnectionInterceptor`): `journal_mode=WAL`,
  `busy_timeout=5000`, `synchronous=NORMAL`, `foreign_keys=ON`.

## Results

| Load (`-n`/`-c`) | Gateway RPS | p50 | p95 | p99 | max | Notes |
|------------------|-------------|-----|-----|-----|-----|-------|
| 2000 / 16 | 723 | 20ms | 42ms | 62ms | 736ms | |
| 4000 / 32 | 802 | 27ms | 76ms | 255ms | 892ms | |
| 6000 / 64 | 733 | 82ms | 164ms | 379ms | 613ms | |

Across ~12,000 requests, **3,182 billing rows** were written concurrently by the batched
usage writer.

### SQLite health after load
- `SQLITE_BUSY` / "database is locked" occurrences: **0**
- Unhandled exceptions: **0**
- `PRAGMA integrity_check`: **ok**
- `journal_mode`: **wal**; WAL file grew to ~4 MB under sustained load, `-shm` 32 KB.

## Interpretation

- **SQLite was not the bottleneck.** Zero lock contention or busy errors even at
  concurrency 64 while writing thousands of usage rows. The single-writer model holds
  because usage/billing writes are **batched through one async writer** (channel +
  debounced flush), so requests do not each contend for the write lock — they coalesce.
- **The test ceiling was the mock upstream, not the gateway.** The non-2xx responses under
  load were `502`s from `Upstream HTTP request failed`: the single-core Python mock
  saturated its accept queue at high concurrency. The gateway degraded correctly (fast 502,
  no crash, no DB damage). A production upstream (or a compiled mock) removes this ceiling;
  re-run with the Docker/wiremock stack + k6 (`perf/ci/run-ga-compose-k6.sh`) for headline
  RPS numbers.

## Tuning notes / limits

- `busy_timeout=5000`, `synchronous=NORMAL`, WAL: **keep**. Verified sufficient for the
  observed write concurrency; no evidence for raising `busy_timeout` or moving to
  `synchronous=FULL` (which would cost latency for durability the deploy backups already
  cover).
- **WAL checkpointing:** the WAL reached ~4 MB, around SQLite's default
  `wal_autocheckpoint` (1000 pages ≈ 4 MB), which checkpoints passively on the writer. This
  was healthy here. For write-heavy sustained soak, monitor WAL growth; if it grows
  unbounded (e.g. a long-lived reader pins the WAL), add a periodic
  `PRAGMA wal_checkpoint(TRUNCATE)`. Not needed at the tested load.
- **Single writer is the scaling axis.** Throughput past this point is raised by reducing
  per-request write work or host resources (vertical), not replicas — the gateway is
  single-instance by design.

## Reproduce

Scratch harness used for this run (threaded mock + `ab`) is not committed; the durable path
is the Docker + k6 GA suite:

```bash
perf/ci/run-ga-compose-k6.sh     # full stack, wiremock upstream, k6 inference-rps
perf/ci/run-soak-local.sh        # longer soak to watch WAL growth / checkpoint behavior
```
