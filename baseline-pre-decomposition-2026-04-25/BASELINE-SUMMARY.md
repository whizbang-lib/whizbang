# Whizbang work-pump baseline (pre-decomposition)

**Captured:** 2026-04-25 (saved as 2026-04-26 UTC inside postgres)
**Postgres uptime at capture:** 8,372 seconds (~2 h 19 m)
**Stack state at capture:** postgres up, .NET services NOT currently running (died after we restarted postgres mid-diagnostic; lifetime stats below reflect the period they WERE running)
**Whizbang version:** pre-decomposition (current `develop`/`main`)
**WAL position at capture:** `3/190E9FC8` (file `000000010000000300000019`)

This baseline captures the pre-change state to compare against post-decomposition results.

## Headline metrics

| Metric | Value |
|---|---|
| **Sustained `process_work_batch` calls/sec (per service, observed live)** | **~22/sec** |
| **Sustained calls/sec across 11-service stack** | **~242/sec** |
| **Mean cost of an empty call** | **~19 ms** (0 rows returned) |
| **Postgres container CPU during peak observation** | **~45%** |
| **Number of distinct prepared-statement variants per DB** | **~10** (live) / 2 (current snapshot — services dead) |
| **Connection count per service DB while services running** | **3-4** (one per scoped IWorkCoordinator) |
| **Architecture** | Two independent SQL pollers per service: `WorkCoordinatorPublisherWorker` (250 ms base) and `PerspectiveWorker` (1000 ms idle / 50 ms active) — both calling `process_work_batch` directly |

## pg_stat_statements snapshot — `process_work_batch` per DB

(Lifetime totals since last postgres restart at 2026-04-26 00:13:43 UTC)

```
         datname         | calls | tot_ms | mean_ms | rows
-------------------------+-------+--------+---------+------
 appservice-db           |  9784 | 183058 |   18.71 |    0
 chatservice-db          |  9784 | 183566 |   18.76 |    0
 integrationsservice-db  |  9784 | 183890 |   18.80 |    0
 jobservice-db           |  9784 | 182517 |   18.65 |    0
 notificationsservice-db |  9784 | 190458 |   19.47 |    0
 pdfservice-db           |  9784 | 187677 |   19.18 |    0
 taskservice-db          |  9786 | 188657 |   19.28 |    0
 uploadservice-db        |  9784 | 186588 |   19.07 |    0
 userservice-db          |  9786 | 185395 |   18.94 |    0
 workflowservice-db      |  9784 | 187111 |   19.12 |    0
```

**Pattern:** every DB symmetric (~9784 calls, ~187 sec total CPU per DB). Confirms the design issue — every service is generating identical idle load regardless of actual work.

**Note on lifetime average rate:** lifetime calls/sec/DB = 9784 / 8372 = ~1.17. That's much lower than the 22 calls/sec observed during the live diagnostic because services weren't running for most of postgres's uptime. The 22 calls/sec is the **actual sustained rate while services are running** and is the baseline number to beat.

## pg_stat_database — lifetime tuple churn

```
         datname         | xact_commit | tup_inserted | tup_updated | tup_deleted | tup_returned
-------------------------+-------------+--------------+-------------+-------------+--------------
 workflowservice-db      |      152936 |      2748907 |       33392 |     2746988 |     19328757
 appservice-db           |      151488 |      2763811 |       33978 |     2755798 |     20346513
 notificationsservice-db |      150537 |      2750186 |       33542 |     2748339 |     18750581
 userservice-db          |      149124 |      2754716 |       33667 |     2752178 |     24169149
 integrationsservice-db  |      148544 |      2749279 |       33404 |     2747213 |     20087105
 chatservice-db          |      144234 |      2755669 |       33904 |     2751305 |     19998229
 taskservice-db          |      142808 |      2750457 |       33550 |     2748339 |     19760085
 pdfservice-db           |      142404 |      2751085 |       33112 |     2749236 |     19023708
 uploadservice-db        |      140749 |      2752211 |       33552 |     2750364 |     19220485
 jobservice-db           |      140511 |      2754671 |       33650 |     2747235 |     19995011
```

Each DB has done **~2.75 million inserts and ~2.75 million deletes** lifetime. Tup_returned (~20 M) shows heavy SELECT activity from inside the function. This churn happens entirely *inside* `process_work_batch`'s temp-table machinery — the actual user tables show only a handful of rows (confirmed in earlier `pg_stat_user_tables` query).

## Top queries in `appservice-db` (full snapshot in 04-top-queries-bffservice.txt)

1. `process_work_batch` (~9784 calls, 19 ms mean, 187 sec total) — **dominant cost**
2. `register_instance_heartbeat` (~9784 calls, 0.30 ms mean, 3 sec total) — heartbeat embedded in poll
3. `DISCARD ALL` (20,280 calls) — Npgsql connection cleanup between EF Core scopes (overhead from per-tick scope creation)

## Host memory state (Mac dev box, 32 GB)

```
Pages free:           10,678 × 16KB = ~167 MB    ← critically low
Pages stored in compressor: ~14 GB physical       ← massive memory pressure
Swap usage:           3.67 GB / 5 GB used         ← swap exhausted
```

**Manifests as host sluggishness.** Postgres + 11 .NET services + Docker VM compete for very little free RAM, so any spike triggers compressor work and swap I/O.

## What the post-decomposition target looks like

| Metric | Baseline (this doc) | Target after Phase A+B+C+D |
|---|---|---|
| `process_work_batch` (or its successor `claim_work`) calls/sec/svc on idle | ~22 | ≤ 0.5 |
| Idle stack call rate (11 DBs) | ~242 | ≤ 6 |
| Mean cost of empty call | ~19 ms | ≤ 1 ms |
| Postgres container CPU on idle stack | ~45% | ≤ 2% |
| Distinct prepared-statement variants per DB | ~10 | 1 per query type per worker (or N/A — Npgsql Max Auto Prepare=0) |
| Heartbeat coupling | tied to claim poll | decoupled (5 s timer) |
| Inbox handler throughput | ~200/s (one fsync each) | ≥ 2000/s (savepoint-batched commit) |

## How to recapture for comparison

After landing changes, restart the a consumer application stack, let it idle 5 minutes, then run:

```bash
PGPASSWORD=postgres docker exec -e PGPASSWORD=postgres postgres psql -U postgres -c "SELECT pg_stat_statements_reset();"
# wait 5 minutes idle
PGPASSWORD=postgres docker exec -e PGPASSWORD=postgres postgres psql -U postgres -c "SELECT datname, calls, mean_exec_time, rows FROM pg_stat_statements JOIN pg_database d ON d.oid=dbid WHERE query !~ 'pg_stat' AND datname LIKE '%service-db' ORDER BY total_exec_time DESC LIMIT 30;"
docker stats --no-stream
```

Expect: `claim_work` ≤ 0.5 calls/sec/DB, mean ≤ 1 ms, rows = 0; `record_heartbeat` ~0.2 calls/sec/DB; postgres CPU ≤ 2%.

## Files in this directory

- `00-docker-stats-pre-reset.txt` — container CPU/memory at capture time
- `01-connection-counts-pre-reset.txt` — backends per DB (0 because services are dead)
- `02-process-work-batch-stats.txt` — pg_stat_statements for the function per DB
- `03-pg-stat-database.txt` — lifetime txn/tuple counts
- `04-top-queries-bffservice.txt` — full top-20 by total_exec_time
- `05-postgres-uptime.txt` — postgres start time + uptime
- `06-wal-position.txt` — WAL LSN at capture (for WAL-bytes-generated diff later)
- `07-host-memory.txt` — vm_stat + swap usage
- `BASELINE-SUMMARY.md` — this doc
