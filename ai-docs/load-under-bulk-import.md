# Database load under a bulk import: what costs, and the rules that keep it bounded

Measured on a consumer's shared 8-vCPU Postgres server carrying fourteen service databases, during
two 350-job bulk imports, after every stored form had been converted and every document index built.
Document indexes were present and used; stored-form reads had zero failures. The server still ran at
94 to 99 percent CPU for the length of each import and at a third of its capacity with every queue
empty. This file records where the time went, in measured order, and the rule each finding turned
into. `plans/archive/db-load-under-bulk-import.md` carries the stage table.

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

## Finding 1, round two: with acquisition bounded, the poll still cost what the instance held

Acquisition was only half of a poll. The other half is the re-offer: a busy instance holds as many
leased rows as its budget allows, has already handed most of them to a drain, and polls several
times a second to re-offer the streams it holds so the drain keeps its work list current. Every part
of that re-offer priced itself by the holdings. Measured again over a 339 to 346 second window with
157 in place, `claim_work` was still the top statement on every database by an order of magnitude:
1,278 calls at 401 ms and about 7,450 shared blocks each on the producer's database for 29 rows a
call, and 659 calls at 780 ms and about 19,000 blocks each on the largest consumer's for 128 rows a
call. Blocks to return a row, not blocks to find one.

Reproduced on a container with `auto_explain` (`log_nested_statements` on, so the plans inside the
function are logged) and per-table `pg_statio` / `pg_stat_user_indexes` deltas around one poll. Five
causes, each priced by the holdings and none by the batch:

- **The orphan guards.** Each of the three read `WHERE processed_at IS NULL AND (instance_id IS NULL
  OR lease_expiry < now)`. A disjunction has no index order, so proving that nothing is orphaned
  examined every pending row: on a busy instance, its whole holdings, three times a poll. They now
  probe twice: unowned rows under `instance_id IS NULL` through the outstanding-by-instance indexes
  (123), and expired leases at the head of a lease-expiry index per queue table (158). An instance
  whose leases are all live proves it at the first entry of each.
- **The outbox re-offer** ranked every held row per stream with a window function whose result the
  query never read, then sorted them all and kept a batch. It now walks
  `idx_outbox_held_arrival` (holder, arrival, id, covering, partial on pending singles) and stops
  at the batch.
- **The inbox re-offer** ranked every held row with three window functions and, because it selected
  `i.*`, fetched every held row's heap page to do it. With payloads wide enough that rows are not
  updated in place, that is one page per held row per poll.
- **The perspective re-offer** aggregated every held event to put streams with a hundred or fewer
  pending events ahead of larger ones. That tier guarded a batch of *rows* against one large stream;
  the drain has been per stream with an unbounded channel since Phase H, so a large stream no longer
  displaces small ones, and the tier is gone with the reason for it.
- **The inbox event-store chain**, which is how an inbox event leased at store time gets its
  event-store row and its perspective work, re-checked every held event against the event store on
  every poll, twice (a lock pass and an insert pass), when all but the newest had been chained long
  ago.

Both re-offers now enumerate the streams an instance holds through a lane index, one index-only
probe per stream, lane by lane in the order the batch is ordered (bucket, and for the inbox kind and
fresh-or-retried class as well), each lane's walk starting at a stream id drawn per poll, wrapping
once, and stopping at the batch. A recursive CTE with a `LIMIT 1` lateral per step is what makes
that one probe per stream rather than a scan; the upper bound of each step has to be written as a
`CASE` and not an `OR`, or it degrades from an index condition to a filter and the walk reads past
its stopping point. A held stream is re-offered as its oldest row in each lane it appears in instead
of as every row it holds there. The drain consumes stream ids and pulls a stream's rows on demand,
and the batch hooks fold a stream's returned rows to one number and one arrival, so the further rows
inside one lane carried nothing a caller read and ranking them was the whole cost. The chain now
reads only rows without `wh_inbox.chain_emitted_at` and stamps the ones whose event it finds in the
event store; a row whose insert hit `ON CONFLICT DO NOTHING` stays unstamped, because that clause's
contract is that the next poll re-attempts it.

One steady-state poll, returning 300 rows out of a batch of 100 per category, summed over the
outbox, inbox, perspective-event and event-store tables and their indexes:

| Held rows per table | Blocks before | Tuples before | Blocks after | Tuples after |
|---|---|---|---|---|
| 5,000 | 5,425 | 35,012 | 700 | 305 |
| 10,000 | 10,705 | 70,012 | 912 | 305 |
| 40,000 | 42,388 | 280,012 | 925 | 304 |

Eight times the holdings cost eight times as much before and 1.3 times as much after, and the
tuples a poll examines no longer depend on the holdings at all. Per row returned: 18, 36 and 141
blocks before against 2.3, 3.0 and 3.1 after.

**Rule:** a poll is priced by the batch it returns. Every part of it, the guards that decide
whether to call an acquisition, the re-offers and the bookkeeping at the end, has to reach its
answer through an index whose key order is the answer's order, with the `LIMIT` before any join or
sort, and must never read a row of a stream it is not going to return. Two ceilings hold it, and
neither is enough alone: blocks per call catches the heap fetches, and tuples examined per row
returned catches an index-only pass over the whole holdings, which is cheap in blocks and still
grows with the backlog. `ClaimWorkPlanShapeTests` asserts both, per table, once at a full budget of
holdings and again at double it.

**Two things deliberately left alone.** `count_outstanding_work` rides the claim's round trip and is
index-only: 159 blocks at 40,000 held rows per table, 11 percent of the fixed poll and about 1
percent of the broken one. It examines every held row in tuples, but migration 123 records why it
cannot be truncated or estimated (the budget would be reading its own output), so it stays exact
and the measurement is recorded here instead. And the block counters in `pg_statio_user_tables` are
cluster-wide, not per backend: autovacuum's reads of the pages a fill just wrote land inside a
measured window and read as poll cost. Vacuum the fill before measuring, or the number is not the
poll's. Tuple counters do not have this problem.

## Finding 1, round three: the outbox acquisition walked a live peer's backlog

The inbox acquisition got its window in 159; the outbox acquisition did not. It chose its heads by
walking every pending outbox row in arrival order and testing each for ownership until it had a
batch. Under a load the oldest pending rows are the unleased tails of streams a live peer owns (the
peer leases a run at a time, 171), so every other instance's poll walked all of them and found
nothing. Two calls captured with `EXPLAIN` on a deployed database visited 79,590,235 and 79,583,173
shared buffers to return 13 and 29 rows, almost all cache hits: the same pages, every poll.

Migration 194 gives it the inbox's shape: two lanes (unowned rows by arrival, expired leases by
expiry), each stopped at eight times the heads the call chooses, with ownership tested against sets
built once per call. `OutboxAcquisitionWindowSqlTests` gates the call at 750 shared buffers over a
40,000-row peer backlog (2,664 before), and `OutboxAcquisitionCostScenarioTests` measures 105 outbox
blocks a poll at both depths (726 and 2,673 before).

The trade is the inbox's and is pinned by a test: a takeable stream whose head arrived behind more
of a live peer's rows than the window holds is not reached until those rows drain.

A long claim also held the instance's own registration row whenever the heartbeat had gone stale:
`claim_work`'s self-heal writes `last_heartbeat_at` inside the claim's transaction, so
`record_heartbeat` for that instance waited for the whole claim (measured: 5.6 s behind a claim
held open for 6 s, against 0.2 s with a fresh registration). A slow claim made the heartbeat late,
and a late heartbeat made the next claim hold the row. Migration 196 removed the coupling (#1226):
the claim reads the row and never writes it, ranks the caller as live for its own claim, and raises
`whizbang.instance_registration_stale=true` when the row is missing or stale. `ClaimWorker` then
registers through `record_heartbeat`, in a statement of its own on its pinned connection, so
registration runs at startup and when needed, never inside a claim. `ClaimWorkRegistrationSqlTests`
holds a claim's transaction open and requires a heartbeat and a registration to complete under a
250 ms lock timeout; both failed before 196. A test that drives `claim_work` for several instances
has to register them, as `ClaimWorker` does: unregistered, each ranks itself alone and sees no live
peer.

**Rule:** the same as round two, for every acquisition: the bound is on rows examined, not on rows
found.

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

The closure still read the whole event store on every call to learn which lanes exist (`SELECT
DISTINCT COALESCE(origin_service_id, zero)`, no index on the expression): 138,603 buffers and 622 ms
per call on a 4,000,000-event store with nothing left to close. Migration 193 reads the lanes from
what already records them (the frontier rows, the digest buckets, the integrity ledger and the lane
index), one index entry per lane: 107 buffers. A local lane none of whose events has a digest bucket
is not found until one is folded; `DigestEpochLaneDiscoverySqlTests` pins both the cost and that
decision.

## Finding 3: the stamper sorted every unstamped row on every wake

`stamp_pending_commit_sequences` and its eligibility CTE ran 1,090 times in nine minutes at 118 to
198 ms each: 344 s on the consumer, 235 s on the producer. The CTE orders every unstamped row by
transaction id before taking a batch, and it ran whether or not anything was unstamped. The leader
now asks the partial index whether any row is unstamped first and runs the stamp only when the answer
is yes; a wake that finds nothing raises `OnStampSkipped`.

A backlog still cost every call a sort of the whole unstamped set, because transaction id order
cannot be indexed (#1062). Migration 197 orders the stamp by `insert_order`, an indexed insert-time
value, and keeps `xmin < horizon` only as the visibility fence (#1225's decision): the walk reads
unstamped rows in insertion order and stops at the batch, and each stream it reaches is stamped from
its lowest unstamped version, stopping at the first that is not yet eligible. Measured with the same
100-row stamp: 3,289 buffers behind 10,000 unstamped rows and 5,666 behind 160,000 before; 3,728 and
3,809 after (`CommitSequenceStreamOrderSqlTests`). The same change fixed what transaction id order
did to a stream: a transaction's id is assigned at its first write, not when it takes the stream's
lock, so a later version could be numbered below an earlier one, across two calls or within one
transaction. **Rule:** within a stream, `commit_sequence` follows the versions; a stamp's cost follows
its batch.

## Finding 3, round two: the liveness calls waited on locks, and what held them

Heartbeats, role votes, the lapsed-bridge expiry and due-schedule claims were reported at 8 to 16 s
on average while doing a few dozen blocks a round, so they were waiting. The harness reproduces it:
`LivenessUnderLoadScenarioTests` runs each of them on its own connection beside claims and a fenced
stamper over a backlog, with `log_lock_waits` on and a sampler reading `pg_blocking_pids()`, and
prints every wait with the statement that held it. On the SQL before 197 to 199:

- **The holder's renewal vote** waited behind the stamper's fenced transaction. `wh_assert_role_epoch`
  holds the role row `FOR SHARE` until the fenced stamp commits, and the renewal needs `FOR UPDATE`.
  That is the fence working; what bounds it is the stamp, which 197 made cost what its batch stamps.
  `RoleFenceWaitSqlTests` pins the wait as a design property.
- **`wh_end_lapsed_bridge`** waited behind the same stamper and behind the holder's vote, because it
  took the vote lock and the row lock before it looked. Every bridged instance that cannot take the
  legacy lock calls it on every vote cycle, and the answer is nearly always "nothing to end". 198
  looks first, without locks: 4,523 lock-wait samples before, none after.
- **A heartbeat that finds a stale peer** reaps it inline, and the reap's lease releases wait for
  rows a claim holds. 199 runs the reap under a 100 ms lock timeout and steps aside; maintenance and
  the next heartbeat reap it.
- Due-schedule claims and retry notifications waited for nothing in the harness. Their reported
  waits are not reproduced; #1217 records what was tried and what a capture would need.

**Rule:** a liveness call never waits for bulk work. Where it shares a row with a fenced duty, the
duty's transaction is bounded by its batch; anything opportunistic inside a liveness call steps aside
instead of waiting.

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

## Finding 6: one deadlock in a worker loop stopped the host

An instance added to a fleet under load started up and ran schema DDL under the lock (Finding 4).
The already-running instance's perspective consumer loop deadlocked against it inside the drain
fetch, the loop logged and rethrew, the exception left `ExecuteAsync`, and
`HostOptions.BackgroundServiceExceptionBehavior` (the default, `StopHost`) shut the process down.
The orchestrator restarted it: an instance gone for the length of a restart plus a schema
initialization, in the middle of the import, over a failure that would have passed on the next
attempt. The same rethrow made every perspective apply failure a potential host stop, because the
per-group catch reports, parks the row, and then rethrows into the same loop.

The rule: **no failure inside one batch or one tick may end a worker loop.** Two types carry it:

- `TransientDatabaseFailure.TryClassify` says what a caught exception is — deadlock, serialization
  failure, statement canceled, lock timeout, connection lost, insufficient resources, a command
  timeout the provider wrapped, or the provider's own transient flag. It reads `DbException.SqlState`
  and `DbException.IsTransient` only, so Core references no provider, and it walks
  `InnerException` and `AggregateException` the way `StoredFormUnreadable` does. A timeout or a lost
  socket counts only beneath a database exception: a wait that elapsed in application code is not the
  database failing.
- `WorkerLoopRecovery` is the one place a loop decides what to do with it. `Report` picks between the
  caller's two `LoggerMessage` methods — each worker keeps its own event ids and wording — and
  `RecoverAsync` adds a bounded backoff on the caller's `TimeProvider` (250 ms doubling to 30 s,
  snapped back by `Recovered()` on the next good iteration) for a loop that has no cadence of its own.

The perspective consumer loop and its drain pass now report each failed batch once at Error with the
reason, the SQLSTATE and the batch's stream ids, release those streams' unstarted rows through
`ReleaseUnstartedLeasesAsync` so a sibling takes them instead of waiting out the lease, back off, and
continue; the drain pass is guarded separately so a failed fetch does not cost the claimed per-event
work sharing its batch. A failure that is not the database's is reported as a defect under its own
event id and the loop still continues, because a stopped host reports nothing at all.
`BackgroundServiceExceptionBehavior` stays at its default on purpose: the loops are correct on their
own, and a worker that genuinely cannot run (a missing dependency at startup) should still stop the
host.

Audit of every `BackgroundService` in `Whizbang.Core` and `Whizbang.Data.Postgres` at the time of the
fix. Two let a failure out of `ExecuteAsync`:

- the perspective worker's consumer loop (logged, then `throw;`), now guarded;
- the outbox drain worker's batch body, which had a `finally` and no `catch` — the per-stream path
  isolates its own failures and the batched fetch degrades to per-stream fetches, but the batch
  envelope around them did not: the identity lookup, the security-context establishment, and the
  publish flush that ships the remainder from that same `finally`. It now has the guard its mirror,
  `InboxDrainWorker`, always had.

Everything else already caught per iteration and continued: the claim, dispatch, inbox drain, publish,
maintenance, heartbeat, dead-letter, integrity, schedule, stamper, durable-signal and flush workers;
the poll sources hand a failed tick to `OnTickError` and keep their timer; the batch flushers retry
inside `BatchFlusher` and drop with a line. Three residual notes worth a later pass: a
`when (ex is not OperationCanceledException)` filter used as a loop's *only* general handler still
lets a non-shutdown cancellation out (a statement the server canceled arrives as one) — the two
statistics collectors have that shape, and the perspective loop's own arms were rewritten to name
shutdown explicitly instead; `BacklogAgeWorker` *returns* on a cancellation rather than retrying; and
`InboxDispatchWorker`'s per-item catch itself writes to a channel, which can throw once the flusher
is disposed.

## Finding 7: a few long outbox streams drained one row per stream per claim cycle

The opposite shape from Finding 1: the database and the broker were idle and the backlog still took
minutes. A job produced tens of thousands of events onto a few dozen streams of several hundred
rows each, and the outbox
drained at about one row per stream per two-second claim cycle, 7 to 14 minutes in all. The drain
rate tracked how many streams still had rows, never the backlog or the claim settings. Three causes:
the claim handed the outbox acquisition its adaptive STREAM window (floor 25, grown only on inbox
evidence) as a ROW cap, so the oldest rows it leased were one row on each of the oldest streams; a
stream then waited for the next claim cycle for its next row; and a claim that took new rows on the
same streams read as a re-offer, so the loop napped before claiming again.

Migration 171 and the loop around it: a claim leases a run of each chosen stream's consecutive rows
under its own outbox row bound (`MaxOutboxRowsPerBatch`, `OutboxRunLength`); the drain continues a
stream from the lease it holds (`wh_continue_outbox_streams`) instead of waiting a cycle; a failed row
stops its stream and `process_outbox_failures` releases the rest of the run behind the retry; and a
claim whose outbox acquisition filled its bound (`whizbang.outbox_acquisition_full=true`) is followed
at once by another, bounded by `MaxOutstandingOutboxRows`. `OutboxStreamRunDrainMeasurementTests`
measures 880 claim cycles before and 2 after for 44 x 500 rows at a window of 25.

**Rule:** a stream count is never a row bound. A drain rate that scales with the number of streams
that have work, rather than with the work, means something is moving one row per stream per cycle.
Every run walk is priced by the run (`idx_outbox_stream_run`, keyed in the run's order), never by the
stream's backlog.

### Finding 7, round two: runs made the claim and the continuation deadlock

With runs in place, a service of several instances ran the first job of its kind after the upgrade:
a few hundred one-row streams plus their saga and completion events. The job's own work finished
in about 25 s, and the outbox tail, including the completion event a UI waits on, arrived about 45 s
later: one drain batch had failed with `40P01` and its streams waited for the claim loop. Nothing
was lost.

Reproduced by `OutboxStreamRunDeadlockSqlTests` with four instances, each running its claim, its
drain fetch, its continuation and its completion flush (with failure releases) concurrently over
overlapping streams, `log_lock_waits` on and a short `deadlock_timeout` in the test database. The
server log named the pair: `claim_orphaned_outbox` (inside `claim_work`) refreshing
`wh_active_streams` for the streams it had just leased, and `wh_continue_outbox_streams`, on the
SAME instance's drain session, renewing `last_activity_at` on the ledger rows of the streams it
continued. Each was one `UPDATE ... FROM` over several ledger rows, and each locked them in the
order its join produced: the claim in arrival order, the continuation in the order it was asked.
The same log showed a second, lesser cost: completions of rows the continuation had just leased
waiting for the whole `claim_work`, because the claim's run lock tested only a message id and so
locked rows that had changed hands since its snapshot, which it then never wrote.

Migration 172 and the worker around it: the continuation's ledger renewal skips a locked row, so
the continuation waits on nothing; the claim's refresh, the completion, the failure release and the
lease renewal take their rows in the one order below; the run locks re-assert takeability; and the
drain retries a continuation that lost a deadlock at once, from the same cursors, instead of leaving
its streams to the claim loop (`OutboxDrainWorker.CONTINUATION_TRANSIENT_ATTEMPTS`).

**Rule (lock order).** Every statement that locks more than one outbox or ledger row follows one
order, and a statement that need not wait does not:

1. Tables: `wh_outbox`, then `wh_active_streams` (the canonical order 162 set for the work tables:
   `wh_outbox`, `wh_inbox`, `wh_inbox_state`, `wh_perspective_events`, `wh_active_streams`).
2. Rows: `wh_outbox` by `(stream_id, created_at, message_id)`, which is `idx_outbox_stream_run`'s key
   and a stream's publish order; `wh_active_streams` by `stream_id`. Write it as a locking subquery,
   `SELECT ... ORDER BY <order> FOR UPDATE OF <alias>`, joined into the `UPDATE`/`DELETE`; a bare
   multi-row `UPDATE ... WHERE id = ANY(...)` or `UPDATE ... FROM` locks in whatever order the plan
   chooses, which is primary-key order, heap order or join order depending on the day.
3. A write that is not load-bearing (an activity timestamp, a run that can stop early) locks with
   `SKIP LOCKED` and treats a locked row as absent. Only a write that must happen waits, and then
   only in the order above. A statement that waits for nothing cannot be part of a cycle.
4. A lock re-asserts the predicate that chose the row. The rows a CTE read are the snapshot's; a
   lock whose `WHERE` tests only the id also locks a row that has since changed hands, and holds it
   to the end of the transaction without writing it.

5. A statement whose rows of one table are written along several paths takes them in ONE pass. Each
   path in order is not enough: two ascending runs are not one order. The outbox acquisition refreshed
   the ledger rows of streams it owned and then pinned the rest, each run sorted, and two claims that
   each refreshed a stream the other pinned deadlocked (#1238). It shows only when claims see no live
   peer and pin each other's streams, which a fleet whose heartbeats all go stale at once does.
   Migration 200 locks every existing ledger row of the claimed streams in one `stream_id` pass,
   whoever owns it, then refreshes and takes over rows it already holds, and inserts new streams after
   the pass, in the same order, `ON CONFLICT DO NOTHING`. `ConcurrentClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync`
   keeps the four instances unregistered for the whole run; an actor that registers after its first
   stale claim, as `ClaimWorker` does, hides the shape from the second round on.

   The rule is per TRANSACTION, not per statement. The inbox and perspective acquisitions had the same
   two-run shape, with an unsorted refresh, and one `claim_work` ran three acquisitions that each wrote
   their own streams' ledger rows: three passes, each sorted, are still three runs (#1256; 9 deadlocks
   in 44 rounds on an inbox, 88 in 64 with outbox and inbox work on the same streams). Migration 202
   gives every acquisition 200's shape and a `p_ledger_deferred` parameter; `claim_work` defers all
   three, collects the streams they leased, and writes the ledger once through
   `wh_lease_claimed_streams` after its last queue row. That also puts the ledger last in the claim,
   as rule 1's table order asks. `ClaimLedgerLockOrderSqlTests` pins both orders and reproduces both
   deadlocks; `ClaimLedgerWriteCostScenarioTests` measures the inbox and `claim_work` writes.

`OutboxStreamRunDeadlockSqlTests` pins each waiting statement's order the same way: another session
holds the first row in the order, the statement is started, and once `pg_stat_activity` reports it
waiting on a lock the test reads with `SKIP LOCKED` which of the later rows it already holds. In
order, it holds none. Each lock subquery is driven by the keys the statement was given, so its cost
is a sort of the batch, never of the table.

## Two consumer-side findings, recorded because the framework cannot detect them

- A consumer-owned trigger cast a document key's text to `timestamptz`, which fails on the canonical
  number: the rewrite's update of that table failed, and every later write would have. The rule and
  the SQL shape for reading either form are in `perspective-stored-forms.md`.
- Azure Query Store in `all` capture mode showed as `LWLock/pg_qs_hash` on eight of nineteen active
  sessions at peak. A server setting, not a framework one; worth `top` or `none` during load testing.
