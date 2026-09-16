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

Every instance polls several times a second, so a backlog saturates the server and grows itself;
scaling the producer out made the run slower. Two causes, both reproduced with `auto_explain` on the
test container (nested statements on, so the plans inside the function are logged):

- **Two of the three acquisitions were bounded by the backlog.** The inbox acquisition had been
  bounded by 138, 145 and 150. `claim_orphaned_outbox` orders its candidates by `created_at` and
  stops at the row bound, but no index carried that order over the pending rows, so the planner
  scanned every pending row, sorted them all, and kept a batch. `claim_orphaned_perspective_events`
  chose its most urgent streams by aggregating every claimable event. Migration 157 adds
  `idx_outbox_pending_arrival` (arrival order, covering, partial on pending singles) and
  `idx_perspective_event_urgency` (priority order, covering, partial on pending), and 150's
  perspective acquisition now takes its candidate streams from a bounded window of the most urgent
  events (`LIMIT GREATEST(p_max_streams, 1) * 8` walked from that index) while a `selected_streams`
  join still captures a chosen stream in full, so per-stream ordering is unchanged.
- **Plans made for empty tables outlived the empty tables.** The queue tables are empty between
  loads. plpgsql caches a generic plan after a few executions, and a session that polled while the
  tables were empty kept plans made for empty tables, which scanned them whole, nested, once they
  filled: the same poll that takes well under a second on fresh plans did not finish inside the
  command timeout. `claim_work` now carries `SET plan_cache_mode = force_custom_plan`, which applies
  to everything it calls, so every poll plans for the tables as they are. (A `SET` clause alone does
  not re-apply a migration; the prosrc comparison needs a body change, so 150 carries a comment.)

**Rule:** the poll's cost is bounded by the batch it returns, never by the backlog it polls over,
and no plan the poll runs may be one made for a table of a different size. `ClaimWorkPlanShapeTests`
reproduces both shapes (poll empty, fill with wide rows, poll again on the same session; once with
the rows leased elsewhere, once unowned) and asserts the tuples one poll reads stay within a few
batches per table.

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

The hash is a function of the statements and nothing else. The first rollout of the record found
every instance of one release computing a different hash, because the infrastructure schema script
began with a comment stamping the current time, so no instance ever skipped. The rule that came out
of it: a schema builder must not write anything per call into the SQL it returns (no clock, no host,
no process), and `SchemaBootstrapPhase.ClosureHash` drops comment-only lines and normalizes line
endings before hashing, so a header or a note added later cannot split one release into two
closures. Script names and order still take part, because a region moved or renamed is a different
closure.

The same rollout showed a second reason DDL ran on every start, this one inside the initializer's
own transaction: the phase summary read `PerspectiveTables=(completed)` beside `skipped (hash match)`
for every other phase. The per-perspective hash rows (`perspective:<name>` in
`wh_schema_migrations`) were keyed by the model's simple type name, and a service that nests its
models under feature holders has many models with one simple name (`Order.Model`, `Invoice.Model`,
several `SagaModel`s). Those models shared one row: whichever wrote last owned it, the one compared
first read as changed on every start, the fast path was refused, and the perspective pass re-applied
`CREATE TABLE` and `CREATE INDEX ... IF NOT EXISTS` for the colliding tables under the schema lock.
`IF NOT EXISTS` still takes a relation lock before finding nothing to do, and that lock is what an
instance starting under load deadlocked on. The entries are keyed by table name now, the one name
unique to a perspective within its schema. The rule: any key that gates a startup phase must be
unique for what it gates; a simple type name is not a key.

A third finding from the same start: a server that refuses `CREATE EXTENSION pg_trgm` (a managed
server that does not allow-list it answers `0A000`; a role without the privilege `42501`; a build
without it `58P01`) failed the whole perspective pass, because every substring index emitted its
own extension statement as ordinary DDL inside the initializer's transaction. Every start paid a
failed attempt and the trigram indexes were never built. The generator now emits the extension once
per table script inside an optional-extension block (`-- @whizbang:optional-extension pg_trgm` to
`-- @whizbang:optional-extension-end`), and `OptionalExtensionBlocks.ApplyAsync` creates the
extension under a savepoint, skips the block with one warning naming the extension and the indexes
when the server refuses, and applies everything else. The generator writes the perspective schema
twice, once per table for the hash-tracked pass and once as a single script for the fallback the
pass takes when it cannot read the tracking tables, and the block has to be in both: a trigram index
emitted outside the block that creates the extension reaches a server with no `gin_trgm_ops`
operator class as an ordinary statement and fails the pass with nothing to skip, which is a worse
outcome than the defect it replaced. `NoTrigramIndexIsEmittedOutsideABlockAsync` reads every script
the generator writes rather than one of them, for that reason. The rule: an index family a server
may refuse is optional by construction; a declaration must never be the reason a service fails to
start.

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
