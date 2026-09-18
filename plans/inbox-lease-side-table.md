# Moving the inbox lease into a table of its own

Status: designed and prototyped, not built. Every number here is from a controlled fixture at the
shape stated beside it. Nothing in this document rests on statistics from a running environment.

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

**The split introduces a different risk and it is the one to test for: a missing lease row.** An
inbox row with no lease row is a message nothing can ever claim, and it fails silently, which is the
worst shape a defect can have. Two things prevent it:

- A foreign key from the lease table to `wh_inbox` on `message_id`, `ON DELETE CASCADE`, so the pair
  cannot be half-deleted.
- Both rows inserted by the same statement in `store_inbox_messages`, so the pair cannot be
  half-created.

And one thing detects it: an invariant test asserting that every unprocessed `wh_inbox` row has a
lease row, run after the fixtures that insert, claim, fail, expire and dead-letter messages.

### 3.5 The migration, and what happens to rows mid-flight

**A single-transaction cutover is not safe and should not be attempted.** The backfill reads a
consistent snapshot. A claim committing after that snapshot but before the cutover commits writes the
old columns, which the new functions no longer read, so its lease would be lost and the row would
look free while an instance is still working it. That is a double dispatch, which this system must
never do. The window is small and the consequence is not, so the design does not rely on it being
small.

**Expand, migrate, contract, across a release boundary:**

1. **Expand.** Create the lease table and backfill it from `wh_inbox`. Add a trigger on `wh_inbox`
   that mirrors every write of the claim-state columns into the lease table. Leave all sixteen
   functions alone. Both representations are now authoritative and the trigger keeps them in step, so
   a row mid-flight during this step is simply written twice. Old and new instances can both run.
2. **Migrate.** In a later migration, switch the sixteen functions to read and write the lease table,
   and drop the trigger. The lease table is already correct for every row, including rows claimed
   during step 1, so there is no snapshot window to lose a write in.
3. **Contract.** Drop the claim-state columns and their fifteen indexes from `wh_inbox`. This is the
   step that collects the win, and it is deliberately last and separate, because it is the only
   irreversible one.

Steps 2 and 3 must not land in the same release as step 1: an instance running old code against a
schema that has had step 2 applied would write columns nothing reads.

The pre-v1 rule that migrations are editable in place does not apply to this one in the usual way,
because it moves data. Each step is its own migration file and none of the three is edited after it
has run anywhere.

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
