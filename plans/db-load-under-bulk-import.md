# Database load under a bulk import

Measured on a consumer's shared 8-vCPU Postgres server (fourteen service databases) during two
350-job bulk imports, after the canonical temporal storage unification had converted every stored
form and built every document index. Document indexes were present and used; stored-form reads had
zero failures. The server still ran at 94 to 99 percent CPU for the length of each import, and at
32 to 34 percent with every queue empty. This plan records what consumed it, in measured order, and
the fix for each.

## 1. Findings, from evidence

Method: statement and table statistics snapshotted before and after each run (`pg_stat_statements`
joined on user, database, query id and top-level flag; `pg_stat_user_tables`), and `pg_stat_activity`
sampled every half second for the length of the second run. The statement cache on that server
thrashes (4,810 of 5,000 entries, 3.2 million deallocations), so a statement missing from a diff is
not evidence of absence; the session samples are the ground truth for occupancy.

### 1.1 The claim poll costs in proportion to the backlog

In a 561-second window, `claim_work` consumed 1,594 s on the producer's database (1,660 calls,
960 ms mean, 49,000 shared blocks per call), 892 s on the largest consumer's (565 calls, 1.58 s
mean) and 351 s on a second consumer's. Three databases spent 2,837 s inside the poll in 561 s, about
five of the eight cores. The table statistics say why: the outbox was sequentially scanned 8,344 times
for 105 million tuples in that window, the perspective-event table 3,320 times for 21 million, the
inbox 3,621 times. Those tables are empty between runs, so the plans cached inside the function
were planned against empty tables and scan them whole once they fill. Every instance polls four
times a second, so the moment a backlog forms the server saturates and the backlog grows faster.
Scaling the producer from two to four instances lengthened the run from 4 min 36 s to 6 min 30 s.

### 1.2 Maintenance runs during the load it should wait out

For the entire ten-minute sample, `close_digest_epochs` occupied about two backends on the largest
consumer and one on the producer, and `perform_maintenance` about two on the producer. The digest
closure probes each foreign lane with `origin_service_id = X AND origin_commit_sequence BETWEEN`,
and no index covers those columns: EXPLAIN shows a parallel sequential scan of the 4.9-million-row
event store per probe, about twenty scans per maintenance tick. The maintenance worker's busy check
counts unprocessed inbox rows and active leases, so a producer whose load sits in its outbox reads as
idle and runs its purges at the peak.

### 1.3 The commit-order stamper

`stamp_pending_commit_sequences` plus its eligibility CTE consumed 344 s on the largest consumer and
235 s on the producer in the window: roughly half a core per busy database.

### 1.4 Server-side lock contention

Azure Query Store in `all` capture mode put 8 of 19 active sessions on `LWLock/pg_qs_hash` at peak.
The producer's sessions also waited on `LWLock/LockManager`: the inbox carries 22 indexes and the
outbox 17, so a poll or store exceeds the sixteen fast-path lock slots and takes the shared lock
manager on every call.

### 1.5 DDL at startup under load deadlocks

An autoscaler-started producer instance ran the bootstrap script for the core infrastructure tables
while maintenance and the work-available poll sources were running, and all three deadlocked
(four `40P01` in two seconds). The schema was already current; nothing needed DDL.

### 1.6 Idle cost

With every queue empty the two busiest databases committed 122 and 182 transactions a second from
the polling loops (`count_outstanding_work`, due-schedule counts, existence probes, settings reads:
26,322 sequential scans of `wh_settings` in nine minutes on the producer).

## 2. Fixes

| Stage | Change | Status |
|---|---|---|
| L1 | `claim_work` bounded by the batch, not the backlog: an arrival-order covering index for the outbox acquisition and an urgency-order one for perspective events (157), the perspective acquisition choosing streams from a bounded window of the most urgent events (150), and `plan_cache_mode = force_custom_plan` on the poll so no session keeps plans made for empty tables | done |
| L2 | Index `(origin_service_id, origin_commit_sequence, created_at)` on the event store for the digest-epoch lane probes (155); the closure's own per-tick bound already exists | done |
| L3 | Maintenance busy check counts outbox and perspective-event backlog too, and epoch closure and purges defer while any is non-trivial (bounded, as today); a deferred sweep is logged at Information with every count | done |
| L4 | Stamper: a wake with nothing unstamped skips the stamp entirely (the partial-index probe decides), reported through `OnStampSkipped`; batch size and intervals unchanged, since the cost was the sort running when there was nothing to sort | done |
| L5 | The bootstrap records the hash of the closure it applied in `wh_bootstrap_closure` (created by 000's region) and an instance whose closure is recorded applies nothing: no statement, no lock, no wait | done |
| L5b | Second defect from the same rollout: the perspective-table pass ran on every start ("PerspectiveTables=(completed)" beside "skipped (hash match)" for the other phases) because per-perspective hash rows were keyed by the model's simple type name and models nested under feature holders share it, so one row served several tables and the first compared always read as changed; the DDL it re-applied under the lock is what deadlocked an instance starting under load. Entries are keyed by table name now; proven by a generator test (two `Model`s, two table-named entries) and an initializer test where the second start of a schema with colliding names takes the fast path (no slow-path log, no lock, no row touched) | done |
| L5c | Third defect from the same rollout: a server that refuses `pg_trgm` failed the whole perspective pass ("Failed to create perspective table"), and every start paid a failed attempt before the retry. The trigram indexes now sit in one optional-extension block per table script (one `CREATE EXTENSION`), applied under a savepoint by `OptionalExtensionBlocks`: a refusal skips the block with one warning naming the extension and the indexes, and the pass completes. Both scripts the generator writes carry the block, the hash-tracked one and the single-script fallback, because a trigram index outside the block fails on a server with no `gin_trgm_ops` operator class with nothing to skip; `NoTrigramIndexIsEmittedOutsideABlockAsync` reads every script rather than one | done |
| L5d | Same rule, other engine: the Dapper initializer's per-perspective entries (`PerspectiveSchemaGenerator`, consumed by `PostgresSchemaInitializer`) are still keyed by the perspective **class's** simple name, so two perspective classes nested under feature holders would share one hash row exactly as L5b's models did. Left out of the L5b change deliberately: it is a second generator and a second initializer with their own tests and CI shard, and nothing observed it, so it is recorded here rather than folded into a fix for the EF Core pass | open |
| L5a | Defect found in the first rollout of L5: the infrastructure script carried a clock stamp in a comment, so every instance hashed a different closure and none ever skipped. The builders no longer stamp the clock and the hash ignores comment-only lines and line endings; proven by a builder contract test, hash unit tests, and a phase test where scripts differing only in comments count as recorded | done |
| L6 | Store-backed pull sources back off when idle: after three empty ticks the interval doubles per tick to a ceiling of one minute, and a hit or a push-transport flip restores the base cadence | done |
| L7 | Outbox and inbox failure functions read the element the runtime writes (`MessageId`, `Reason`) as well as the older names, as the perspective function already does, so `failure_reason` stops reading Unknown for every row (156) | done |
| L8 | A drain-path apply failure of any kind parks each leased row through the failure channel, as the stored-form path does, so the rows back off and dead-letter instead of being re-claimed forever behind a cursor failure that names no row | done |
| L9 | Tag payload-size thresholds bind from configuration under `Whizbang:Tags` (`PayloadSizeWarningThresholdBytes`, `PayloadSizeErrorThresholdBytes`, and per tag under `...ByTag:{tag}`); the processor resolves the per-tag value first, then the global one | done |

Ordering: L1 removes most of the peak; L2 and L3 remove the maintenance load from the peak; L5 is a
correctness item (deadlocks); L4 and L6 are efficiency; L7 and L8 were deferred from the stored-form
work and change what a failed row records; L9 was found alongside (six warnings per message on a
tag whose payloads are legitimately wide, with no configuration to raise the line).

The settings-snapshot idea from the first draft of L6 was dropped on the numbers: the settings reads
cost a tenth of a millisecond each. The transaction rate at idle is connection churn (a `DISCARD ALL`
per pooled connection returned, several hundred a second per database) and the probes themselves;
the backoff addresses the probes, and the churn is a consumer connection-string decision
(`No Reset On Close`) recorded in the migrations documentation.

## 3. Consumer-side items found alongside

- A consumer-owned trigger casting a document key's text to `timestamptz` failed the stored-form
  rewrite for that table and would have failed every later write (rule recorded in
  `ai-docs/perspective-stored-forms.md`).
- A grid stopped rendering because sorting had been removed from resolvers the web still ordered
  inline; the assessment that removed it counted type references in generated code and missed
  hand-written operations. Assess by the operations the web actually sends.
- Query Store `all` capture mode on the shared development server is worth reducing to `top` or
  `none` during load testing.
