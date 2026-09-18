# Moving the inbox lease into a table of its own

Status: designed, prototyped and measured. No SQL written. Every number here is from a controlled
fixture at the shape stated beside it. Nothing in this document rests on statistics from a running
environment.

The cutover is **one migration in one transaction**, because customers take the latest package and
there is no sequence of releases to march anyone through. The lock window that buys is measured in
section 3.5 and is the number to argue with.

## 1. The case

`wh_inbox` holds a row that is **wide, heavily indexed, and written once**, and a lease that is
**narrow and rewritten constantly**. Those two things want to be separate tables, and the cost of
keeping them together is measurable.

| Column the claim writes | Indexes on `wh_inbox` naming it |
|---|---|
| `instance_id` | 12 of 26 |
| `lease_expiry` | 12 of 26 |
| `attempts` | 2 of 26 |

**The name.** `wh_inbox_state`, not `wh_inbox_lease`: the lease turned out to be one of nine mutable
columns the table holds, and a name describing one of nine misleads. The pair is `wh_inbox`, the
immutable message, and `wh_inbox_state`, its mutable work state. Renamed before release, when it cost
nothing.

### The boundary test: two questions, and the second one is the one people skip

**Applies to `wh_outbox` and `wh_perspective_events` when their turn comes. Deriving a column set
from question one alone produces a split that de-indexes a hot path, and `chain_emitted_at` below is
the worked example of exactly that.**

> **1. What rewrites this column?** Anything rewritten lives in exactly ONE table. A column nothing
> rewrites after insert may be copied to both, and copying is often right, because copies of a value
> that never changes cannot disagree.
>
> **2. What predicate reads it, and can that predicate still be covered by one index?** A rewritten
> column that is already single-homed passes question one and can still be in the wrong place, if
> leaving it behind splits a hot predicate across both tables.

Question one alone is necessary and not sufficient. `scheduled_for` is what question one catches:
rewritten, copied to both, would have diverged. `chain_emitted_at` is what only question two catches:
rewritten, single-homed, no split brain to avoid, passes question one cleanly, and still has to move
because the six-column predicate that reads it loses its only covering index otherwise.

`status` is the second column caught only by question two, and it is the one that settled the table's
identity. `claim_work`'s held-lane re-offer projects it, and `idx_inbox_held_lanes` INCLUDEs it so
that probe is index-only, one per stream, which is the bounded-cost property migration 150 exists to
establish. That index keys on `instance_id` and predicates on `processed_at`, both of which move, so
leaving `status` behind buys a heap fetch on the wide row per candidate stream: the exact cost this
migration removes. **Nine mutable columns, and 21 of 24 indexes move, so `wh_inbox` ends with three.**


**This is the rule, and it is stated as a rule because the column list is a consequence of it and
the next person deriving a boundary needs the question rather than the answer.**

> For every column, ask **"what rewrites this?"** A column that anything rewrites must live in
> exactly ONE table. A column that nothing rewrites after insert may be copied to both, and copying
> it is often right, because copies of a value that never changes cannot disagree.
>
> Then ask a second question: **"what predicate reads it, and can that predicate still be covered by
> one index?"** A rewritten column that is already single-homed passes the first test and can still
> be in the wrong place, if leaving it behind splits a hot predicate across both tables.

Asking instead "what does the claim read?" produces a boundary that looks correct and corrupts data.
`scheduled_for` is the counterexample that proves it: it reads like static routing data, it is not in
the claim's predicate in any obvious way, and `process_inbox_failures` **rewrites it on every
failure** to compute the retry backoff. Left on both tables, the two copies diverge, and the symptom
is a retry firing at the wrong time or never firing at all, with nothing anywhere naming the cause.
That is close to the worst failure mode available: silent, intermittent, and attributable to
anything.

**Correction to an earlier version of this document, and it was a latent bug rather than a wording
problem.** The split boundary is not "the columns claiming reads" but **the columns anything
rewrites**. Seven are mutable and must move: `instance_id`, `lease_expiry`, `attempts`,
`processed_at`, `scheduled_for`, `failure_reason`, `error`. The first draft kept `scheduled_for` on
both tables because it reads like static routing data, and it is not: `process_inbox_failures`
rewrites it on every failure to compute the retry backoff. Two copies of a value something rewrites
is a split brain, and it would have shown up as a retry firing at the wrong time or not at all.

The immutable columns are copied to both tables deliberately and safely, because copies of a value
that never changes after insert cannot diverge: `stream_id`, `received_at`, `is_event`,
`partition_number`, `priority`. They are on the lease table so the ordering gate and the lane picks
read one narrow table instead of joining back to a wide one.

**Correcting the count with the right drop list: 20 of the 24 indexes name one of the seven mutable
columns, so `wh_inbox` goes from 24 indexes to four** (the primary key, `idx_inbox_received_at`,
`idx_inbox_stream_pending`, `idx_inbox_source_cursor`). That is a larger win than the 15-of-24 this
document claimed before.

**A heap-only update is therefore impossible by construction.** A HOT update requires that no indexed
column changes; the claim changes three, all indexed. No `fillfactor` setting can help, because free
space on the page is not what is missing. A wide row closes the other door by fitting few tuples to a
page.

That prediction is confirmed at scale. Across three databases in one deployed fleet, over roughly six
million updates to `wh_inbox`: 4,006 HOT of 2,969,674; 5,434 of 3,084,970; 0 of 24,508. Under two
tenths of one percent. A lab fixture independently measured 0 of 660. **So every write to this table
maintains every index on it**, and that is the write amplification measured below.

This is the one claim in this document taken from a running environment, and it is safe to take
because it is structural rather than workload-dependent: it follows from which columns are indexed,
not from which queries happen to run.

**15 of the 24 indexes in the fixture (26 in the fleet) name `instance_id` or `lease_expiry`.** They
belong to claiming and they leave with it. That is the argument for the change, and it is much
stronger than the claim path's own saving: `wh_inbox` drops to nine indexes, so **every** write to it
gets cheaper, not only the claim.

## 2. What the prototype measured

Lab fixture: 20,000 inbox rows, 24 indexes, autovacuum off, nothing vacuumed, churned by repeated
claiming so the heap carries dead tuples the way a queue table under load does. The side table
carries every column `claim_orphaned_inbox` reads or writes and nothing else, with the three indexes
claiming needs. Same message ids on both sides, picked by primary key, five paired runs.

| | `wh_inbox` today | lease side table |
|---|---|---|
| Heap | 14,968 pages, 6,130 bytes/row | **249 pages, 101 bytes/row** |
| Indexes | 24 | **3 plus the key** |
| **Lease stamp** | **41.5 blocks/row** | **7.5 blocks/row** |
| Any other write (`chain_emitted_at`) | 41.0 blocks/row | 12.5 on the nine-index inbox |
| **Gated pick, batch of 10** | **582,640 blocks** | **1,317 blocks** |

The 41-blocks-per-row figure reproduces independently: `auto_explain` on the emit chain's
`chain_emitted_at` UPDATE measured 276 blocks to stamp 6 rows by a completely different method.

**On the pick, read the plan shape rather than the ratio.** Today the planner sequential-scans 19,460
rows and runs the per-stream ordering probe on every one of them, then sorts, then takes ten. On the
narrow table it walks the priority-ordered partial index and stops after 206 rows: the bound is
reached before the per-row work instead of after it. The 442x is real for this fixture and some of it
is the fixture's dead tuples; the change in plan shape is the durable part.

## 3. The five constraints

### 3.1 Atomicity, answered, and it moved the design

**The premise that the claim is a single atomic `UPDATE` is not what the code does.** It is already
two steps inside one CTE: a locking `SELECT ... FROM wh_inbox i ... FOR UPDATE OF i SKIP LOCKED`,
then an `UPDATE ... WHERE i.message_id = c.cand_message_id` over the rows that locked. So the split
does not turn one atomic statement into a join. It moves the lock from the wide row to the narrow
one, which is the object actually being contended, and that makes the lock **more** precise, not
less.

**But `processed_at` has to move too, and that is a correctness requirement rather than a
preference.** The claim predicate filters `processed_at IS NULL`. If that column stayed on
`wh_inbox` while the lock moved to the lease row, a completion setting `processed_at` between the
read and the lock would not be excluded, and excluding exactly that is what today's `FOR UPDATE` on
the inbox row does. With `processed_at` on the lease table the entire predicate reads and locks one
table, and atomicity is exactly what it is today.

**So the boundary is four claim-state columns, not three**, and completions write the lease table.

### 3.2 Per-stream ordering, proven rather than argued

The gate is a correlated `NOT EXISTS`: a row is claimable only if no earlier unprocessed event of the
same stream is itself claimable. It reads `stream_id`, `processed_at`, `is_event`, `received_at`,
`message_id`, `instance_id`, `lease_expiry`, `scheduled_for` and `partition_number`.

Every one of those is in the derived column set, so **the gate does not become a join at all**: it
is the same query against a different table. Run against both tables in an identical state (500 rows
leased, the rest free, mixed priorities and schedules):

| | rows admitted |
|---|---|
| Today, over `wh_inbox` | **4,892** |
| Split, over the lease table | **4,892** |
| Admitted by one and not the other, in either direction | **0** |

The column set is derived from the function rather than chosen: `message_id`, `stream_id`,
`received_at`, `partition_number`, `priority`, `is_event`, `scheduled_for`, `processed_at`,
`instance_id`, `lease_expiry`, `attempts`, `failure_reason`, `error`. `error` is on the list because
the claim writes it (attributing an attempt that expired silently) and reads `error IS NULL` to
decide. It is NULL for all but failed rows, so it costs a bit in the null bitmap and nothing else,
which the 101 bytes per row confirms.

This belongs in a test that runs, not only in this table. The test asserts the set equality above
against a fixture holding leased, free, scheduled and processed rows across several streams.

### 3.3 What else reads those columns: sixteen functions

`claim_orphaned_inbox`, `claim_work`, `fetch_inbox_batch`, `process_inbox_completions`,
`process_inbox_failures`, `release_unprocessed_inbox`, `release_unstarted_leases`, `renew_leases`,
`purge_orphan_inbox`, `move_to_dead_letters`, `count_outstanding_work`, `cleanup_stale_instances`,
`deregister_instance`, `perform_maintenance`, `store_inbox_messages`,
`_emit_event_store_chain_for_inbox`.

Every one follows the columns to the lease table or the split is incomplete. That is the size of the
change and it is the reason this is a migration rather than an edit.

### 3.4 Crash and orphan recovery

**The failure mode named in the brief, a row claimed in one table and free in the other, cannot
occur, because after the split claim state lives in exactly one place.** `wh_inbox` carries none of
it. Orphan reclaim looks for `lease_expiry < now()`, which is a lease-table predicate start to
finish, so every reclaim path keeps working unchanged in shape.

Dropping the columns rather than ignoring them also removes the failure mode that would otherwise
be invisible here: there is no longer a place for a stale lease to be written. A claim either finds
the lease table or raises.

**The split introduces a different risk and it is the one to test for: a missing lease row.** An
inbox row with no lease row is a message nothing can ever claim, and it fails silently, which is the
worst shape a defect can have. Two things prevent it:

- A foreign key from the lease table to `wh_inbox` on `message_id`, `ON DELETE CASCADE`, so the pair
  cannot be half-deleted.
- Both rows inserted by the same statement in `store_inbox_messages`, so the pair cannot be
  half-created.

And one thing detects it: an invariant test asserting that every unprocessed `wh_inbox` row has a
lease row, run after the fixtures that insert, claim, fail, expire and dead-letter messages.

### 3.5 The migration: one transaction, and the lock window it costs

Customers take the latest package, so there is no sequence of releases to march anyone through. The
cutover happens in one update or not at all. That rules out expand/migrate/contract and it makes the
design simpler rather than harder, for the reason the hazard analysis already contained:

**The danger was never the cutover. It was leaving the old columns in place while ignoring them.** A
claim that writes a column nothing reads succeeds, believes it holds a lease, and dispatches work a
second claimer will also take. A claim that *fails* is safe: no lease is taken, the row stays
claimable, and the worker retries. **Erroring loudly is the property that makes a single cutover
correct, and it is only available if the columns are gone rather than ignored.** So the migration
drops them.

#### The statement order, and why the lock comes first

```
BEGIN;
LOCK TABLE wh_inbox IN ACCESS EXCLUSIVE MODE;   -- before the backfill, not after
CREATE TABLE wh_inbox_state (...);
INSERT INTO wh_inbox_state SELECT ... FROM wh_inbox;
CREATE INDEX ... ON wh_inbox_state ...;          -- three of them
ALTER TABLE wh_inbox DROP COLUMN instance_id, DROP COLUMN lease_expiry,
                     DROP COLUMN attempts, DROP COLUMN processed_at;
CREATE OR REPLACE FUNCTION ...;                  -- the sixteen
COMMIT;
```

**Taking the lock first is load-bearing and is the answer to the consistent-snapshot question.** An
`INSERT ... SELECT` takes only `ACCESS SHARE`, which does not block writers, and under `READ
COMMITTED` each statement takes a fresh snapshot. Left to itself the backfill would therefore miss a
claim that commits after it and before the `ALTER`, and that row's lease would be dropped with the
column: claimed by a live worker, free in the new table, dispatched twice. An explicit
`ACCESS EXCLUSIVE` before the backfill closes that window completely. Every concurrent claim either
committed before the lock was granted, in which case the backfill sees it, or waits on the lock and
then fails on a column that no longer exists.

**This is why the lock window includes the backfill rather than just the drop**, and it is the cost
the design is buying correctness with.

#### The lock window, measured

Faithful copy of `wh_inbox`: same columns, same 24 indexes, 1.4 KB payload per row. The whole
transaction timed statement by statement.

| | 100,000 rows | 500,000 rows |
|---|---|---|
| Table size | 156 MB heap, 71 MB index | 781 MB heap, 331 MB index |
| `LOCK TABLE` | 0.03 ms | 0.06 ms |
| `CREATE TABLE` | 4.9 ms | 2.0 ms |
| **Backfill `INSERT ... SELECT`** | **373 ms** | **1,409 ms** |
| Three `CREATE INDEX` | 307 ms | 700 ms |
| **`ALTER TABLE ... DROP COLUMN` x4** | **1.7 ms** | **4.3 ms** |
| `COMMIT` | 7.1 ms | 21.7 ms |
| **Total stall** | **0.69 s** | **2.14 s** |

**The drop itself is free.** PostgreSQL's `DROP COLUMN` is a catalog update rather than a table
rewrite, and dropping the fifteen indexes that name those columns is a catalog update plus a file
unlink. Single-digit milliseconds at both scales. **The stall is the backfill and the three index
builds**, and those scale with the table.

**The rule to scale by: roughly 4.3 microseconds per row** on this hardware, which extrapolates to
about 4 seconds at one million rows and 43 seconds at ten million. Read those as the optimistic end.
This was measured on a local container with a warm cache and local NVMe; a database on network
storage with a cold cache can reasonably be several times slower.

**Whether that is acceptable depends on what the table is, and the honest answer is that it is
acceptable for a queue and not for an archive.** `wh_inbox` is a queue: rows are processed and
reaped, so its steady-state size is the backlog rather than the history. A few hundred thousand rows
is a service under load; ten million rows is a table that has stopped being reaped, which is a
different problem that this migration would merely reveal. If a deployment is known to carry a very
large inbox, the number to quote is the per-row rule and not the totals above.

**What was considered and does not help.** Backfilling outside the lock and catching up changed rows
under it would shorten the stall, but finding the changed rows means a full scan of the same wide
heap, which is the cost being avoided. It also gives up the single-transaction property that makes
rollback clean. The backfill has to read the wide heap once, and that read is the floor.

#### It genuinely fits one transaction, and this was checked rather than assumed

`SchemaCommandBoundary` applies a script with no commit-boundary marker as **one `NpgsqlCommand` on
one fresh connection**, which PostgreSQL runs as a single implicit transaction. That is not inferred:
`SchemaCommandBoundaryTests.WithoutTheBoundaryTheSameScriptStillFailsAsync` already pins it, by
running a marker-free script whose later statement fails and asserting that the earlier data rewrite
and index creation both rolled back. It was run again as part of this work and passes.

So the migration must carry **no** boundary marker. Nothing in it depends on an earlier statement's
committed effect: the three `CREATE INDEX` statements read rows the same transaction inserted, which
is the ordinary case and was verified working in the timing run above. The marker exists for the
opposite situation, an index over an expression on freshly rewritten data, which this migration does
not contain.

#### Old instances during the rolling deploy

Verified in the code rather than assumed. Every worker loop is
`while (!stoppingToken.IsCancellationRequested)` with a per-iteration `catch (Exception)` that hands
the exception to `WorkerLoopRecovery`. That classifies it, reports it, waits, and **returns so the
loop continues** on both branches: a `TransientDatabaseFailure` is reported as transient, anything
else as a defect, and neither stops the loop. A missing column raises SQLSTATE 42703, which is not
transient, so it is reported as a defect and the loop backs off from 250 ms doubling to a 30 second
ceiling and keeps trying until the pod is replaced.

**Nothing dead-letters the work.** Dead-lettering is driven by the attempt budget, and `attempts` is
bumped only by a claim that succeeds. A claim that throws bumps nothing, so a message cannot be aged
out by the failure window. Old instances take no work and lose none; new instances take it all.

#### Rollback, including the awkward case

If the migration fails at any point the transaction rolls back whole: no lease table, columns intact,
functions unchanged. The fleet is on old code against an unchanged schema and keeps working. Because
it is one transaction, the next instance to win the schema lock retries from exactly the same state,
which is the property `SchemaCommandBoundary` exists to protect in the general case.

**The case worth stating plainly is a failure after some instances have already restarted onto new
code.** Those instances run new SQL against the old schema, so their statements reference a lease
table that does not exist and raise SQLSTATE 42P01. That travels the same defect-and-retry path as
above: they back off, take no work, and lose none. Old-code instances keep working normally
throughout. The fleet degrades to reduced capacity rather than to data loss or double dispatch, and
it recovers when the migration succeeds on a later start.

## 3.7 Test coverage established before rewriting, and one methodology trap

The rule this follows: a rewrite verified by a suite that never calls it proves nothing, so coverage
is established BEFORE the rewrite, and a gap gets a test first.

### RULE: re-derive the function enumeration from the CURRENT drop list, every time it changes

**Imperative, because this was got wrong once and the same shape has now been got wrong three times
in this work.**

> The list of functions to rewrite is **derived from the current drop list, every time the drop list
> changes**. It is never carried forward from a previous derivation. Write the count beside the
> derivation so a stale number is visible rather than invisible.

What happened here: the first enumeration searched two columns (`instance_id|lease_expiry`) and
found **16** functions. The drop list then grew to four, seven, eight and nine columns without the
search being re-run. Re-derived against all nine it is **22**, and the six that were missing include
`recover_dead_letter`, which has two `INSERT INTO wh_inbox` statements writing `status` and
`attempts` and therefore constructs work state directly.

| Drop list | Functions, re-derived |
|---|---|
| 2 columns (the original guess) | 16 |
| 9 columns (current) | **22** |

This is the third instance of one failure mode: **a claim described by a search narrower than the
thing it describes.** The function-name search understated coverage; the `obj/generated` hits
overstated it; a partial column list understated scope. In every case the number looked authoritative
and was an artifact of the query. The rule generalizes: **state what a count was derived from, beside
the count, so the derivation can be checked rather than trusted.**

### The method, for the next person: search by wrapper, exclude obj, confirm it reaches a database

**Both errors appeared in the same search, in opposite directions, which is what makes this worth
writing down rather than remembering.**

| Step | Why |
|---|---|
| Search for the **C# wrapper name**, not the SQL function name | Tests call `PurgeOrphanInboxAsync` and never name `purge_orphan_inbox`, so the function name **understates** coverage to the point of reading as zero |
| Exclude `obj/` | Migration text is embedded in generated code, so most raw name hits are not tests at all and **overstate** coverage at the same time |
| Confirm the test runs against a real database | A hit in a mock, a `WorkCoordinatorMocks` helper, or a default-interface-implementation test executes no SQL |
| Confirm the test reaches the **arm** being changed | `RenewLeasesSqlTests` covers an existence check and the outbox arm; neither touches the inbox arm |

This is the same class of trap as a `<tests>` tag naming a test that cannot reach the line it claims,
and as a gate measure whose fixture never seeds the table it counts. A link or a count that looks
like coverage and is not is worse than an admitted gap.

**Searching for the SQL function name understates coverage badly, and nearly sent this work down a
false path.** Grepping the test tree for `purge_orphan_inbox` returns zero real test files, and for
`deregister_instance` returns a single code comment. Both look untested. Both are in fact covered,
because the tests call the C# wrapper (`PurgeOrphanInboxAsync`, `DeregisterInstanceAsync`) and never
name the function. The reverse trap is also present: most raw name hits are in `obj/generated`, where
the migration text is embedded in generated code, so a naive count overstates coverage at the same
time as the name search understates it. **Search by wrapper, exclude `obj/`, and confirm the test
runs against a real database rather than a mock or a default interface implementation.**

Measured baseline, all green before any rewrite: **128 tests across 18 classes**, covering all eight
of the claim-state functions.

| Function | Reached by |
|---|---|
| `release_unstarted_leases` | `BoundedAcquisitionRewriteSqlTests` (two direct calls) |
| `renew_leases` | `RenewLeasesSqlTests`, `ActiveStreamLeaseExpirySqlTests` |
| `count_outstanding_work` | `CountOutstandingWorkSqlTests` and three more |
| `deregister_instance` | `EFCoreWorkCoordinatorDeepPathTests`, `DapperWorkCoordinatorBroadTests` |
| `cleanup_stale_instances` | twelve classes |
| `purge_orphan_inbox` | `EFCoreWorkCoordinatorLifecycleAndJanitorTests`, `DapperWorkCoordinatorWithDataTests` |
| `release_unprocessed_inbox` | `InboxGracefulReleaseSqlTests`, `InboxAttemptAccountingBoundaryTests` |
| `process_inbox_failures` | `OutboxInboxFailureReasonSqlTests` and five more |

**One reclassification.** `purge_orphan_inbox` filters on `message_type`, which stays on `wh_inbox`,
so it needs a join and belongs in the second batch rather than the first. Batch one is seven
functions, not eight.

## 3.8 The build scaffold, and why it is not the staged migration the owner rejected

Verifying a batch at a time needs every intermediate state to be consistent, and it is not: a
rewritten function reading the lease table beside an un-rewritten one still writing `wh_inbox` gives
two representations that disagree, and the 128 baseline tests would fail for reasons that say nothing
about the rewrite.

So the migration carries a **temporary bidirectional sync trigger while the branch is being built**,
guarded against recursion by trigger depth. It keeps both representations in step so every batch can
be verified against the real suite. It is removed in the same commit that adds the column drops, and
the migration that ships is the single-transaction cutover described above.

This is the expand/migrate/contract mechanism used as a development scaffold rather than as a
shipping strategy. The distinction is not cosmetic: the reason staging was rejected is that consumers
would run a version where the old columns exist but are ignored, and a claim writing an ignored column
believes it holds a lease it does not. No consumer ever sees the scaffold.

## 3.9 Documentation and test annotations on the functions

SQL is code, so the `<docs>` and `<tests>` convention C# types already follow applies to the
functions too, in the migration that most recently defines each one. Ownership is by function rather
than by migration, since a function outlives the migration that introduced it.

Only a test confirmed to reach the function is named. `renew_leases` is the worked example of why
that matters: `RenewLeasesSqlTests` contains an existence check and an OUTBOX renewal, neither of
which exercises the inbox arm being rewritten. The inbox arm is covered, but by
`ActiveStreamLeaseExpirySqlTests`, whose helper passes the category through as a parameter, so the
link points there.

**One documentation gap, recorded rather than papered over:** `release_unprocessed_inbox` has no
documentation page anywhere on the site. Its `<tests>` links are present and its `<docs>` tag is
deliberately absent, with a comment in the migration saying so. Pages that do exist and are now
linked: `operations/workers/claim-backpressure`, `fundamentals/work-coordinator/claim-loop`,
`fundamentals/work-coordinator/overview`, `messaging/work-coordinator`,
`messaging/failure-handling`.

## 3.10 What a batch verification proves, and what it does not

Stated because the scaffold weakens it in a way that is easy to miss and easy to overclaim.

The scaffold keeps both representations equal by construction. So a test asserting on
`wh_inbox.instance_id` passes whether the function wrote the lease table and the trigger copied it
back, or wrote `wh_inbox` as before. **No runtime test can discriminate between those two while the
scaffold is in place.**

What a green batch therefore proves: the rewritten function applies, and it still computes the same
values and produces the same observable state transitions. That is behavior preservation, and it is
what a batch is for.

What proves the rewrite actually moved: removing the scaffold and dropping the columns. At that point
every function still reading `wh_inbox` for claim state fails loudly, and the tests that assert on
those columns fail too and have to move to the lease table. **Those failures are the verification,
not a regression**, and they are expected in the final commit rather than treated as a surprise.

## 3.6 Build status

| Piece | State |
|---|---|
| Structural DDL: table, lock, backfill, indexes, foreign key | **landed** in `162_InboxWorkStateSideTable.sql` |
| Build scaffold (temporary sync triggers) | **landed**, removed with the column drops |
| Batch one: six claim-state-only functions rewritten, annotated | **landed, 92 tests green** |
| Lease-table ownership tests | **landed and passing** |
| Commit-boundary guard test | **landed and passing** |
| Ordering set-equality test | **landed and passing** |
| Missing-lease-row invariant test | **landed and passing** |
| `ALTER TABLE ... DROP COLUMN` | not written: cannot land until every function is rewritten |
| `cleanup_stale_instances` (batch one b) | **landed** |
| Batch two: `purge_orphan_inbox`, `process_inbox_completions`, `fetch_inbox_batch` | **landed, 77 tests green** |
| `chain_emitted_at` moved to the lease table with its index | **landed** |
| Batch two remainder: `move_to_dead_letters`, `store_inbox_messages`, `_emit_event_store_chain_for_inbox` | **landed, 78 tests green** |
| Batch three: `claim_orphaned_inbox` and `claim_work` | **landed, 127 tests green. Both became PURE state-table functions** |
| Batch four: the six functions the re-derived enumeration found | not written |

The migration is deliberately NOT in `src/Whizbang.Data.Postgres/Migrations/` yet. Shipped
incomplete it would take `ACCESS EXCLUSIVE` on every startup and backfill a table nothing reads, and
the file has to ship whole or not at all: the columns cannot be dropped while fourteen other
functions still reference them.

The remaining work by function, measured from the live catalog rather than estimated:

| Function | Lines | References to the four columns |
|---|---|---|
| `claim_orphaned_inbox` | 619 | 139 |
| `claim_work` | 612 | 100 |
| `perform_maintenance` | 433 | 17 |
| `_emit_event_store_chain_for_inbox` | 345 | 43 |
| `move_to_dead_letters` | 161 | 23 |
| `store_inbox_messages` | 141 | 10 |
| `cleanup_stale_instances` | 132 | 28 |
| the other nine | 385 | 106 |

They divide into two kinds, and the division is what makes the job tractable rather than uniform.
Functions that touch only claim state (`release_unstarted_leases`, `renew_leases`,
`count_outstanding_work`, `deregister_instance`, `cleanup_stale_instances`, `purge_orphan_inbox`,
`release_unprocessed_inbox`, `process_inbox_failures`) become pure lease-table operations and get
simpler. Functions that also read the message body (`fetch_inbox_batch`,
`process_inbox_completions`, `move_to_dead_letters`, `store_inbox_messages`,
`_emit_event_store_chain_for_inbox`, and the two claim functions) need a real rewrite with a join
back to `wh_inbox` on the paths that need the payload.

## 3.11 Documentation gaps found while annotating

Running list, to be taken to the owner as a whole rather than fixed mid-migration: a missing page is
a documentation-side gap, not something to invent a path for. A function with no page carries its
`<tests>` links and no `<docs>` tag, with a comment in the migration saying why.

| Function | Status |
|---|---|
| `release_unprocessed_inbox` | **no page anywhere on the site** |

### The second boundary question, found in batch two

`chain_emitted_at` is rewritten, and it is already single-homed on `wh_inbox`, so the first boundary
question says it may stay. **It has to move anyway**, and finding out why is the most useful thing
batch two produced.

The emit chain's driving read is:

```sql
WHERE instance_id = ... AND lease_expiry > ... AND processed_at IS NULL
  AND is_event AND stream_id IS NOT NULL AND chain_emitted_at IS NULL
```

It runs on every claim poll, and `idx_inbox_chain_pending` serves it by keying on `instance_id` and
predicating on `processed_at`. **Both of those move, so that index cannot survive on `wh_inbox`.**
Leaving `chain_emitted_at` behind would split this predicate across two tables with no index able to
cover it, in the path measured at roughly 870 blocks per poll. That is the single change that would
have made this migration slower rather than faster, in its hottest path.

With the column on the lease table every term is local again and one partial index
(`idx_inbox_state_chain_pending`) covers the whole predicate. **Eight mutable columns move, not
seven.** The index count is unchanged at 24 down to 4, because the chain index was already counted as
moving.

## 3.12 Expected failures in the final commit

The final commit removes the scaffold and drops the eight columns, and these failures are the
verification rather than regressions. Listed as they are found so none is a surprise.

| Test | Why it will fail, and what it becomes |
|---|---|
| `EmitChainInboxIndexTests.EmitChainInboxIndex_ExistsAfterMigrationsAsync` | Asserts `idx_inbox_chain_pending` exists on `wh_inbox`. That index keys on `instance_id` and predicates on `processed_at`, both of which move, so it cannot survive. Repoint at `idx_inbox_state_chain_pending`. |
| `EmitChainInboxIndexTests.EmitChainInboxIndex_HasExpectedPartialPredicateAsync` | Same index, same reason. |
| Any test asserting on `wh_inbox.instance_id`, `lease_expiry`, `attempts`, `processed_at`, `scheduled_for`, `failure_reason`, `error` or `chain_emitted_at` | The column is gone. Move the assertion to `wh_inbox_state`. |
| `InboxWorkStateIsTheOwnerSqlTests` (all four) | These PASS and become discriminating at that point. Their docstring says they are not discriminating yet and must be updated to say they now are. |

## 4. Scope

`wh_inbox` only, as the proof. `wh_outbox` and `wh_perspective_events` have the same shape and very
likely the same problem, and they follow only if this one lands and pays.

## 5. What is not claimed here

- **No read-side number for the claim poll as a whole.** The 442x above is one query at one fixture
  shape. The poll's discovery cost against the production lane indexes has not been measured on the
  split, and quoting a whole-poll improvement before that measurement exists would be an overclaim.
- **No conclusion about which indexes are unused.** The 15-of-24 count is a fact about which columns
  are indexed. Whether any of the nine that stay is needed is a separate question that scan counts
  from a lightly exercised environment cannot answer.
