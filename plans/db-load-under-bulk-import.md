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
| L1 | `claim_work` bounded by the batch, not the backlog: an arrival-order covering index for the outbox acquisition and an urgency-order one for perspective events (157), the perspective acquisition choosing streams from a bounded window of the most urgent events (150), and `plan_cache_mode = force_custom_plan` on the poll so no session keeps plans made for empty tables. Round two (158): the poll is priced by the batch, not by the instance's holdings either: the orphan guards probe two index heads instead of scanning the pending rows for a disjunction with no index order, the outbox re-offer walks an arrival index to its batch, the inbox and perspective re-offers walk lane indexes one index-only probe per stream (bucket, kind, fresh or retried) from a stream id drawn per poll and stop at the batch -- a held stream is re-offered as its oldest row in each lane it appears in, not as every row it holds there -- and the inbox event-store chain reads only rows without `wh_inbox.chain_emitted_at` and stamps the ones whose event is in the event store. One steady-state poll returning 300 rows: 700 blocks and 305 tuples at 5,000 held rows per table and 925/304 at 40,000, against 5,425/35,012 and 42,388/280,012 before. `count_outstanding_work` was measured and left exact (159 blocks at 40,000 held rows; 123 records why it cannot be truncated) | done |
| L2 | Index `(origin_service_id, origin_commit_sequence, created_at)` on the event store for the digest-epoch lane probes (155); the closure's own per-tick bound already exists | done |
| L3 | Maintenance busy check counts outbox and perspective-event backlog too, and epoch closure and purges defer while any is non-trivial (bounded, as today); a deferred sweep is logged at Information with every count | done |
| L4 | Stamper: a wake with nothing unstamped skips the stamp entirely (the partial-index probe decides), reported through `OnStampSkipped`; batch size and intervals unchanged, since the cost was the sort running when there was nothing to sort | done |
| L5 | The bootstrap records the hash of the closure it applied in `wh_bootstrap_closure` (created by 000's region) and an instance whose closure is recorded applies nothing: no statement, no lock, no wait | done |
| L5b | Second defect from the same rollout: the perspective-table pass ran on every start ("PerspectiveTables=(completed)" beside "skipped (hash match)" for the other phases) because per-perspective hash rows were keyed by the model's simple type name and models nested under feature holders share it, so one row served several tables and the first compared always read as changed; the DDL it re-applied under the lock is what deadlocked an instance starting under load. Entries are keyed by table name now; proven by a generator test (two `Model`s, two table-named entries) and an initializer test where the second start of a schema with colliding names takes the fast path (no slow-path log, no lock, no row touched) | done |
| L5c | Third defect from the same rollout: a server that refuses `pg_trgm` failed the whole perspective pass ("Failed to create perspective table"), and every start paid a failed attempt before the retry. The trigram indexes now sit in one optional-extension block per table script (one `CREATE EXTENSION`), applied under a savepoint by `OptionalExtensionBlocks`: a refusal skips the block with one warning naming the extension and the indexes, and the pass completes. Both scripts the generator writes carry the block, the hash-tracked one and the single-script fallback, because a trigram index outside the block fails on a server with no `gin_trgm_ops` operator class with nothing to skip; `NoTrigramIndexIsEmittedOutsideABlockAsync` reads every script rather than one | done |
| L11 | Test infrastructure found while verifying the above: a fixture that issues its own `CREATE DATABASE` loses the race against the dozens of sibling fixtures doing the same thing against one container, and the loss lands in `[Before(Test)]`, so the runner reports whichever test held the slot as failing in milliseconds before an assertion runs. `SharedPostgresContainer` now offers `PerTestDatabaseFactory.CreateAsync`/`DropAsync`, which retry that contention with a bounded backoff on a `TimeProvider`, defer to `TransientDatabaseFailure` for what is transient plus `55006` for the template race, and treat `42P04` as an earlier attempt of its own having won. Adopted by the seven classes this work touched. **Open: 27 classes in `Whizbang.Data.EFCore.Postgres.Tests` and 40 across all test projects still create databases raw**, each one a chance of the same failure; migrating them is mechanical but is a separate change, because each conversion is a chance to break a working fixture | partly done |
| L12 | Blind spot found in the quality gate itself while verifying L11: the gate's diff side counted every added line under `src/` (`Find-UncoveredNewLines.ps1` keys on the path), while `codecoverage.config` excludes `.*\.Testing\.dll$` from instrumentation. A line added under `src/Whizbang.Testing/` was therefore counted on the diff side and invisible on the coverage side, so it could never be listed as uncovered whatever it did, and the gate's summary claimed every added library line was executed by a test while one project's lines were unmeasurable. Green either way is not a gate. Two resolutions existed: instrument the assembly (stricter) or exclude it from the diff side (cheaper). The owner chose the exclusion, and the diff side now passes `:(exclude)src/Whizbang.Testing/**`, checked by listing the diff side both ways, the two `src/Whizbang.Testing/` entries going and the other eighteen files staying | done |
| L13 | A window that has stopped receiving churn samples holds silently, and nothing says adaptation is no longer being driven. The unmeasured-versus-clean distinction itself is already made and made carefully, in `ClaimChurnFeedback.Take` (whose contract says an `Observed` of zero is unmeasured, not clean), in `ClaimWorker._fetchedAttempts` (null for that case), and in `ClaimChurnSignal.Measurement.IsMeasurable`, which gates growth while leaving a shrink ungated. What is missing is the consequence being observable: `LogClaimWindowResized` fires only when the width changes, so a window frozen because samples stopped arriving reads exactly like a window that is correctly steady, and the difference is the difference between adaptivity working and adaptivity being dead. The evidence it is real is the flake fixed alongside this: a churn report consumed a cycle early left the window pinned at its peak for the rest of the run, and the only thing that noticed was a test asserting a width relationship. Wants its own design decision, probably a line when a window goes some number of cycles unmeasured, and its own tests; nothing in this pull request needs it and none of it is implemented here | open |
| L12a | **What L12 did not do: `src/Whizbang.Testing/` is still not gated at all.** The exclusion stops the gate overstating what it verifies; it does not make that project's code covered, and nothing now checks whether a line added there is executed by a test. Today seven tests drive `PerTestDatabaseFactory`, by intent rather than by enforcement. Closing this means instrumenting the assembly, which is the stricter reading of L12 and remains the owner's call, and it must remove the `:(exclude)` pathspec in the same change: the diff side and the instrumentation side have to stay in step, which the script says at its call site, because excluding a project from one and not the other is what opened this hole | open |
| L5d | Same rule, other engine: the Dapper initializer's per-perspective entries (`PerspectiveSchemaGenerator`, consumed by `PostgresSchemaInitializer`) are still keyed by the perspective **class's** simple name, so two perspective classes nested under feature holders would share one hash row exactly as L5b's models did. Left out of the L5b change deliberately: it is a second generator and a second initializer with their own tests and CI shard, and nothing observed it, so it is recorded here rather than folded into a fix for the EF Core pass | open |
| L5a | Defect found in the first rollout of L5: the infrastructure script carried a clock stamp in a comment, so every instance hashed a different closure and none ever skipped. The builders no longer stamp the clock and the hash ignores comment-only lines and line endings; proven by a builder contract test, hash unit tests, and a phase test where scripts differing only in comments count as recorded | done |
| L6 | Store-backed pull sources back off when idle: after three empty ticks the interval doubles per tick to a ceiling of one minute, and a hit or a push-transport flip restores the base cadence | done |
| L7 | Outbox and inbox failure functions read the element the runtime writes (`MessageId`, `Reason`) as well as the older names, as the perspective function already does, so `failure_reason` stops reading Unknown for every row (156) | done |
| L8 | A drain-path apply failure of any kind parks each leased row through the failure channel, as the stored-form path does, so the rows back off and dead-letter instead of being re-claimed forever behind a cursor failure that names no row | done |
| L9 | Tag payload-size thresholds bind from configuration under `Whizbang:Tags` (`PayloadSizeWarningThresholdBytes`, `PayloadSizeErrorThresholdBytes`, and per tag under `...ByTag:{tag}`); the processor resolves the per-tag value first, then the global one | done |
| L10 | Workers survive transient database failures: `TransientDatabaseFailure` classifies a deadlock, serialization failure, canceled statement, lock timeout, lost connection, exhausted resources, a wrapped command timeout or a provider-flagged failure from `DbException` alone, and `WorkerLoopRecovery` is the one place a loop decides what it caught, reports through the loop's own event ids and waits a bounded backoff (250 ms doubling to 30 s, reset by the next good iteration) on the loop's `TimeProvider`. The perspective consumer loop and its drain pass no longer rethrow: each failed batch is reported once at Error with the reason, the SQLSTATE and the batch's stream ids, its unstarted rows are released for a sibling to take, and the loop continues; a failure that is not the database's is reported as a defect under its own event id and the loop still continues. The outbox drain worker gains the per-batch guard its inbox mirror always had, and the claim poll and inbox drain name the classification on the line they already wrote. Evidence: an instance added to a fleet under load ran schema DDL under the lock (L5), a running instance's perspective consumer loop deadlocked against it inside the drain fetch, the loop logged and rethrew, and the host's default `BackgroundServiceExceptionBehavior` (`StopHost`) stopped the process mid-load — a restart plus a schema initialization lost to a failure the next attempt would have won | done |

Ordering: L1 removes most of the peak; L2 and L3 remove the maintenance load from the peak; L5 is a
correctness item (deadlocks); L4 and L6 are efficiency; L7 and L8 were deferred from the stored-form
work and change what a failed row records; L9 was found alongside (six warnings per message on a
tag whose payloads are legitimately wide, with no configuration to raise the line). L10 is what makes
the rest survivable: until it landed, any of these deadlocks could end a worker loop and, with it,
the host.

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

## 4. Measured and deliberately not changed: the adaptive notify debounce is inert

Recorded so the next person does not re-derive it, and so nobody re-tunes a threshold against a
baseline that is about to move. From a deployed fleet's `wh_notify_state` counters and the statement
deltas for one bulk import:

| Measure | Value |
|---|---|
| Lifetime | 62,302 fired against 8,340 suppressed, a 11.8 percent suppression rate |
| In the window | 8,466 rings through `_notify_debounced` against 574 suppress-path updates, 6.3 percent |
| Of those rings | 6,099 took the fire path; the other 2,367 (28 percent) bypassed the state machine entirely through the `NOT FOUND` branch, where a `SKIP LOCKED` miss rings and returns |
| `effective_window_ms` | 50 for every kind, which is `notify_debounce_floor_ms` |
| `rapid_run` | maximum 1 for outbox and inbox, 3 for perspective, against `notify_churn_run` = 5 |

**The escalation to the seven-second ceiling has never engaged.** The volume axis needs five
consecutive doorbells to the same (instance, kind) each within `notify_rapid_gap_ms` (100 ms). The
observed aggregate is about seven rings a second spread over 142 targets, so roughly one ring per
target per twenty seconds: the real gap is about two hundred times wider than the threshold, so
`rapid_run` resets to zero every time and the window stays at the 50 ms floor. A 50 ms floor almost
never suppresses, because suppression additionally requires `claim_work` to have armed `last_work_at`
inside that window.

**Why nothing changed here.** Suppressing a doorbell trades latency for database work, and that trade
was worth making while a doorbell cost about nineteen thousand block reads. Once the deterministic
branch stopped recovering a stream's partition number by reading the queue table (migration 141, in
place), a doorbell is a hash and a small join, and the observed rate is not pathological: tightening
the debounce would buy little and add delivery latency. Re-calibrating against the old baseline would
be the same mistake as tuning on any stale measurement.

**Rule:** re-measure these counters after the scan fix has run under load before anyone moves
`notify_rapid_gap_ms`, `notify_churn_run` or `notify_debounce_floor_ms`. The 28 percent that bypasses
the state machine through the `SKIP LOCKED` miss is worth a second look at the same time: it is a
correctness-preserving fall-through (a contended watermark rings rather than waits), but it means the
measured suppression rate understates what the state machine would do if it saw every ring.

## 5. Measured and deliberately not changed: the emit chain's `chain_emitted_at` stamp

The emit chain costs about 1,920 block reads per claim poll, and the question was which part pays.
From `auto_explain` with `log_nested_statements` on a single poll in a lab fixture (100 streams at
depth 200, four claimants, churning, autovacuum off, deliberately not vacuumed):

| Statement | Blocks | Rows |
|---|---|---|
| `pg_advisory_xact_lock(hashtext('wh_event_store:' ...))` | 25 | 0 |
| `WITH inbox_events AS (...)`, the chain proper | 25 | 0 |
| `UPDATE wh_inbox SET chain_emitted_at = ...` | **276**, 6 buffers dirtied | 6 |

**The reading is already cheap and the writing is not.** Both chain queries found nothing to chain
and cost 25 blocks each, which is what the `idx_inbox_chain_pending` index of migration 158 was for:
that optimization works. Of the UPDATE's 276 blocks, the Nested Loop that finds the rows is 30, so
about 246 blocks went to stamping six rows, roughly **41 blocks per row stamped**.

**Why it costs that.** `chain_emitted_at` appears in `idx_inbox_chain_pending`'s partial predicate,
so writing it removes the row from that index. Changing index membership makes the update non-HOT by
definition, and a non-HOT update maintains every index on the table. `wh_inbox` carries 26.

**Why nothing changed here, and why reversing 158 would be wrong.** The stamp is a one-time cost per
row that exists to stop the chain re-examining the same rows on every later poll. Trading 41 blocks
once against a scan every poll is the right direction; removing the index to cheapen the stamp would
restore the recurring scan that cost more. The cost is real but it is bounded by the number of rows
chained, not by the backlog, which is the property that matters.

**Rule:** a partial index whose predicate names a column the writer sets makes every write to that
column non-HOT. That is usually still the right trade when the write is once per row and the read is
once per poll. State the trade when adding such an index, so the write cost is not later mistaken
for a defect.

## 6. Investigated and declined: `wh_active_streams` is two pages

A fleet's statistics showed `wh_active_streams` at 6,079 sequential scans reading 1,543,468 tuples,
which is 698 tuples read per row in the table and looks alarming next to every other ratio. It is
not worth changing, and the reason is the page count:

| Measure | Value |
|---|---|
| Rows | 88 |
| Heap pages | **2** |
| Cost inside one claim poll | **68 blocks**, about 2 percent of the poll |

A sequential scan of a two-page table is two block reads, and the planner picks it over an index
scan because it is genuinely cheaper. The 698 tuples per row is the same two pages read many times,
not work. **Tuples read per row only implies pages read when the table is large enough for the two
to correlate**; on a table that fits in two pages the ratio is noise. Record the page count beside
any tuples-per-row figure before treating the ratio as a finding.

## 7. Open investigation: `wh_inbox` takes no HOT updates, and declares its indexes in two places

Raised rather than acted on. The write amplification is structural and measured; the question of
what to do about it is not answerable from the data available today, and the reason why is worth
recording as carefully as the finding.

**HOT is effectively zero at production scale.** Across three databases in one deployed fleet, two
high-volume and one low-volume, over roughly six million updates to `wh_inbox`:

| Database | HOT updates | Total updates | HOT share |
|---|---|---|---|
| High-volume A | 4,006 | 2,969,674 | 0.13 percent |
| High-volume B | 5,434 | 3,084,970 | 0.18 percent |
| Low-volume C | 0 | 24,508 | 0 percent |

A lab fixture independently measured 0 of 660. `fillfactor` is default everywhere, but raising it
would not help: the claim update writes `instance_id`, `lease_expiry` and `attempts`, and
`idx_inbox_instance_lease` indexes the first two by design, so the update changes index membership
no matter how much free space the page has. Wide payloads close the other door by fitting few
tuples per page. **So every one of the 26 indexes is maintained on every update**, which is the
write amplification section 5 measured at one statement's scale.

**One source-code fact belongs with this, independent of any statistics.** `wh_inbox` indexes are
declared in two places: the SQL migrations and `src/Whizbang.Data.Schema/Schemas/InboxSchema.cs`.
Grepping the migrations for an inbox index therefore does not enumerate them, and a migration that
drops an index the schema descriptor still declares will see it recreated. Anyone counting or
changing indexes on this table has to read both sites. This is a fact about the repository rather
than a claim about any index, and it holds whatever a workload's statistics say.

**The index count is the open question, and it cannot be answered from statistics.** Whether 26 is
the right number for this table is exactly the kind of question scan counts look like they answer
and do not. An index that guards a condition a given workload never reaches records zero scans and
is indistinguishable from an index nothing will ever need. Every environment available to measure
today is lightly exercised, so its statistics can say where to look and can never say what to
change.

**What this investigation is blocked on.** A workload that actually exercises the paths each index
was added for, so that a scan count means "not needed" rather than "not reached". That is a
measurement to build, not an opinion to gather, and until it exists no index on this table should be
proposed for removal on the strength of how unused it looks.

**Calibrate the ceiling before anyone spends time here.** Even a correct reduction in the index count
cannot make the claim update HOT, because the columns that update writes are indexed by live indexes
that lease reclamation and orphan detection need. The available win is proportional to the indexes
removed, and it is a fraction of the amplification rather than an end to it. Worth knowing before
the work starts.

**Rule that generalizes past this table:** a number from a controlled fixture at a stated data shape
is reproducible and falsifiable, and a number from a lightly exercised environment is neither. Both
are worth reading. Only the first is worth concluding from. Section 6 is an instance of the same
error caught earlier: a ratio that looked like a finding until the page count explained it.
