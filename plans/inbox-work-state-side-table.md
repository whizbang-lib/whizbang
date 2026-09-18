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
| Batch four: five functions rewritten, one needed no change | **landed, 59 tests green** |

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
| `EFCoreWorkCoordinatorDeepPathTests.RecomputePartitionNumbersAsync_MismatchedRows_RecomputesAllThreeTablesAsync` | Reads `wh_inbox.partition_number`. That column moves. Repoint at `wh_inbox_state`. |
| Any test asserting on `wh_inbox.instance_id`, `lease_expiry`, `attempts`, `processed_at`, `scheduled_for`, `failure_reason`, `error` or `chain_emitted_at` | The column is gone. Move the assertion to `wh_inbox_state`. |
| `InboxWorkStateIsTheOwnerSqlTests` (all four) | These PASS and become discriminating at that point. Their docstring says they are not discriminating yet and must be updated to say they now are. |

## 3.12b RULE: derive an enumeration by machine, then check it against an independently known fact

**Stated because this has now failed four times in one piece of work, always silently and always in
the direction of too few.**

> Any mechanical enumeration is **derived by machine and then checked against at least one fact
> established independently of the machine.** The failure mode of a pattern narrower than the thing
> it describes is silence, not error, so nothing in the output announces it.

The four instances, all one cause:

| Enumeration | The pattern | Direction of the error |
|---|---|---|
| Which tests cover a function | searched the SQL function name | **understated** to zero; tests call the C# wrapper |
| The same search | counted `obj/generated` hits | **overstated**; those are embedded migration text |
| Which functions to rewrite | searched two of the columns | **understated**, 16 against 22 |
| Which columns are mutable | `SET ... (?:WHERE\|FROM)` capture | **understated**; it terminated on the word "from" inside a comment, losing `scheduled_for` and `error` |
| The column count itself | enumerated my own lab fixture | **overstated**, 25 against 23; the fixture had drifted |
| The index count | the same fixture | **understated**, 24 against 26; the fixture was built from migrations only and never got `InboxSchema.cs`'s indexes |
| Whether the SUITE had the same gap | grepped the generated `.g.cs` for the index name | **would have understated**; that DDL is built at RUNTIME from the descriptor, so the name is never in the generated file by design. Caught by reading the call, not the output |

**The reusable mechanism, which is the part worth keeping:** the trap is not "fixtures drift". It is
that **a schema with two sources of truth has build paths, and a build path may read only one of
them.** Here the two sources are the SQL migrations and `InboxSchema.cs`. The product's path reads
both. My hand-built lab read only the migrations. Any future build path, tool, fixture or diagnostic
that constructs this schema has to be checked against both sources, and "it produced a working
database" does not distinguish a path that read one from a path that read two.

Six instances now. Each output looked authoritative. In every case the check that caught it was
comparing the machine's answer to something already known to be true: "`process_inbox_failures` writes `scheduled_for`, so
why is it absent?" is what found the fourth, and "the migrations contain only four `ADD COLUMN`
statements" is what found the fifth.

### The count was wrong, and the reason was the worse of the two possibilities

`wh_inbox` has **23 live columns.** I reported 25. The cause was not unfiltered `pg_attribute`,
which would have been harmless: **I enumerated my own lab fixture, which had drifted from the product
schema.** Earlier in this work I hand-patched that fixture with `ALTER TABLE ... ADD COLUMN IF NOT
EXISTS` to get a probe running, and two of those columns do not exist in the product at all:
**`envelope_type` and `envelope_data`.**

Reconciled against the authority that settles it: databases initialized by the product's own schema
pass report 23. The migrations contain four `ADD COLUMN` statements against this table and no
`DROP COLUMN`, which matches.

**Effect on the partition: none.** Both phantom columns were classified write-once, in no index,
payload, "stays", so they contributed nothing and no mutable column was missed. The partition is over
**23 live columns: 10 move, 5 are copied, 8 stay.** Effect on the measurements: negligible, because
both columns were always NULL in the fixture and cost a bit in the null bitmap, but the fixture has
been corrected so later numbers are taken against the real shape.

**A fixture that has drifted from the schema is worse than a miscount, because the measurements rest
on it.**

> **RULE, for a fixture specifically:** the independent check is a **schema diff against a
> product-initialized database**, never a recollection of what was patched. A fixture is the one case
> where the machine and the check can be the same wrong artifact, so the check has to come from
> outside the fixture entirely.

### The diff, run rather than reasoned about, and it was not empty

Removing the two columns I remembered patching answered the wrong question. The risk was not "were
those two material", it was "the fixture had drifted and I did not know". So: columns, types,
nullability, defaults, indexes and constraints across all six tables the measurements touch, diffed
against a database the product had just initialized. **Eighteen differing lines.**

**The material one: my lab had 24 indexes on `wh_inbox`. The product creates 26.** Missing were
`idx_inbox_instance_lease` and `idx_inbox_partition_claiming`, and the cause is systematic rather
than random: **both are declared in `InboxSchema.cs`, and my lab was built from the SQL migrations
alone**, so it never received the schema descriptor's indexes. That is the same two-declaration-sites
trap recorded in the bulk-import findings, arriving from the other direction. 26 also matches the
figure measured on a deployed fleet, so two independent sources agree and my fixture was the outlier.

The remaining differences do not touch the partition: `flags` nullability on three tables, an
`updated_at` column on `wh_active_streams`, `wh_outbox.destination` nullability, a `CURRENT_TIMESTAMP`
against `now()` default, and two indexes I had created by hand during the probe experiments.

**The direction matters and it is the favorable one: the drift made the BEFORE number optimistic.**
A 24-index table is cheaper to write than a 26-index table, so every "cost today" figure taken on
that fixture understated today's cost, and therefore understated the improvement. Corrected, the
stamp reduction is 69 percent rather than 65. That is luck, not method: it could as easily have gone
the other way, which is the whole argument for running the diff rather than reasoning about it.

## 3.12c The integration suite was checked and is sound

The question the fixture drift raised: does the suite the performance scenarios run on initialize
through the product's own schema pass, or through the migrations alone? If the latter, every SQL
measurement in that suite was taken against a 24-index table while production has 26, and the
ceilings we are promoting into CI would be calibrated against a schema no consumer has.

**Answered by reading the path, because tests passing cannot distinguish these.** The chain:

1. `EFCoreTestBase.InitializeDatabaseAsync()` calls `dbContext.EnsureWhizbangDatabaseInitializedAsync()`.
2. That generated method calls **`PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(schemaConfig)`
   at runtime** (`DbContextSchemaExtensionTemplate.cs`, and the generator's own comment says "Core
   infrastructure schema is generated at runtime by PostgresSchemaBuilder" precisely so it is not
   baked into the generated file).
3. `PostgresSchemaBuilder` reads `InboxSchema.Table`, which declares `idx_inbox_partition_claiming`
   and `idx_inbox_instance_lease`.
4. Then the SQL migrations are applied on top, hash-tracked in `wh_schema_migrations`.

**So the product's path reads BOTH declaration sites, and the suite's databases carry 26 indexes**,
which is what they measure at and what a deployed fleet measures at. Only the hand-built lab was
short. The performance scenarios and their ceilings are calibrated against the production shape.

**This nearly became the seventh instance of the pattern.** Grepping the generated `.g.cs` for
`idx_inbox_partition_claiming` returns zero, which reads exactly like "the suite does not create it".
It returns zero because that DDL is assembled at runtime from the descriptor and is deliberately not
in the generated file. The answer came from following the call, not from searching the output.

One incidental correction: the per-test databases are **pooled and reused** rather than created per
run, so none of them is a reliable "current head" reference either. A run that creates no new database
is evidence of reuse, not of a fresh build.

## 3.13 The exhaustive column pass: all 23 live columns, both questions

Done because the set had grown four, seven, eight, nine by discovery, each time triggered by hitting
something. That is not evidence the set has stopped growing. `wh_inbox` has **23 live columns**,
which is small enough to answer definitively. (An earlier version of this section said 25, counted
from a drifted fixture; see 3.12b.)

Question one was answered mechanically rather than by reading: every `UPDATE` statement in every
function body that targets `wh_inbox`, comments stripped, matched against each column name. Eleven
such statements exist. **Ten columns are mutable, fifteen are write-once.**

| Column | Q1: what rewrites it | Q2: needs index coverage on the state table | Verdict |
|---|---|---|---|
| `message_id` | nothing | yes, the join key and every lane's tiebreak | **copy** |
| `handler_name` | nothing | no, in no index; a projection only | stays |
| `message_type` | nothing | no, in no index | stays |
| `event_data` | nothing | no, payload | stays |
| `metadata` | nothing | no, payload | stays |
| `scope` | nothing | no, payload | stays |
| `stream_id` | nothing | yes, the ordering gate and the lane walk | **copy** |
| `partition_number` | **`recompute_partition_numbers`** | keyed in 0, included in 8 | **MOVE** |
| `is_event` | nothing | yes, the gate and the lane bucket | **copy** |
| `status` | `process_inbox_completions`, `process_inbox_failures` | included in the held-lane index for an index-only probe | **MOVE** |
| `attempts` | `claim_orphaned_inbox`, `release_unprocessed_inbox`, `release_unstarted_leases` | in the lane's fresh/retry split | **MOVE** |
| `error` | `claim_orphaned_inbox`, `process_inbox_failures` | no | **MOVE** |
| `instance_id` | six functions | keyed in 4, included in 2 | **MOVE** |
| `lease_expiry` | seven functions | keyed in 4, included in 6 | **MOVE** |
| `failure_reason` | `claim_orphaned_inbox`, `process_inbox_failures` | no | **MOVE** |
| `scheduled_for` | `process_inbox_failures` | keyed in 1, included in 7 | **MOVE** |
| `processed_at` | `process_inbox_completions` | the partial predicate of 15 indexes | **MOVE** |
| `received_at` | nothing | yes, arrival order in every lane | **copy** |
| `source_service_id` | nothing | no, only the source-cursor index, which stays | stays |
| `source_commit_sequence` | nothing | no, same | stays |
| `priority` | nothing | yes, the lane bucket expression | **copy** |
| `chain_emitted_at` | `_emit_event_store_chain_for_inbox` | the chain's partial predicate | **MOVE** |
| `flags` | nothing | no, in no index | stays |

**Ten move, five are copied, eight stay: 23 columns accounted for.** `wh_inbox` keeps its thirteen write-once columns and three
indexes (`wh_inbox_pkey`, `idx_inbox_received_at`, `idx_inbox_source_cursor`). `wh_inbox_state` holds
fifteen columns (ten mutable plus five write-once copies) and six indexes. **21 of 24 indexes move.**

`partition_number` is the tenth and it was found only by this pass. It looks like static routing
data, which is exactly what `scheduled_for` looked like, and `recompute_partition_numbers` rewrites
it.

## 3.14 Re-measured against the final set, and one number got worse

Like for like, same fixture, same method, five paired runs. The prototype's figures were taken
against a subset of the columns and a smaller index set, so they were optimistic.

| | Prototype (subset, drifted fixture) | **Final (23 columns, 26 indexes, diffed fixture)** |
|---|---|---|
| Stamp on `wh_inbox` | 41.5 blocks/row | **36.7 blocks/row** |
| **Stamp on the state table** | **7.5 blocks/row** | **11.3 blocks/row** |
| Reduction on the stamp | 82 percent | **69 percent** |
| Gated pick, before | 582,640 blocks | 562,170 blocks, sequential scan |
| **Gated pick, after** | **1,317 blocks** | **799 blocks, index scan** |
| State table | 249 pages, 101 B/row, 3 indexes | 267 pages, 109 B/row, **6 indexes** |
| `wh_inbox` after | not measured | 5,056 pages, 2,070 B/row, **3 indexes** |
| Index partition | 15 of 24 moving | **23 of 26 move, 3 stay** |
| Lock window, 100k rows | 0.69 s | **0.37 s** |
| Lock window, 500k rows | 2.14 s | **2.51 s** |
| Per row | 4.3 microseconds | **5.0 microseconds** |
| `DROP COLUMN` | 1.7 to 4.3 ms | **1.1 ms for all ten** |

**The stamp got worse and it should be read as worse: 7.5 to 11.3 blocks per row, so the reduction
is 69 percent rather than 82.** The cause is not a surprise in hindsight: the state table now
carries six indexes rather than three, and one of them (`idx_inbox_state_held_lanes`) is a wide
covering index carrying five INCLUDE columns. That is the price of keeping the held-lane re-offer
probe index-only, and it is worth paying, but it is a cost the prototype did not have and did not
predict.

**The gated pick got better**, 1,317 to 799 blocks, and the plan is now an index scan on the driving
side rather than a sequential scan. The lock window is unchanged in substance at roughly 4.7
microseconds per row, and `DROP COLUMN` remains catalog-only at about a millisecond for all ten.

**The case still holds and it is no longer improving with every column added.** The honest summary:
a 69 percent cut on every write to the work state, a roughly 700x cut on the gated pick at this
fixture's shape, `wh_inbox` down from 26 indexes to three, for a one-time stall of about 5.0
microseconds per row.

## 3.15 The C# domain, and the enumeration that was finally provably complete

The function enumeration was derived from `pg_proc`, correctly and completely. **`pg_proc` is not
where all of this system's SQL lives**: the coordinators build statements as C# strings. The
enumeration was complete for functions and complete for nothing else, and **the question to have
asked was "what reads these columns", not "which functions read these columns"**. That is a category
worse than the six pattern-too-narrow instances, because no amount of care with the pattern would
have caught it. A failing test found it, in `CountServiceBacklogAsync`, after the columns were
already dropped.

Eight real code sites, of 32 window hits; the other 24 were comments and interface documentation.
Seven needed redirecting, plus the EF entity model, which mapped nine of the ten moved columns and
is now explicitly `Ignore`-ing them. The `DapperWorkCoordinator` discard sweep is the interesting
one: `message_type` stays on the message and the claim state moved, so the inbox variant could no
longer share `DISCARD_PENDING_WHERE` with the outbox.

**Then it was verified, which is the part that matters.** Full suite, and for every
`undefined_column` failure, whether any `src/` frame appears before the test frame. **Not one does.**
All remaining failures originate in test fixture code. So the 22-function enumeration plus the C#
sweep is complete for product code, by evidence rather than by assertion, after nine instances today
of enumerations being short.

**For the outbox and perspective-events splits: enumerate their C# exposure from the start.** They
have it, and discovering it by a failing test after the columns are gone is the expensive order.

## 3.16 Two things the guards caught, one of them on their author

**The shard guard caught my own three new test classes.** They declared no shard category, so CI
would have selected them in no slice and they would have **silently stopped running** while appearing
to exist. That is exactly the failure the guard exists to prevent, and it caught it on the person who
had been told to read it before changing categories. It is the cleanest example in this whole body of
work of why a structural guard beats a convention: the convention was known, written down, and
pointed at, and it was still missed.

**The enumeration guard caught a defect in the migration itself.** `claim_work` ends with
`$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;`, and the extraction regex's terminator
could not match the `=`, so it ran to the next terminator and silently swallowed **253 lines,
including the whole of `claim_orphaned_perspective_events` and a destructive `drop_all_overloads`
call**. Identical text, so nothing would have failed. The harm is that migration 162 would have
become the authoritative site for a function nobody thinks it owns, and a later edit to 150 would be
shadowed by the stale copy: the two-declaration-sites trap, created by the tooling written to avoid
it.

The guard's first derived output was also a false positive worth keeping: it flagged
`notify_instance_owners`, whose 045 and 130 definitions scan `wh_inbox` and whose current definition
(141) does not reference the table at all. It was flagging history. It now resolves
**latest-definition-wins** by numeric migration order, because a guard that fails on superseded text
is a guard somebody disables.

## 3.17 Recorded, not done: 62 hand-rolled inbox inserts across 40 test files

Every fixture that needs an inbox row builds the `INSERT` by hand. **62 insert statements in 40
files**, reached through 77 distinct helper methods. A shared test builder would have made this
migration a one-line edit instead of sixty-two, and the outbox and perspective splits will pay the
same cost again in exactly the same way.

Not consolidated here deliberately: it would balloon a change that is already large, and it is a
different edit with a different risk profile.

**One observation that makes the sweep smaller and the tests better.** `status` appears in 61 of the
62 and is a **hardcoded literal in every one sampled**, never a parameter and never asserted on:
row-plausibility boilerplate rather than a tested value. So is `partition_number 0`, and
`error, failure_reason` as `NULL, 99`. The columns that are genuinely under test are the parameterized
ones: `attempts`, `instance_id`, `lease_expiry`, `processed_at`. Where a column is incidental the
better edit is to stop setting it rather than to set it somewhere new, which leaves the fixture
saying only what it means.

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

## 6. The read side is under-covered, and this is what blocks the PR

The write-side result is real and measured. The read side is not, and the shape of the gap is now
exact rather than a caveat. Derived from the migration text by machine, not from the failing tests:

**Sixteen indexes on `wh_inbox` name a moved column, so the cutover's `DROP COLUMN` takes every one
of them. The split creates five on `wh_inbox_state`.**

| dropped from `wh_inbox` | replacement on `wh_inbox_state` |
|---|---|
| `idx_inbox_chain_pending` | `idx_inbox_state_chain_pending` -- same keys, same predicate |
| `idx_inbox_held_lanes` | `idx_inbox_state_held_lanes` -- same keys, INCLUDE preserved |
| `idx_inbox_pending_stream_order` | `idx_inbox_state_stream_order` -- same keys, **INCLUDE dropped** |
| `idx_inbox_unowned_bucket_arrival` | `idx_inbox_state_unowned` -- keyed on raw `priority`, **not the bucket CASE expression**, no INCLUDE |
| `idx_inbox_expired_bucket_arrival` | `idx_inbox_state_expired` -- same, plus `lease_expiry` |
| `idx_inbox_pending_interactive` | none |
| `idx_inbox_pending_arrival_standard` | none |
| `idx_inbox_pending_commands` | none |
| `idx_inbox_stuck_sentinel` | none |
| `idx_inbox_stream_blocked` | none |
| `idx_inbox_stream_pending` | none |
| `idx_inbox_instance_id` | none |
| `idx_inbox_lease_expiry` | none |
| `idx_inbox_outstanding_by_instance` | none |
| `idx_inbox_unprocessed_claiming` | none |

Two of sixteen carry over exactly. Three are near-equivalents with a real difference. **Eleven have
no replacement at all.**

**Why "26 indexes to 3" is both the win and the problem.** Dropping the indexes is exactly what buys
the 69 percent off every write, so the number is not a mistake. But a dropped index is only free if
the query it served has another path, and for eleven of these nobody has shown that it does. The
three priority lanes and the bucket-expression keys are the ones to worry about: migration 159
deliberately replaced one disjunctive pick with bucket-ordered partial-index lanes, and the state
table keys on raw `priority` instead, which cannot serve a `CASE`-bucket ordering.

**The failing tests are the symptom, not the problem.** Three separate passes over the fixtures
arrived here independently: `InboxAcquisitionIndexSqlTests` asserts an index-only scan that no state
index can now satisfy, and `PriorityLaneIndexUsabilityTests` asserts three lane indexes that no
longer exist. Both were left red on purpose. Rewriting what they assert would convert a real finding
into a green suite, which is the one outcome to avoid.

**A quieter version of the same hazard, in the measurements themselves.** Several performance guards
still name `wh_inbox` only in their scaffolding -- `DoorbellCostScenarioTests`' `QUEUE_TABLES`,
`NotifyInstanceOwnersScanShapeSqlTests`' `INBOX` and its `TUPLE_CEILING`, `ClaimWorkPlanShapeTests`'
`pg_stat_user_tables` filters and `VACUUM (ANALYZE)` lists. None of them errors, because none names
a moved column. They simply measure a table the hot path barely touches now, so a regression on
`wh_inbox_state` would make them go **quiet rather than breach**. That is the same class as a quality
gate that cannot see a project, and it has to be fixed before any number from those guards means
anything on the split.

**What this leaves.** The column set, the migration, the function rewrites, the C# sweep and the
fixtures are done and the projects compile. What remains is a design decision plus a measurement, in
this order:

1. Decide, per dropped index, whether its query keeps a path: add the replacement on
   `wh_inbox_state` (with the bucket expression and the INCLUDE columns where 159 and 138 needed
   them), or show the query no longer runs.
2. Re-point the measurement scaffolding above at both tables, so the guards can breach.
3. Re-measure the read side, which section 5 has always said was unmeasured.

Until step 1 has an answer this should not be a PR. The write win would ship alongside an unmeasured
read regression, which is precisely the trade this work exists to avoid making by accident.

### 6.1 Migration REPLAY does not survive the cutover

Full suite after the fixture sweep: **5,714 tests, 5,701 passed, 13 failed.** Eleven of the thirteen
are the index and lane assertions above, left red deliberately. The other two are a separate defect
and they are the more serious of the two findings:

```
SchemaInitializationTests.EnsureWhizbangDatabaseInitialized_ReplaysLedgerAgainstSplitStore_WhenTrackingLostAsync
SchemaInitializationTests.EnsureWhizbangDatabaseInitialized_EarlierRedefinerReRun_PullsLastWordViaClosureAsync
  PostgresException 42703: column "processed_at" does not exist
  at ...ExecuteMigrationsAsync
```

Both tests deliberately force migrations to run again -- one by losing the tracking rows, one by
re-running an earlier redefiner -- and both fail inside the migration executor. After the cutover has
dropped the ten columns, **re-executing an earlier migration fails**, because earlier migrations were
written against the schema that had them.

Checked before concluding: no function's last-word definition reads a moved column off `wh_inbox`.
Derived per statement from every migration, the only four hits are false positives (two are
`wh_perspective_events`' own identically-named columns, one is the redirected
`find_stuck_inbox_rows`, one is the cutover's own `DROP COLUMN`). So the steady-state function set is
correct and this is specifically about re-running DDL, not about a missed rewrite.

Why it matters more than a failing test: a database that loses its migration tracking cannot recover
past the cutover, and "lost tracking" is exactly the situation that replay exists to handle. The
options are a design decision rather than an edit:

1. Make the inbox DDL in earlier migrations tolerant of the post-cutover shape. Pre-v1.0 migrations
   are mutable and edited in place, so this is permitted, but it means touching many files.
2. Treat the cutover as a rebase point, so replay starts from it rather than from migration 000.
3. Have the replay path apply only function last words, never DDL.

This is a **third blocker**, independent of the index coverage, and it should be settled before
either of the others: options 2 and 3 change what replay means for every future column move, and the
outbox and perspective splits will hit the identical wall.

## 7. Settled: all three blockers closed, and the number re-measured on the shipped set

Section 6 and 6.1 record three reasons this was not shippable. All three are closed.

**The descriptor no longer undoes the migration.** The nine moved columns carry `BackfillExempt`, so
the ensure stops emitting `ADD COLUMN IF NOT EXISTS` for them, and the descriptor's index list is
down from eight to one, because an index has no equivalent flag and had to be removed outright. The
guard derives the dropped set from the migration text rather than listing it, with an anti-vacuity
test that fails if the parse comes back empty.

**Index coverage is answered.** Five priority-lane indexes are restored on the state table, where
they belong; the remaining six of the eleven that had no replacement were redundant with the
covering index or with each other. The `idx_inbox_state_stream_order` index gained four `INCLUDE`
columns so the lane probes stay index-only.

**Replay survives the cutover.** Seventeen of the eighteen `CREATE INDEX` statements on the message
table are gated on the column they name still existing, along with twelve `COMMENT` statements; the
eighteenth names two columns the cutover does not move and correctly needs no guard.
`find_stuck_inbox_rows` became `plpgsql` rather than `SQL`, because PostgreSQL resolves a
`LANGUAGE SQL` body's columns at create time and a replay reaching it against the post-cutover shape
failed there before the later definition could replace it. And migration 158's
`ADD COLUMN IF NOT EXISTS chain_emitted_at` is gated too: it was resurrecting the very column that
fourteen of those guards were testing, so the guard passed and the index failed on its own predicate.

### The number, on the index set that actually ships

Section 3.14 reported 69 percent against a state table with six indexes. Five lanes have been
restored since, taking it to eleven, and the number moved with them. Measured by
`InboxStateWriteCostScenarioTests`, which reconstructs the pre-cutover row rather than comparing two
cheap numbers, and which asserts the wide side cost more than one block per row so it cannot pass on
a fixture that failed to build the shape:

| | Wide row, as it was | **State row, as it ships** |
|---|---|---|
| Indexes | 17 | **11** |
| Blocks written per row stamped | 37.2 | **18.3** |
| Reduction | - | **50.9 percent**, a 2.03x cut |

**51 percent, not 69, and the plan said 69.** Restoring the lanes cost about eighteen points of the
reduction. The case still holds comfortably -- a claim write costs half what it did -- but the
headline figure in section 3.14 is now history rather than the current number.

The gate in `baseline.tsv` is the state table's **index count**, ceiling twelve, not the block
figure. The index count is the thing a later change trades away without noticing: six indexes bought
69 percent, eleven buy 51, and a drift back toward seventeen gives up the entire reason the split
exists. Twelve leaves room for one more lane. The reduction percent is recorded and not gated,
because a ceiling is an upper bound and that number wants a floor; the ratio is asserted in the test.

### A second justification the block figure does not capture

PostgreSQL gives a backend sixteen fast-path slots for relation locks, and a statement needing more
takes its locks through the shared lock manager's partitioned hash instead, where the contention
reads as `LWLock:LockManager` and says nothing about the query. A statement against the pre-split
table locked the table plus every index on it, which exceeded the limit on its own. After the cutover
the message table carries three relations and the state table eleven, so a statement against either
is inside the fast path, and one joining both still is. That is a different kind of win from write
cost and it is invisible to a blocks-per-row measurement.

## 8. Deadlocks found in the same load run, and the order that fixes them

Deadlocks on message-table row locks appeared under load with the server's detail redacted, so the
log named neither the pair of tables nor the pair of functions. Deriving each function's reachable
lock order from the migration text found two inversions.

`recompute_partition_numbers` locked the state table before the outbox, while `renew_leases`,
`deregister_instance` and `cleanup_stale_instances` all lock the outbox first. Their row sets overlap
at exactly the wrong moment, because a partition recompute is triggered by the same scale event that
runs a deregistration and a stale-instance sweep. The order was pre-existing: migration 041 already
had it, and the cutover only retargeted the first statement, so this is not a regression the split
introduced.

`perform_maintenance` inverted against itself, deleting from the perspective-event table, then the
active-stream table, then the perspective-event table again in one transaction, with message-table
deletes on both sides of the first. Every instance runs it on the same tick, so two copies deadlock
each other with no scale event involved at all.

With both fixed, all twelve functions that lock more than one work table agree on a single total
order rather than merely avoiding pairwise disagreement: outbox, message table, state table,
perspective events, active streams. That is the order work moves through them, so the rule describes
the design instead of constraining it. A pairwise check would have been satisfied by a cycle across
three tables, which deadlocks just as readily.

### The derivation was wrong three times, each in a different direction

Worth recording, because the analysis is what found the defects and a narrower one gets the answer
wrong rather than merely incomplete.

`UPDATE` and `DELETE` are not the only row locks. `INSERT ... ON CONFLICT` locks the row it conflicts
with, which is the only way `store_inbox_messages` reaches the active-stream table, and
`SELECT ... FOR UPDATE` locks unless it `SKIP LOCKED`s or `NOWAIT`s -- neither of which ever waits,
so neither can be half of a deadlock. Missing these understates the graph.

plpgsql branches are mutually exclusive. `renew_leases` is a `CASE` over the work category, and
flattening its three arms invents the order outbox then state table, which no single call takes, and
reports two inversions that do not exist. This one overstates.

A `CASE` expression is not a branch. Every task in `perform_maintenance` reports through
`RETURN QUERY SELECT ..., CASE WHEN ... ELSE 'ok' END::TEXT`, at parenthesis depth zero, so depth
cannot tell them apart from a `CASE` statement; a statement closes with `END CASE` and an expression
with a bare `END` that pops nothing, so each one leaked a construct onto the branch stack.
Separately, `EXIT WHEN` and `CONTINUE WHEN` are loop modifiers rather than arms, and there are
sixteen of them here: reading one as an arm splits a single arm in two, so statements either side
look mutually unreachable and their pair is never derived -- the direction that HIDES an inversion.

Each correction was checked by re-deriving and diffing rather than by the output looking wrong. The
answer held every time, which is the only reason the two fixes can be trusted.

### Still open

The server supplies a stack of the active routines on any canceled operation, most recent first, and
supplies it whether or not the connection includes detailed error text -- so nothing about a
deployment has to change to get it. The platform reads the error code and discards the rest, which is
why this deadlock had to be derived from structure instead of read from a log. Tracked separately;
the fix needs a design decision rather than a patch, because the code that classifies these failures
deliberately reads only what any provider exposes while the context is specific to one of them.
