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
CREATE TABLE wh_inbox_lease (...);
INSERT INTO wh_inbox_lease SELECT ... FROM wh_inbox;
CREATE INDEX ... ON wh_inbox_lease ...;          -- three of them
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
