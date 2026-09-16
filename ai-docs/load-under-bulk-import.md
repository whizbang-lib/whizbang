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
