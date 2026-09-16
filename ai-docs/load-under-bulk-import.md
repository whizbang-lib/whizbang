# Database load under a bulk import: what costs, and the rules that keep it bounded

Measured on a consumer's shared 8-vCPU Postgres server carrying fourteen service databases, during
two 350-job bulk imports, after every stored form had been converted and every document index built.
Document indexes were present and used; stored-form reads had zero failures. The server still ran at
94 to 99 percent CPU for the length of each import and at a third of its capacity with every queue
empty. This file records where the time went, in measured order, and the rule each finding turned
into. `plans/db-load-under-bulk-import.md` carries the stage table.

## How to measure this, and how not to

- **Snapshot and diff, never read cumulative counters.** `pg_stat_statements` and
  `pg_stat_user_tables` are cumulative since a reset that may be weeks old. Snapshot both before and
  after the window (join `pg_stat_statements` on user, database, query id and the top-level flag) and
  report the deltas. A per-statement mean read from the cumulative table is dominated by history.
- **The statement cache thrashes under a polling workload.** On that server it held 4,810 of 5,000
  entries with 3.2 million deallocations, so a statement absent from a diff proves nothing. Sample
  `pg_stat_activity` every half second for the window as well; occupancy by statement and wait
  event is the ground truth the cache cannot lose.
- **A statement still running at the end of the window is not in the diff at all.** The epoch
  closure and the maintenance sweep each ran for minutes and appeared nowhere in the statement
  deltas; the session samples showed them occupying two to three backends continuously.
- **Sequential scans of a hot table are the tell.** `seq_scan` and `seq_tup_read` deltas on the
  queue tables and the event store name the statement class before any plan is read.

## Finding 1: the claim poll cost in proportion to the backlog

In a 561-second window `claim_work` consumed 1,594 s on the producer's database (1,660 calls at
960 ms, 49,000 shared blocks each) and 892 s on the largest consumer's (565 calls at 1.58 s). Three
databases spent 2,837 s inside the poll in 561 s: five of eight cores. The outbox was scanned whole
8,344 times for 105 million tuples, the perspective-event table 3,320 times, the inbox 3,621 times.

The queue tables are empty between loads. A session that polls while they are empty carries plans
made for empty tables, and once the tables fill those plans scan them whole on every poll. Every
instance polls several times a second, so a backlog saturates the server and grows itself; scaling the
producer out made the run slower.

**Rule:** the poll's cost is bounded by the batch it returns, never by the backlog it polls over,
and a plan the function caches must not be able to stick to an empty-table shape.
`ClaimWorkPlanShapeTests` reproduces the pathology (poll empty, fill with wide rows leased
elsewhere, poll again on the same session) and asserts the tuples read stay within a few batches.

## Finding 2: maintenance ran at the peak

`close_digest_epochs` occupied about two backends on the consumer and one on the producer for a
whole ten-minute sample; `perform_maintenance` about two on the producer. Two causes:

- The digest closure probes each foreign lane with
  `origin_service_id = X AND origin_commit_sequence BETWEEN`, and no index covered those columns:
  EXPLAIN showed a parallel sequential scan of the 4.9-million-row event store per probe, about
  twenty per tick. Migration 155 adds `idx_event_store_origin_lane`, partial on rows that have a
  lane; `DigestEpochLaneIndexTests` proves the probe shape is served by it.
- `HousekeepingCoordinator`'s busy verdict read `ServiceBacklog.UnprocessedInboxRows` and
  `ActiveLeasedRows` only. A producer's load sits in its outbox and a draining consumer's in its
  perspective events, so both read as idle. `ServiceBacklog` now carries bounded counts of both,
  `IsSettled` needs all four measures at zero, and a deferred sweep is logged at Information with
  every count.

**Rule:** settledness is measured on every work table, and the sweep says why it waited.

## Finding 3: the stamper sorted every unstamped row on every wake

`stamp_pending_commit_sequences` and its eligibility CTE ran 1,090 times in nine minutes at 118 to
198 ms each: 344 s on the consumer, 235 s on the producer. The CTE orders every unstamped row by
transaction id before taking a batch, and it ran whether or not anything was unstamped. The leader
now asks the partial index whether any row is unstamped first and runs the stamp only when the answer
is yes; a wake that finds nothing raises `OnStampSkipped`.

## Finding 4: DDL at startup under load deadlocks

An autoscaler-started instance applied the bootstrap closure while the maintenance sweep and the
work-available poll sources ran, and all three deadlocked (four `40P01` in two seconds). The closure
is idempotent DDL, and `CREATE INDEX IF NOT EXISTS` on an existing index still takes a share lock
before it discovers there is nothing to do. The bootstrap now records a hash of the closure it
applied in `wh_bootstrap_closure` (created by the closure itself, in migration 000) and an instance
whose closure is recorded applies nothing: no statement, no lock. See
`schema-initialization-connections.md`, trap 4.

## Finding 5: the idle cost is polling and connection churn

With every queue empty the two busiest databases committed 122 and 182 transactions a second. The
work-available and due-schedule pull sources now back off after three empty ticks, doubling per empty
tick to a one-minute ceiling, and return to their base cadence on the first hit or on a gate flip
(`PollIdleBackoff`, `BasePollSignalSource`). What remains is connection churn: each probe opens and
closes a pooled connection and the driver's reset (`DISCARD ALL`) is a transaction of its own,
several hundred a second per database. That is a consumer connection-string decision
(`No Reset On Close`) and is documented on the claim-loop page, not something the framework can
decide for a consumer.

## Two consumer-side findings, recorded because the framework cannot detect them

- A consumer-owned trigger cast a document key's text to `timestamptz`, which fails on the canonical
  number: the rewrite's update of that table failed, and every later write would have. The rule and
  the SQL shape for reading either form are in `perspective-stored-forms.md`.
- Azure Query Store in `all` capture mode showed as `LWLock/pg_qs_hash` on eight of nineteen active
  sessions at peak. A server setting, not a framework one; worth `top` or `none` during load testing.
