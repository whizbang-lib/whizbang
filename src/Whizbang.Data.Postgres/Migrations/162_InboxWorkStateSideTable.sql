-- Migration: 162_InboxWorkStateSideTable.sql
-- Date: 2026-09-17
-- Description: An inbox message's mutable work state moves into a narrow table of its own, so a
--              claim stops paying for every index on a wide message row.
--
--              NAMED wh_inbox_state RATHER THAN wh_inbox_lease because the lease turned out to be
--              one of NINE mutable columns it holds, and a name describing one of nine misleads.
--              The pair is now: wh_inbox is the immutable message, wh_inbox_state is its mutable
--              work state.
--
--              wh_inbox holds a row that is wide, heavily indexed and written once, beside a lease
--              that is narrow and rewritten constantly. Keeping them together costs more than it
--              looks:
--
--                instance_id   appears in 12 of the table's 26 indexes
--                lease_expiry  appears in 12
--                attempts      appears in 2
--
--              The claim writes all three, so a heap-only (HOT) update is impossible BY
--              CONSTRUCTION: HOT requires that no indexed column changes. No fillfactor setting can
--              help, because free space on the page is not what is missing, and a wide row closes
--              the other door by fitting few tuples to a page. Measured across three databases in a
--              deployed fleet, over roughly six million updates: 4,006 HOT of 2,969,674; 5,434 of
--              3,084,970; 0 of 24,508. Under two tenths of one percent. A lab fixture independently
--              measured 0 of 660. So every write to this table maintains every index on it.
--
--              TWENTY-ONE of the twenty-four indexes in the fixture name one of the NINE mutable
--              columns. They belong to the work state and they leave with it, which is the real
--              argument for this change: wh_inbox drops from twenty-four indexes to THREE, so EVERY
--              write to it gets cheaper, not only the claim.
--
--              Measured on a churned fixture, same ids picked by primary key, five paired runs:
--
--                                                     before        after
--                lease stamp                     41.5 blocks/row   7.5 blocks/row
--                any other write to wh_inbox     41.0 blocks/row  12.5 blocks/row
--                gated pick, batch of 10          582,640 blocks   1,317 blocks
--                heap                    14,968 pages @ 6,130 B    249 pages @ 101 B
--
--              On the pick, the durable part is the plan shape rather than the ratio: today the
--              planner sequential-scans 19,460 rows and runs the per-stream ordering probe on every
--              one before sorting and taking ten; on the narrow table it walks the priority-ordered
--              partial index and stops after 206. The bound is reached before the per-row work.
--
-- WHY THIS IS ONE TRANSACTION, AND WHY THE COLUMNS ARE DROPPED RATHER THAN LEFT
--
--              Consumers take the latest package, so there is no sequence of releases to stage this
--              through. It happens in one update or not at all.
--
--              That is what makes it safe, not what makes it risky. The hazard in a staged cutover
--              is leaving the old columns PRESENT BUT IGNORED: a claim that writes a column nothing
--              reads succeeds, believes it holds a lease, and dispatches work a second claimer will
--              also take. A claim that FAILS is safe, because no lease is taken, the row stays
--              claimable, and the worker retries. Failing loudly is the property that makes a single
--              cutover correct, and it is only available if the columns are gone.
--
--              An instance still running older code during a rolling deploy therefore raises
--              SQLSTATE 42703 (undefined_column) on every claim. That is not a transient failure, so
--              WorkerLoopRecovery reports it as a defect -- and its loop CONTINUES, backing off from
--              250 ms to a 30 second ceiling until the pod is replaced. Nothing dead-letters the
--              work: the attempt budget only advances on a claim that succeeds, so a claim that
--              throws ages nothing out. Old instances take no work and lose none.
--
-- THE LOCK ON THE LINE BELOW IS NOT REDUNDANT. DO NOT REMOVE IT.
--
--              It looks redundant, because the ALTER TABLE further down takes ACCESS EXCLUSIVE on
--              the same table anyway. Taking it there instead is a data-loss bug, and here is the
--              sequence that produces it:
--
--                1. The backfill below is an INSERT ... SELECT. It takes ACCESS SHARE on wh_inbox,
--                   which DOES NOT BLOCK WRITERS, and under READ COMMITTED it reads its own fresh
--                   snapshot.
--                2. A concurrent claim commits after that snapshot is taken. Its lease is written to
--                   wh_inbox's columns and is NOT in the copy.
--                3. The ALTER TABLE then drops those columns, taking the lease with them.
--                4. The row is now unleased in the only table that has leases, so the next poll
--                   claims it while the first worker is still processing it. Two dispatches of one
--                   message, which is the exact failure this whole design exists to prevent.
--
--              Holding ACCESS EXCLUSIVE from before the backfill closes the window completely. Every
--              concurrent claim either committed before the lock was granted, in which case the
--              backfill sees it, or waits on the lock and then fails on a column that is gone -- and
--              a claim that fails is safe.
--
--              The cost of that correctness is that the lock is held for the length of the backfill
--              rather than the length of the drop. Measured on a faithful copy of this table at
--              1.4 KB a row: 0.69 s at 100,000 rows and 2.14 s at 500,000, about 4.3 microseconds
--              per row, on local storage with a warm cache, so treat it as the optimistic end. The
--              DROP COLUMN itself is catalog-only and costs single-digit milliseconds at both
--              scales; the stall is the backfill and the three index builds.
--
-- THE COLUMN DROPS ARE AT THE END OF THIS FILE, AFTER EVERY FUNCTION THAT READ THEM
--
--              The order is load-bearing and it is the reason this is one file: the columns cannot
--              be dropped until every function has been redirected, and the functions cannot be
--              redirected in a separately committed step without leaving a window where a claim
--              writes a column nobody reads. One transaction, in this order, is the only safe
--              arrangement.
--
-- THIS FILE MUST NEVER CARRY A COMMIT BOUNDARY MARKER
--
--              SchemaCommandBoundary applies a marker-free script as ONE command on one connection,
--              which PostgreSQL runs as a single implicit transaction. That is what makes everything
--              above hold. A marker anywhere in this file splits it into separately committed
--              pieces and reintroduces every hazard described here, including a window where the
--              columns are dropped but the functions still reference them. There is a test that
--              fails if a marker ever appears in this file; it is not decoration.
--
-- Dependencies: 149 (store_inbox_messages, _emit_event_store_chain_for_inbox), 150 (claim_orphaned_inbox, claim_work), 158, 159
-- Objects: wh_inbox_state, wh_inbox (drops instance_id, lease_expiry, attempts, processed_at, scheduled_for, failure_reason, error, chain_emitted_at, status)

-- See "THE LOCK ON THE LINE BELOW IS NOT REDUNDANT" above before touching this statement.
LOCK TABLE __SCHEMA__.wh_inbox IN ACCESS EXCLUSIVE MODE;

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_inbox_state (
  -- The join key, and the only column shared with wh_inbox.
  message_id       UUID PRIMARY KEY,
  -- Everything below is a column claim_orphaned_inbox reads or writes. The set is DERIVED from the
  -- function rather than chosen: a column the claim path needs that stays behind turns the
  -- per-stream ordering gate into a cross-table join, which is the one thing that would make this
  -- slower rather than faster.
  -- WRITE-ONCE COPIES. These stay on wh_inbox as well, and that is safe precisely because nothing
  -- rewrites them after the row is inserted: two copies of a value that never changes cannot
  -- diverge. They are copied here so the per-stream ordering gate and the lane picks read one narrow
  -- table instead of joining back to a wide one, which is the whole performance case. The exhaustive
  -- pass over all 25 columns settled this set at exactly five: the other ten write-once columns are
  -- payload, appear in no index, and are read only as projections by paths that join anyway.
  stream_id        UUID,
  received_at      TIMESTAMPTZ NOT NULL,
  -- DEFAULT 150, which is what migration 149 gave this column on all three work tables. This read
  -- 100 until the contract guard compared it against the outbox. 100 and 150 land in the same
  -- priority band, so nothing about lane selection changes, but the lanes ORDER BY priority: a row
  -- that took the old default would have sorted ahead of every row that named 150 explicitly.
  priority         INTEGER     NOT NULL DEFAULT 150,
  is_event         BOOLEAN     NOT NULL DEFAULT FALSE,
  -- partition_number is the TENTH mutable column and was found only by the exhaustive pass.
  -- recompute_partition_numbers rewrites it, so question one forbids it being copied: it moves.
  -- It looks like static routing data, which is exactly what scheduled_for looked like.
  partition_number INTEGER,
  -- THE NINE MUTABLE COLUMNS. These MOVE: they are dropped from wh_inbox, so each lives in exactly
  -- one place. The distinction that matters is mutability, not whether claiming reads them. An
  -- immutable column can safely be copied to both tables because the copies can never disagree; a
  -- MUTABLE column copied to both tables is a split brain waiting to happen, and the first draft of
  -- this design had exactly that bug with scheduled_for.
  --
  -- processed_at is here for a correctness reason rather than a performance one: the claim predicate
  -- filters on it, and if it stayed on wh_inbox while the row lock moved here, a completion landing
  -- between the read and the lock would no longer be excluded. Excluding exactly that is what the
  -- old FOR UPDATE on the inbox row did.
  processed_at     TIMESTAMPTZ,
  instance_id      UUID,
  lease_expiry     TIMESTAMPTZ,
  attempts         INTEGER     NOT NULL DEFAULT 0,
  -- scheduled_for is REWRITTEN on every failure, by process_inbox_failures computing the retry
  -- backoff. It reads like static routing data and is not.
  scheduled_for    TIMESTAMPTZ,
  -- Written when a failure is reported, and when the claim finds a lease that expired with nobody
  -- having said why. error is NULL for every row that has not failed, which costs a bit in the null
  -- bitmap and nothing else.
  -- NOT NULL DEFAULT 99, carried over verbatim from wh_inbox rather than simplified. Dropping it
  -- was silent and wrong: store_inbox_messages and both recover_dead_letter paths insert without
  -- naming this column, so every newly stored message got NULL where it used to get 99, while the
  -- backfill copied real values for the rows that already existed. A NULL here is not a quieter 99
  -- -- `failure_reason = 99` and `failure_reason <> 99` both exclude it -- so the rows would have
  -- gone missing from anything that counts or groups by reason, and only for rows stored after the
  -- cutover. Caught by FailureReasonSchemaTests asserting the column keeps its default.
  failure_reason   INTEGER NOT NULL DEFAULT 99,
  error            TEXT,
  -- status is rewritten by the completion and failure paths, and it is single-homed today, so the
  -- first boundary question allows it to stay. The second one does not. claim_work's held-lane
  -- re-offer PROJECTS status, and idx_inbox_held_lanes INCLUDEs it specifically so that probe is
  -- index-only: one probe per stream, which is the bounded-cost property migration 150 exists to
  -- establish. That index keys on instance_id and predicates on processed_at, both of which move, so
  -- leaving status behind buys a heap fetch on the wide row per candidate stream, which is the exact
  -- cost this migration removes. A split that de-indexes the path it was meant to speed up is not a
  -- smaller change, it is a wrong one.
  -- DEFAULT 1, which is what wh_inbox declared. This read DEFAULT 0 until a column-by-column diff
  -- against the table it came from caught it. No insert in this migration omits status, so nothing
  -- observes the difference today -- which is exactly why it would have sat here until something
  -- did, and status is a bit field, so 0 and 1 are not near-misses of each other.
  status           INTEGER     NOT NULL DEFAULT 1,
  -- chain_emitted_at is here for the SAME reason processed_at is, and it was nearly missed. It is
  -- single-homed on wh_inbox today, so the boundary rule (a rewritten column lives in exactly one
  -- table) does not by itself force it to move. What forces it is the predicate that reads it:
  --
  --   WHERE instance_id = ... AND lease_expiry > ... AND processed_at IS NULL
  --     AND is_event AND stream_id IS NOT NULL AND chain_emitted_at IS NULL
  --
  -- That is the emit chain's driving read, it runs on every claim poll, and idx_inbox_chain_pending
  -- serves it by keying on instance_id and predicating on processed_at. BOTH of those move, so the
  -- index cannot survive on wh_inbox, and leaving chain_emitted_at behind would split the predicate
  -- across two tables with no index able to cover it. That is the one change that would make this
  -- migration slower rather than faster in its hottest path. With the column here, every term is on
  -- this table and one partial index covers the whole predicate again.
  chain_emitted_at TIMESTAMPTZ,
  -- A message with no lease row can never be claimed, and it would fail silently, which is the worst
  -- shape a defect can have. The key and the cascade make the pair impossible to half-create or
  -- half-delete; an invariant test covers the rest.
  CONSTRAINT fk_inbox_state_message FOREIGN KEY (message_id)
    REFERENCES __SCHEMA__.wh_inbox (message_id) ON DELETE CASCADE
);

COMMENT ON TABLE __SCHEMA__.wh_inbox_state IS
  'Claim state for wh_inbox rows (162), split out so a claim does not rewrite a wide, heavily '
  'indexed message row. instance_id and lease_expiry each appear in twelve of wh_inbox''s indexes, '
  'so a claim can never be a HOT update and pays full index maintenance on every write; measured '
  'HOT share across six million updates in a deployed fleet was under two tenths of one percent. '
  'One row per wh_inbox row, created and deleted with it.';

-- The backfill reads the ten columns off wh_inbox, which is right the first time this migration
-- applies and impossible the second. A replayed ledger reaches here after the DROP below has
-- already run, and the unqualified column list then fails with 42703 -- which wedges the whole init
-- behind the schema-ready gate, on a database whose state table is already correctly populated.
--
-- Guarded on the source column rather than on the destination: wh_inbox_state existing proves
-- nothing (CREATE TABLE IF NOT EXISTS above just made it), whereas wh_inbox still carrying
-- processed_at is exactly the condition under which there is anything to copy.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'processed_at' AND NOT attisdropped) THEN
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      processed_at, instance_id, lease_expiry, attempts, scheduled_for, failure_reason, error,
      chain_emitted_at, status)
    SELECT
      message_id, stream_id, received_at, partition_number, priority, is_event,
      processed_at, instance_id, lease_expiry, attempts, scheduled_for, failure_reason, error,
      chain_emitted_at, status
    FROM __SCHEMA__.wh_inbox
    ON CONFLICT (message_id) DO NOTHING;
  END IF;
END $$;

-- The lanes the claim picks from. Partial on processed_at IS NULL so they track pending work rather
-- than settled history, and split by ownership so neither lane reads the other's rows.
CREATE INDEX IF NOT EXISTS idx_inbox_state_unowned
  ON __SCHEMA__.wh_inbox_state (priority, received_at, message_id)
  WHERE processed_at IS NULL AND instance_id IS NULL;
CREATE INDEX IF NOT EXISTS idx_inbox_state_expired
  ON __SCHEMA__.wh_inbox_state (priority, lease_expiry, received_at)
  WHERE processed_at IS NULL AND instance_id IS NOT NULL;
-- The per-stream ordering gate walks a stream's pending rows in arrival order. Every column it reads
-- is on this table, so the gate stays a single-table query rather than becoming a join.
-- The emit chain's driving read, now entirely local to this table. Mirrors
-- idx_inbox_chain_pending, which cannot survive on wh_inbox because it keys on instance_id and
-- predicates on processed_at, both of which move.
-- INCLUDE carries the three columns the emit-chain lock pass reads for every candidate row, so the
-- pass is index-only rather than one heap fetch per candidate. message_id is the one that matters
-- most: the pass anti-joins it against the event store, and v0.685's lock-in (migration 057, and
-- EmitChainInboxIndexTests) required it to be reachable from the index precisely so the planner can
-- take a merge or hash anti-join instead of a nested loop over heap tuples. It was the primary key
-- of the wide row and is the primary key here, which does NOT put it in a secondary index, so it
-- has to be named. lease_expiry and stream_id are the pass's other two reads.
CREATE INDEX IF NOT EXISTS idx_inbox_state_chain_pending
  ON __SCHEMA__.wh_inbox_state (instance_id)
  INCLUDE (message_id, stream_id, lease_expiry)
  WHERE processed_at IS NULL AND is_event = TRUE AND stream_id IS NOT NULL
    AND chain_emitted_at IS NULL;
-- claim_work's held-lane re-offer walk, rebuilt here. INCLUDE carries every column the walk
-- projects so the probe stays index-only, which is the whole reason status had to move: one probe
-- per stream rather than a heap fetch on the wide row per candidate.
CREATE INDEX IF NOT EXISTS idx_inbox_state_held_lanes
  ON __SCHEMA__.wh_inbox_state (
    instance_id,
    (CASE WHEN priority <= 99 THEN 0 WHEN priority <= 199 THEN 1 ELSE 2 END),
    is_event,
    ((attempts = 0)),
    stream_id,
    received_at,
    message_id)
  INCLUDE (priority, attempts, partition_number, status, lease_expiry)
  WHERE processed_at IS NULL;
-- INCLUDE restores what idx_inbox_pending_stream_order (138) carried. Without it the gated pick
-- stops being index-only and takes a heap fetch per candidate. The fetch is far cheaper here than
-- it was on the 2,070-byte inbox row, but free is cheaper still, and 138 added these columns for a
-- measured reason.
CREATE INDEX IF NOT EXISTS idx_inbox_state_stream_order
  ON __SCHEMA__.wh_inbox_state (stream_id, received_at, message_id)
  INCLUDE (instance_id, lease_expiry, scheduled_for, partition_number)
  WHERE processed_at IS NULL;

-- THE PRIORITY LANES. Migration 150 split the arrival pick into one partial index per priority
-- band, and 145 gave commands their own, because a band reading another band's rows is the defect
-- those indexes exist to prevent. Every column they key, include or predicate on now lives on this
-- table, so they move across unchanged apart from the table name.
--
-- These are re-created and the remaining orphans are NOT, and the distinction is deliberate rather
-- than arbitrary: each of these five has a test that asserts a PLAN uses it, which is a demonstrated
-- query. The others were dropped with their columns and nothing has yet shown what still reads them.
-- Adding all of them back would take this table from six indexes to seventeen, past the sixteen-slot
-- fast-path limit, and the point of the split is to get a hot statement back INSIDE that limit --
-- measured on a deployed fleet as 39 backends over the limit and 3,296 locks going through the
-- shared lock manager. Restoring everything unexamined would trade one bottleneck for the same one.
CREATE INDEX IF NOT EXISTS idx_inbox_state_pending_interactive
  ON __SCHEMA__.wh_inbox_state (stream_id, received_at, message_id)
  INCLUDE (instance_id, lease_expiry, scheduled_for, partition_number, is_event)
  WHERE processed_at IS NULL AND priority <= 99;
CREATE INDEX IF NOT EXISTS idx_inbox_state_pending_arrival_standard
  ON __SCHEMA__.wh_inbox_state (received_at, message_id)
  INCLUDE (stream_id, instance_id, lease_expiry, scheduled_for, partition_number)
  WHERE processed_at IS NULL AND is_event = TRUE AND priority >= 100 AND priority <= 199;
CREATE INDEX IF NOT EXISTS idx_inbox_state_pending_arrival_background
  ON __SCHEMA__.wh_inbox_state (received_at, message_id)
  INCLUDE (stream_id, instance_id, lease_expiry, scheduled_for, partition_number)
  WHERE processed_at IS NULL AND is_event = TRUE AND priority > 199;
CREATE INDEX IF NOT EXISTS idx_inbox_state_pending_commands
  ON __SCHEMA__.wh_inbox_state (stream_id, received_at, message_id)
  INCLUDE (instance_id, lease_expiry, scheduled_for, partition_number)
  WHERE processed_at IS NULL AND is_event = FALSE;

-- find_stuck_inbox_rows' driving read. Tiny and highly selective: attempts > 5 on unprocessed rows
-- is a handful of rows in a healthy system, which is exactly what makes the sentinel cheap to ask.
CREATE INDEX IF NOT EXISTS idx_inbox_state_stuck_sentinel
  ON __SCHEMA__.wh_inbox_state (attempts)
  WHERE processed_at IS NULL AND attempts > 5;


-- perform_maintenance was in the enumeration of twenty-two and was missed in the batch that should
-- have carried it. The enumeration was right; the checklist against it was not. Found by the full
-- suite failing, not by review.
--
-- <docs>operations/infrastructure/maintenance</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PurgesStuckInboxMessages_OlderThanRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesRecentStuckInboxMessages_WithinRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesLeasedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesClaimedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.cs:NoFunctionLocksTheWorkTablesOutOfCanonicalOrderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.perform_maintenance()
RETURNS TABLE(
  task_name TEXT,
  rows_affected BIGINT,
  duration_ms DOUBLE PRECISION,
  status TEXT
) AS $$
DECLARE
  v_start TIMESTAMPTZ;
  v_rows BIGINT;
  v_dedup_retention_days INTEGER;
  v_stuck_inbox_retention_days INTEGER;
  v_debug_mode BOOLEAN;
  v_abandoned_stream_hours INTEGER;
  v_ephemeral_grace_seconds INTEGER;
  v_per_table TEXT;
  v_per_deleted BIGINT;
  v_instance_eviction_retention_hours INTEGER;
  v_dead_letter_retention_days INTEGER;
  v_orphan_grace_hours INTEGER;
BEGIN
  -- Read debug_mode flag once for the cycle. When true, the complete_* functions
  -- retain rows for forensics with processed_at stamped, this maintenance pass
  -- MUST skip purging those rows or the debug-mode design breaks.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug_mode;

  -- Grace period before an owner-less active-stream row is purged (Task 6). Configurable via
  -- wh_settings; default 1 hour preserves the transient-NULL race window between
  -- cleanup_stale_instances nulling the owner and the next claim cycle re-assigning it.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'abandoned_stream_hours'),
    1
  ) INTO v_abandoned_stream_hours;

  -- Rewind grace window (seconds): an ephemeral body is retained this long AFTER consumption so an
  -- out-of-order straggler can still rewind through it (events arrive out of order in a short window).
  -- Configurable via wh_settings; default 300s. A per-type [Ephemeral(RewindGrace)] override lands later.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_rewind_grace_seconds'),
    300
  ) INTO v_ephemeral_grace_seconds;

  -- Retention for wh_instance_evictions tombstones (Task 10, migration 106/107). The tombstone only
  -- needs to outlive a paused instance's resumption window, not the fleet's lifetime. Default 24
  -- hours is generous against any realistic pause while still bounding the table.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'instance_eviction_retention_hours'),
    24
  ) INTO v_instance_eviction_retention_hours;

  -- ========================================
  -- Task 1: Purge completed outbox messages
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox WHERE processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_outbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 2: Purge completed inbox messages
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    -- 162: processed_at is work state. The DELETE stays on wh_inbox because that is the row being
    -- removed, and the state row goes with it through ON DELETE CASCADE; the predicate reads the
    -- state table. USING keeps this one statement so the GET DIAGNOSTICS below still measures it.
    DELETE FROM __SCHEMA__.wh_inbox i
    USING __SCHEMA__.wh_inbox_state ist
    WHERE ist.message_id = i.message_id AND ist.processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 5: Purge ancient stuck inbox messages
  -- ========================================
  -- POSITION: this sweep sits beside Task 2 because both DELETE from wh_inbox, and a transaction
  -- that locks a table, moves on, and comes back to it can deadlock against a sibling that took
  -- the two tables the other way round. Every pod runs this function on the same tick, so the
  -- sibling here is another copy of this very function: one holds a wh_perspective_events row and
  -- wants wh_active_streams, the other holds wh_active_streams and wants wh_perspective_events.
  -- Each table is therefore visited exactly once, in the canonical order wh_outbox, wh_inbox,
  -- wh_inbox_state, wh_perspective_events, wh_active_streams. The report is looked up by task
  -- name everywhere it is read, never by position, so moving a block is free.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stuck_inbox_retention_days'),
    7
  ) INTO v_stuck_inbox_retention_days;

  v_start := clock_timestamp();
  -- 162: same shape as the purge above. Every term of this predicate is on the state table,
  -- received_at as a write-once copy, so the sweep never reads the wide message row to decide.
  DELETE FROM __SCHEMA__.wh_inbox i
  USING __SCHEMA__.wh_inbox_state ist
  WHERE ist.message_id = i.message_id
    AND ist.processed_at IS NULL
    AND ist.lease_expiry IS NULL
    AND ist.instance_id IS NULL
    AND ist.received_at < NOW() - (v_stuck_inbox_retention_days || ' days')::INTERVAL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_stuck_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 3: Purge completed perspective events
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_perspective_events'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 12: Reap orphaned perspective-event rows (issue #687)
  -- ========================================
  -- POSITION: beside Task 3 for the same reason Task 5 sits beside Task 2 -- both DELETE from
  -- wh_perspective_events, and this one used to run after the wh_active_streams purge, which put
  -- the two tables in both orders inside one transaction. Nothing here depends on the blocks it
  -- moved past: the grace hours are read from wh_settings by this block itself, the predicate
  -- reads wh_event_store which this function never writes, and Task 13 still runs after it and
  -- still sees v_orphan_grace_hours set.
  -- A wh_perspective_events row whose source event no longer exists in wh_event_store is
  -- UNPROJECTABLE forever: the drainer's inner join (get_stream_events) returns nothing, so the
  -- row is re-claimed every cycle with attempts climbing and no error, livelocking the pipeline
  -- (root cause of #679). These arise when an event is reaped/purged after its perspective work
  -- was created. Deleting is correct: the event is gone, so there is nothing to project and the
  -- projection cursor never advanced past the row. Age-bounded on created_at so a row whose event
  -- write has simply not committed yet (a legitimate in-flight window) is never reaped out from
  -- under itself. Not gated on debug_mode: this is unprojectable garbage, not forensic evidence,
  -- and leaving it keeps the pipeline wedged.
  v_start := clock_timestamp();
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'orphan_perspective_grace_hours'),
    1
  ) INTO v_orphan_grace_hours;
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  WHERE pe.processed_at IS NULL
    AND pe.created_at < NOW() - (v_orphan_grace_hours * INTERVAL '1 hour')
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = pe.event_id
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'reap_orphaned_perspective_events'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 4: Purge old deduplication entries
  -- ========================================
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'),
    30
  ) INTO v_dedup_retention_days;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dead_letter_retention_days'),
    7
  ) INTO v_dead_letter_retention_days;

  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_message_deduplication
  WHERE first_seen_at < NOW() - (v_dedup_retention_days || ' days')::INTERVAL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_old_deduplication'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 6: Purge abandoned active-stream rows
  -- ========================================
  -- Two branches, both safe because UUIDv7 IDs never repeat (a missing
  -- wh_service_instances row means the instance is fully gone):
  --
  --   (a) Rows whose assigned_instance_id is non-NULL but points at a
  --       wh_service_instances row that no longer exists. After the
  --       heartbeat-recency liveness check in claim_orphaned_inbox /
  --       claim_orphaned_outbox (migrations 024/025) these are already
  --       non-blocking; the cleanup just bounds accumulation. No age guard.
  --
  --   (b) Rows whose assigned_instance_id IS NULL AND whose last_activity_at
  --       is older than the grace period. cleanup_stale_instances nulls the
  --       assigned_instance_id in the same tick where it deletes the dead
  --       wh_service_instances row, so without this branch every dead
  --       instance leaves its streams in the table forever (production forensic:
  --       tens of thousands of rows accumulated, 99% with NULL owner). The age
  --       guard preserves the legitimate transient-NULL race window between
  --       cleanup_stale_instances nulling the field and the next
  --       claim_orphaned_* cycle re-assigning via INSERT ON CONFLICT.
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_active_streams
  WHERE (
      assigned_instance_id IS NOT NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = __SCHEMA__.wh_active_streams.assigned_instance_id
      )
    )
    OR (
      assigned_instance_id IS NULL
      AND last_activity_at < NOW() - (v_abandoned_stream_hours * INTERVAL '1 hour')
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_abandoned_active_streams'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 7: Refresh wh_dead_letter_summary
  -- ========================================
  -- Slice 6 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis).
  -- Two-step pipeline inside aggregate_dead_letters:
  --   (1) Version-aware backfill, re-hashes raw wh_dead_letters rows with
  --       stale error_fingerprint_version; current-version rows are skipped.
  --   (2) GROUP BY upsert into wh_dead_letter_summary.
  -- The summary table is the operator/AI-facing rollup view: ~dozens of
  -- distinct fingerprint clusters instead of tens of thousands of raw rows.
  -- Cluster-count metric is the rows_affected for this task (post-aggregation).
  v_start := clock_timestamp();
  PERFORM __SCHEMA__.aggregate_dead_letters();
  SELECT COUNT(*) FROM __SCHEMA__.wh_dead_letter_summary INTO v_rows;
  RETURN QUERY SELECT
    'aggregate_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 8: Reap consumed ephemeral event bodies (E1 #13b2, + E2-4c TTL floor)
  -- ========================================
  -- wh_event_body holds ONLY ephemeral bodies (offloaded by the emit chain, migration 072). A body is
  -- reapable once every perspective that consumes its event has processed it, i.e. no unprocessed
  -- wh_perspective_events work item still references the event_id. The emit chain writes the body and
  -- its perspective work items in one transaction, so a body with consumers always has a matching
  -- gating work item (no premature-reap window); an ephemeral event with no consuming perspective has
  -- no work item and is reapable at once. The wh_event_store pointer is left in place, a
  -- pointer-present / body-NULL row is the deterministic rebuild-guard signal (#13d), not a lost event.
  -- Skipped under debug_mode so retained forensic bodies survive with the retained work items.
  -- Grace window: a consumed body is also kept until it is OLDER than v_ephemeral_grace_seconds, so an
  -- out-of-order straggler can still rewind through it (rewind uses the surviving bodies + a snapshot floor).
  -- TTL floor (E2-4c): an AfterTtl event carries its own absolute expiry in body metadata
  -- ('ephemeral_expires_at', stamped at dispatch). It EXTENDS retention, the consumed body is kept until it
  -- is ALSO past that expiry. An event with no key (Sourced / WhenConsumed) is unaffected: the gate is
  -- vacuously true, and it reaps as soon as consumed+aged.
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb
    USING __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_ephemeral_type_grace g ON g.event_type = es.event_type
    WHERE es.event_id = eb.event_id
      -- #13b4 safety gate: the reap is scoped to EPHEMERAL events explicitly. Pre-split this was
      -- guaranteed "by construction" (wh_event_body held only ephemeral bodies); once SOURCED bodies
      -- move into the body table (full split), this gate is what keeps the durable log un-reapable.
      AND (es.flags & 8) = 8
      -- E2-3 destruction hold: a PreDestruction hook may Cancel (hold far-future) or Defer(until) a body;
      -- while a hold is active the reap skips it, so the hook's decision is honoured.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
        WHERE h.event_id = eb.event_id AND h.hold_until > NOW()
      )
      AND es.created_at < NOW() - (COALESCE(g.grace_seconds, v_ephemeral_grace_seconds) * INTERVAL '1 second')
      -- E2-4c TTL retention floor: an AfterTtl body carries an absolute 'ephemeral_expires_at' in its
      -- metadata; it is kept until past that instant. No key (Sourced / WhenConsumed) => vacuously true.
      AND (
        eb.metadata ->> 'ephemeral_expires_at' IS NULL
        OR (eb.metadata ->> 'ephemeral_expires_at')::timestamptz < NOW()
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.event_id = eb.event_id
          AND pe.processed_at IS NULL
      )
      -- Snapshot-coverage gate: the reap must never outrun the rewind floor. Reap only once EVERY consuming
      -- perspective has a snapshot at/past this event's commit_sequence, i.e. there is no association whose
      -- perspective lacks a covering snapshot for the stream. The reap-driven step (MaintenanceWorker) drives
      -- those snapshots just before this runs, so coverage is normally satisfied; an event with no consuming
      -- perspective is vacuously covered, and an unstamped event (commit_sequence NULL) is held until stamped.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_message_associations ma
        WHERE ma.normalized_message_type = es.event_type
          AND ma.association_type = __CATEGORY_PERSPECTIVE__
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
            WHERE s.stream_id = es.stream_id
              AND s.perspective_name = ma.target_name
              AND s.snapshot_commit_sequence >= es.commit_sequence
          )
      );
    GET DIAGNOSTICS v_rows = ROW_COUNT;

    -- Keep the hold table bounded: drop holds whose body is already gone (a Defer whose window lapsed and
    -- was then reaped, or any body reaped by another path). A permanent Cancel keeps body + hold together.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h
    WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = h.event_id);
  END IF;
  RETURN QUERY SELECT
    'reap_consumed_ephemeral_bodies'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 9: Reap expired TtlRow perspective rows (E2-4d)
  -- ========================================
  -- TransientStorage.TtlRow perspective rows carry an expires_at (stamped on upsert = now + ttl). Once past,
  -- a row is logically expired (already hidden from lens reads) and is physically deleted here. Perspective
  -- tables are named per-app (wh_per_*), so this dynamically enumerates every wh_per_* table that HAS an
  -- expires_at column and deletes its expired rows. A non-TtlRow perspective's rows never get an expires_at
  -- value (NULL), so they are never matched. Skipped under debug_mode, like the body reaper.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    -- current_schema() (NOT the __SCHEMA__ placeholder): the EFCore schema-init replaces __SCHEMA__ with a
    -- QUOTED identifier ("public"), which is correct for `schema.table` refs but wrong inside a string literal
    -- compared to information_schema.table_schema (unquoted). current_schema() is the effective schema the
    -- maintenance connection runs in (same pattern as migration 046).
    FOR v_per_table IN
      SELECT table_name FROM information_schema.columns
      WHERE table_schema = current_schema()
        AND column_name = 'expires_at'
        AND table_name LIKE 'wh\_per\_%'
    LOOP
      EXECUTE format(
        'DELETE FROM %I.%I WHERE expires_at IS NOT NULL AND expires_at < NOW()',
        current_schema(), v_per_table);
      GET DIAGNOSTICS v_per_deleted = ROW_COUNT;
      v_rows := v_rows + v_per_deleted;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'reap_expired_perspective_rows'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 10: Purge expired instance-eviction tombstones (migration 106)
  -- ========================================
  -- The tombstone in wh_instance_evictions only needs to survive long enough for a genuinely
  -- paused instance to resume and be correctly refused. Once it is older than the retention
  -- window, either the instance is long dead for real, or, since instance ids are generated
  -- per PROCESS, not per deployment slot, anything still calling with that id is not the same
  -- process that was reaped. Keeping the row past that point only grows the table. Not gated on
  -- debug_mode: this is instance-identity bookkeeping, not forensic message data (same treatment
  -- as Task 6's abandoned-active-stream purge).
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_instance_evictions
  WHERE evicted_at < NOW() - (v_instance_eviction_retention_hours * INTERVAL '1 hour');
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_instance_evictions'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 11: Purge settled dead letters
  -- ========================================
  -- Recovered(3) means the message was successfully re-driven, so the row is a receipt rather than
  -- work. Every other status is either unresolved or a deliberate human hold, and none of those may
  -- be discarded on age alone. Skipped under debug_mode, where the operator asked to keep evidence.
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    -- Retention keys on when the row SETTLED (#682): a backlog older than the window would
    -- otherwise have its receipts deleted within one maintenance cycle of recovering ,
    -- recovered counts went BACKWARDS while a drain made real progress. recovered_at is
    -- NULL only on legacy rows settled before it was stamped; those fall back to the
    -- original failure time rather than living forever.
    -- A row referenced by an UNRESOLVED campaign's probe_ids is evidence, not clutter:
    -- deleting it resolves the campaign on an empty evidence set (see 127's evaluate).
    DELETE FROM __SCHEMA__.wh_dead_letters d
    WHERE d.recovery_status = 3
      AND COALESCE(d.recovered_at, d.dead_lettered_at) < NOW() - (v_dead_letter_retention_days || ' days')::INTERVAL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_dlq_probe_campaigns c
        WHERE c.verdict = 0 AND d.dead_letter_id = ANY(c.probe_ids)
      );
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_recovered_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 13: Settle orphaned perspective-event DEAD LETTERS (issue #687)
  -- ========================================
  -- Task 12 reaps orphans still in wh_perspective_events. A row that was dead-lettered before
  -- its event vanished sits in wh_dead_letters instead, held forever: recovery would re-drive
  -- it into an empty join, and generation replay excludes held rows by design. When the row's
  -- ENTIRE source stream is absent from wh_event_store, every event of it is gone, so the
  -- perspective work is unrecoverable, settle it (Recovered + note, same disposition as the
  -- disabled-subsystem discard) so the ledger records the disposal and retention ages it out.
  -- The whole-stream predicate needs no per-event lookup and has no false positives: a stream
  -- with any surviving event is left for review (a genuine apply failure, not an orphan). Age-
  -- gated on dead_lettered_at by the same grace window so a stream still being written is safe.
  -- Operator holds (operator_disposition 2/3) are respected.
  v_start := clock_timestamp();
  UPDATE __SCHEMA__.wh_dead_letters dl
  SET recovery_status = 3,  -- Recovered: settled, eligible for the retention purge
      recovered_at    = NOW(),
      operator_notes  = COALESCE(operator_notes || E'\n', '')
        || 'auto-settled by maintenance: orphaned perspective event, source stream absent from event store'
  WHERE dl.source_table = 'wh_perspective_events'
    AND dl.recovered_at IS NULL
    AND dl.recovery_status NOT IN (3, 4)
    AND dl.operator_disposition NOT IN (2, 3)
    AND dl.stream_id IS NOT NULL
    AND dl.dead_lettered_at < NOW() - (v_orphan_grace_hours * INTERVAL '1 hour')
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = dl.stream_id
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'settle_orphaned_perspective_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

END;
$$ LANGUAGE plpgsql;
-- ===========================================================================================
-- THE CUTOVER. Everything above has moved to wh_inbox_state; these columns now have no reader.
-- ===========================================================================================
-- Ten columns, every one of them rewritten by something, established by the exhaustive pass over
-- all 23 live columns rather than by discovery. Dropping them rather than leaving them ignored is
-- what makes the single cutover safe: an instance still running older code raises SQLSTATE 42703
-- on every claim, its loop reports a defect and backs off, and it takes no work and loses none. A
-- claim that silently succeeded into a column nobody reads would believe it held a lease it did
-- not, and dispatch work a second claimer would also take.
--
-- DROP COLUMN is a catalog update, not a table rewrite, and the twenty-one indexes that name these
-- columns go with them as a catalog update plus a file unlink. Measured at about 1.1 ms for all ten
-- at both 100,000 and 500,000 rows. The lock window is the backfill above, not this.
-- RECLAIM: the dropped bytes persist per EXISTING row. DROP COLUMN only flags the attribute in
--          pg_attribute; every row already on disk keeps the ten columns' bytes forever and
--          autovacuum never returns them. On this table that is the difference between the
--          2,070 bytes a row occupies today and the ~1,900 it would occupy rewritten, so the
--          write-amplification win this migration exists for is only PARTLY realized until a
--          rewrite happens. Operators should run pg_repack on wh_inbox after this migration;
--          VACUUM FULL or CLUSTER also work but take an exclusive lock for the duration.
--          Nothing here is incorrect without it -- the reclaim is a size and cache-footprint
--          matter, not a correctness one.
ALTER TABLE __SCHEMA__.wh_inbox
  DROP COLUMN IF EXISTS instance_id,
  DROP COLUMN IF EXISTS lease_expiry,
  DROP COLUMN IF EXISTS attempts,
  DROP COLUMN IF EXISTS processed_at,
  DROP COLUMN IF EXISTS scheduled_for,
  DROP COLUMN IF EXISTS failure_reason,
  DROP COLUMN IF EXISTS error,
  DROP COLUMN IF EXISTS chain_emitted_at,
  DROP COLUMN IF EXISTS status,
  DROP COLUMN IF EXISTS partition_number;

-- ===========================================================================================
-- BATCH ONE: the functions that touch only claim state. Each reads and writes the lease table
-- instead of wh_inbox, and nothing else about any of them changes.
-- ===========================================================================================

-- <docs>operations/workers/claim-backpressure</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CountOutstandingWorkSqlTests.cs:CountOutstandingWork_CountsOnlyLiveLeasedUnprocessedRowsForThisInstanceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CountOutstandingWorkSqlTests.cs:CountOutstandingWork_ReturnsZeroForAnInstanceHoldingNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CountOutstandingWorkSqlTests.cs:EFCoreCoordinator_ReportsTheSameFigureTheFunctionDoesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.count_outstanding_work(
  p_instance_id UUID
) RETURNS TABLE(
  inbox_rows BIGINT,
  outbox_rows BIGINT,
  perspective_rows BIGINT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    -- A live lease is the definition of "held": an expired lease is no longer this instance's
    -- work, whoever still has the row stamped. Counting expired leases would hold the budget
    -- closed against work the store has already made available to everyone else.
    -- 162: the inbox's claim state lives in wh_inbox_state. Reading it here also means this count
    -- no longer touches the wide message row at all.
    (SELECT count(*) FROM __SCHEMA__.wh_inbox_state ist
      WHERE ist.instance_id = p_instance_id
        AND ist.processed_at IS NULL
        AND ist.lease_expiry > NOW()),
    (SELECT count(*) FROM __SCHEMA__.wh_outbox o
      WHERE o.instance_id = p_instance_id
        AND o.processed_at IS NULL
        AND o.lease_expiry > NOW()),
    -- All three kinds count. Each is leased and each charges an attempt, so bounding one column
    -- alone would leave the identical arithmetic free to recur in another: the failure would
    -- move rather than stop.
    (SELECT count(*) FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.instance_id = p_instance_id
        AND pe.processed_at IS NULL
        AND pe.lease_expiry > NOW());
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BoundedAcquisitionRewriteSqlTests.cs:ReleaseUnstartedLeases_ReturnsOnlyTheNamedStreamsRefundsTheAttemptAndOpensThemToSiblingsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BoundedAcquisitionRewriteSqlTests.cs:ReleaseUnstartedLeases_NeverTouchesAnotherInstancesLeasesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.release_unstarted_leases(
  p_instance_id UUID,
  p_inbox_stream_ids UUID[],
  p_perspective_stream_ids UUID[]
) RETURNS TABLE(inbox_released INTEGER, perspective_released INTEGER) AS $$
DECLARE
  v_inbox INTEGER := 0;
  v_perspective INTEGER := 0;
  v_now TIMESTAMPTZ := NOW();
BEGIN
  IF p_inbox_stream_ids IS NOT NULL AND array_length(p_inbox_stream_ids, 1) > 0 THEN
    -- 162: stream_id is on the lease table as an immutable copy, so the whole predicate and the
    -- whole update are local to it and never touch the wide row.
    UPDATE __SCHEMA__.wh_inbox_state ist
    SET attempts = GREATEST(ist.attempts - 1, 0),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE ist.instance_id = p_instance_id
      AND ist.processed_at IS NULL
      AND ist.stream_id = ANY(p_inbox_stream_ids);
    GET DIAGNOSTICS v_inbox = ROW_COUNT;
  END IF;
  IF p_perspective_stream_ids IS NOT NULL AND array_length(p_perspective_stream_ids, 1) > 0 THEN
    UPDATE __SCHEMA__.wh_perspective_events pe
    SET attempts = GREATEST(pe.attempts - 1, 0),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE pe.instance_id = p_instance_id
      AND pe.processed_at IS NULL
      AND pe.stream_id = ANY(p_perspective_stream_ids);
    GET DIAGNOSTICS v_perspective = ROW_COUNT;
  END IF;
  -- End this instance's ownership of the released streams so the unowned path opens to siblings.
  UPDATE __SCHEMA__.wh_active_streams ast
  SET lease_expiry = v_now
  WHERE ast.assigned_instance_id = p_instance_id
    AND ast.stream_id = ANY(COALESCE(p_inbox_stream_ids, ARRAY[]::UUID[]) || COALESCE(p_perspective_stream_ids, ARRAY[]::UUID[]));
  RETURN QUERY SELECT v_inbox, v_perspective;
END;
$$ LANGUAGE plpgsql;

-- release_unprocessed_inbox has no documentation page. Recorded as a gap in
-- plans/inbox-work-state-side-table.md rather than pointed at an invented path.
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs:ReleaseUnprocessed_RefundsTheClaimAttemptAndClearsTheLeaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs:ReleaseUnprocessed_NeverDrivesAttemptsNegativeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs:RepeatedOverClaim_DoesNotAccumulateAttemptsOnUntouchedRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.release_unprocessed_inbox(
  p_instance_id UUID,
  p_message_ids UUID[]
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_released INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET
      -- GREATEST(...,0) keeps the budget from going negative under a duplicated release (retry,
      -- at-least-once flush, a shutdown path that runs twice). A negative budget would make the row
      -- effectively un-dead-letterable, trading this bug for an unbounded-retry one.
      attempts = GREATEST(ist.attempts - 1, 0),
      -- Clear the lease so the row is immediately claimable again rather than invisible until it
      -- would have expired. Handing work back is the whole point; making the caller wait out a lease
      -- it explicitly relinquished would just reintroduce the stall.
      instance_id = NULL,
      lease_expiry = NULL
  WHERE ist.message_id = ANY(p_message_ids)
    -- Scoped to the caller's OWN claim. A release from an instance that does not hold the row is a
    -- no-op: without this, one worker could unlock a row another instance is actively dispatching
    -- and two workers would handle the same message concurrently. This predicate is also what makes
    -- the call idempotent: after the first release instance_id is NULL, so a repeat matches nothing
    -- and cannot refund twice.
    AND ist.instance_id = p_instance_id
    -- Never disturb rows that already completed.
    AND ist.processed_at IS NULL;

  GET DIAGNOSTICS v_released = ROW_COUNT;
  RETURN v_released;
END;
$$;

-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:RenewLeases_ExtendsTheStreamLease_ForTheOwnersStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:RenewLeases_LeavesAStreamAssignedToAnotherInstanceAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RenewLeasesSqlTests.cs:RenewLeases_OutboxCategory_ExtendsLeaseExpiryAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.renew_leases(
  p_category TEXT,
  p_ids UUID[],
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS INTEGER AS $$
DECLARE
  v_new_expiry TIMESTAMPTZ := NOW() + (p_lease_seconds || ' seconds')::INTERVAL;
  v_updated INTEGER;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  CASE p_category
    WHEN __CATEGORY_OUTBOX__ THEN
      UPDATE __SCHEMA__.wh_outbox
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM __SCHEMA__.wh_outbox o
        WHERE o.message_id = ANY(p_ids)
          AND o.processed_at IS NULL
          AND o.stream_id = ast.stream_id
          AND o.instance_id = ast.assigned_instance_id;
    WHEN __CATEGORY_INBOX__ THEN
      -- 162: renewing a lease is the purest case for the split. It used to rewrite a row carrying
      -- the whole message body and twenty indexes to move one timestamp.
      UPDATE __SCHEMA__.wh_inbox_state
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM __SCHEMA__.wh_inbox_state ist
        WHERE ist.message_id = ANY(p_ids)
          AND ist.processed_at IS NULL
          AND ist.stream_id = ast.stream_id
          AND ist.instance_id = ast.assigned_instance_id;
    WHEN 'perspective_event' THEN
      UPDATE __SCHEMA__.wh_perspective_events
        SET lease_expiry = v_new_expiry
        WHERE event_work_id = ANY(p_ids)
          AND processed_at IS NULL;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.event_work_id = ANY(p_ids)
          AND pe.processed_at IS NULL
          AND pe.stream_id = ast.stream_id
          AND pe.instance_id = ast.assigned_instance_id;
    ELSE
      RAISE EXCEPTION 'renew_leases: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;

  RETURN v_updated;
END;
$$ LANGUAGE plpgsql;

-- <docs>messaging/work-coordinator</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorDeepPathTests.cs:DeregisterInstanceAsync_RegisteredInstanceWithLease_ReleasesLeaseAndRemovesInstanceRowAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorBroadTests.cs:DeregisterInstanceAsync_RemovesRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.deregister_instance(
  p_instance_id UUID,
  p_service_name TEXT DEFAULT NULL,
  p_host_name TEXT DEFAULT NULL
) RETURNS VOID AS $$
BEGIN
  -- Release all outbox leases held by this instance
  UPDATE __SCHEMA__.wh_outbox SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all inbox leases held by this instance. 162: claim state only, so the lease table.
  UPDATE __SCHEMA__.wh_inbox_state SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all perspective event leases held by this instance
  UPDATE __SCHEMA__.wh_perspective_events SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all receptor processing leases held by this instance
  UPDATE __SCHEMA__.wh_receptor_processing SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND completed_at IS NULL;

  -- Release all active stream assignments held by this instance
  UPDATE __SCHEMA__.wh_active_streams SET assigned_instance_id = NULL, lease_expiry = NULL
  WHERE assigned_instance_id = p_instance_id;

  -- Log shutdown to wh_log for audit trail (guaranteed persistence)
  INSERT INTO __SCHEMA__.wh_log (log_level, source, message_id, error_message, metadata)
  VALUES (
    1,  -- Information
    'shutdown',
    p_instance_id,
    'Instance deregistered during graceful shutdown',
    jsonb_build_object(
      'service_name', COALESCE(p_service_name, 'unknown'),
      'host_name', COALESCE(p_host_name, 'unknown')
    )
  );

  -- Remove the instance registration
  DELETE FROM __SCHEMA__.wh_service_instances WHERE instance_id = p_instance_id;
END;
$$ LANGUAGE plpgsql;

-- <docs>messaging/failure-handling</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxInboxFailureReasonSqlTests.cs:InboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_inbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
BEGIN
  IF jsonb_array_length(p_failures) = 0 THEN RETURN; END IF;

  FOR v_failure IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      (elem->>'CompletedStatus')::INTEGER as status_flags,
      elem->>'Error' as error_message,
      COALESCE(elem->>'Reason', elem->>'FailureReason')::INTEGER as failure_reason
    FROM jsonb_array_elements(p_failures) as elem
  LOOP
    -- 162: every column this writes is work state, including status, so it stays one statement
    -- against one table. An earlier draft split it in two because status was going to stay on
    -- wh_inbox; status moved once it turned out the held-lane index INCLUDEs it for an index-only
    -- probe, and the split went away with it.
    --
    -- Attempts are counted by claim_orphaned_inbox alone, so the backoff reads attempts as it
    -- stands: the value after the claim's bump is the attempt that just failed.
    UPDATE __SCHEMA__.wh_inbox_state ist
    SET status = ist.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
        error = v_failure.error_message,
        failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
        -- Exponential backoff: 30s * 2^attempts, capped at 5 minutes
        scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(ist.attempts, 10)), 10)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE ist.message_id = v_failure.msg_id;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

-- ===========================================================================================
-- BATCH ONE B and TWO: the remaining claim-state release, and the functions that need the
-- message row as well as its claim state.
-- ===========================================================================================

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CleanupStaleInstancesOrphanNotifySqlTests.cs:CleanupStaleInstances_OneStaleOneLive_EmitsOrphanOnLiveChannelAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CleanupStaleInstancesDefinitiveDeathSqlTests.cs:CleanupStaleInstances_HeartbeatPastDefinitiveCutoff_DeletesEvenWhenLockHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CleanupStaleInstancesDefinitiveDeathSqlTests.cs:CleanupStaleInstances_HeartbeatBeforeDefinitiveCutoff_LockGuardStillAppliesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.cleanup_stale_instances(
  p_stale_cutoff TIMESTAMPTZ,
  p_definitive_dead_cutoff TIMESTAMPTZ DEFAULT NULL
) RETURNS TABLE(deleted_instance_id UUID) AS $$
DECLARE
  v_deleted_ids UUID[];
BEGIN

  -- Find and delete stale instances (older than cutoff). v0.681: also skip rows
  -- whose session-level alive-lock is still held (migration 055): the adaptive
  -- heartbeat cadence may legitimately delay the heartbeat write past p_stale_cutoff
  -- when the direct conn is healthy. The lock is the primary liveness signal in
  -- that mode; the heartbeat-table check remains the fallback.
  --
  -- v0.687: the alive-lock guard has a long-tail failure mode under OOMKill +
  -- half-open TCP. The kernel SIGKILLs the process before any graceful socket
  -- teardown, so the server-side session keeps holding the advisory lock until
  -- OS-level TCP keepalive notices (defaults to 7200 s = 2 h on Linux). Within
  -- that window cleanup_stale_instances refuses to remove the dead row, which
  -- in turn keeps that instance_id on every claimed lease in wh_inbox / wh_outbox
  -- / wh_perspective_events, and claim_orphaned_* cannot release the work because
  -- those rows still have a future lease_expiry and a non-null instance_id.
  --
  -- The optional p_definitive_dead_cutoff lets callers say: "if the heartbeat
  -- table has been silent for THIS long, the instance is definitely dead, so bypass
  -- the alive-lock guard and clean it up." The lock guard still applies in the
  -- short window (heartbeat stale but newer than the definitive cutoff) so we
  -- preserve the adaptive-heartbeat correctness case. NULL preserves pre-v0.687
  -- behavior (single-arg callers get the legacy semantics).
  WITH deleted AS (
    DELETE FROM __SCHEMA__.wh_service_instances
    WHERE last_heartbeat_at < p_stale_cutoff
      AND (
        -- v0.687 definitive-dead bypass: heartbeat is older than the caller's
        -- "we don't trust the lock past this point" threshold. Skip the guard.
        (p_definitive_dead_cutoff IS NOT NULL
          AND last_heartbeat_at < p_definitive_dead_cutoff)
        OR
        -- v0.681 alive-lock guard: respect the lock as the primary liveness
        -- signal within the adaptive-heartbeat window.
        NOT EXISTS (
          -- pg_locks.classid/objid are oid (uint32). hashtext() returns signed int4, so when
          -- negative, the lower-32-bit lane evaluates >2^31-1 as bigint, which overflows
          -- ::int (22003). Compare against the bigint expression and cast to ::oid so the
          -- comparison stays in oid-space without sign-flip.
          SELECT 1 FROM pg_locks
          WHERE locktype = 'advisory'
            AND classid = ((hashtext('wh_instance_alive:' || wh_service_instances.instance_id::text)::bigint >> 32) & x'FFFFFFFF'::bigint)::oid
            AND objid = (hashtext('wh_instance_alive:' || wh_service_instances.instance_id::text)::bigint & x'FFFFFFFF'::bigint)::oid
            AND granted = true
        )
      )
    RETURNING instance_id
  )
  SELECT ARRAY_AGG(instance_id) INTO v_deleted_ids
  FROM deleted;

  -- Release all work from deleted instances
  IF v_deleted_ids IS NOT NULL THEN
    -- Tombstone every reaped instance so a paused process that resumes and calls
    -- record_heartbeat again is refused rather than silently rejoining. See migration 106.
    -- ON CONFLICT is defensive only: an instance_id cannot be re-deleted once gone, so a
    -- collision here would mean a caller reused an id, which this must not paper over by
    -- discarding the earlier eviction's timestamp.
    INSERT INTO __SCHEMA__.wh_instance_evictions (instance_id, evicted_at, reason)
    SELECT unnest(v_deleted_ids), NOW(), 'stale heartbeat (last_heartbeat_at < ' || p_stale_cutoff || ')'
    ON CONFLICT (instance_id) DO NOTHING;

    -- Release outbox messages
    UPDATE __SCHEMA__.wh_outbox
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release inbox messages
    -- 162: the inbox's claim state lives in wh_inbox_state, so releasing a dead instance's inbox
    -- leases no longer rewrites the message rows. This is the reclaim path that runs most often on
    -- a fleet losing pods, and it was the one paying the most per row released.
    UPDATE __SCHEMA__.wh_inbox_state
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release perspective events
    UPDATE __SCHEMA__.wh_perspective_events
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release active stream assignments from deleted instances
    UPDATE __SCHEMA__.wh_active_streams
    SET assigned_instance_id = NULL,
        lease_expiry = NULL
    WHERE assigned_instance_id = ANY(v_deleted_ids);

    -- Release receptor processing leases from deleted instances
    UPDATE __SCHEMA__.wh_receptor_processing
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Log stale instance removal to wh_log for audit trail
    INSERT INTO __SCHEMA__.wh_log (log_level, source, message_id, error_message, metadata)
    SELECT
      2,  -- Warning
      'stale_cleanup',
      unnest(v_deleted_ids),
      'Stale instance removed, all leases released',
      jsonb_build_object(
        'deleted_instance_count', array_length(v_deleted_ids, 1),
        'stale_cutoff', p_stale_cutoff
      );

    -- v0.502 slice B.3: orphan-redistribution NOTIFY.
    -- After releasing leases owned by the dead instances, wake every LIVE instance so it
    -- runs a catch-up claim_orphaned_* over the newly-unowned rows. Without this, live
    -- instances only discover the released work on their next poll tick, which under the
    -- new v0.502 NotifyHealthyPollingIntervalMilliseconds=30000 default could be up to
    -- 30 seconds away. Emitting a NOTIFY here turns orphan recovery from polling-bound to
    -- NOTIFY-bound, the architectural goal of v0.502.
    --
    -- Per-instance channel naming matches existing PgWorkNotificationListener.ChannelName:
    --   wh_work_i_{instance_id}
    -- Payload "orphan" signals "go run claim_orphaned_*" to ClaimWorker._onSignal.
    PERFORM pg_notify('wh_work_i_' || si.instance_id::text, 'orphan')
    FROM __SCHEMA__.wh_service_instances si
    WHERE si.last_heartbeat_at >= p_stale_cutoff;  -- live instances only
  END IF;

  -- Return deleted IDs for orchestrator logging
  RETURN QUERY
  SELECT UNNEST(COALESCE(v_deleted_ids, ARRAY[]::UUID[]));
END;
$$ LANGUAGE plpgsql;

-- <docs>messaging/work-coordinator</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorLifecycleAndJanitorTests.cs:PurgeOrphanInboxAsync_UnhandledUnleasedRows_DeletesAndReturnsThemAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorLifecycleAndJanitorTests.cs:PurgeOrphanInboxAsync_EmptyHandledTypes_IsSafeNoOpAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorWithDataTests.cs:PurgeOrphanInboxAsync_OrphanRow_DeletesAndReturnsMappedRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.purge_orphan_inbox(p_handled_types TEXT[])
RETURNS TABLE(message_id UUID, message_type TEXT, handler_name TEXT) AS $$
BEGIN
  IF p_handled_types IS NULL OR array_length(p_handled_types, 1) IS NULL THEN
    -- No types handed in: do nothing. Caller has nothing to filter against,
    -- and we don't want to accidentally truncate the inbox if the registry
    -- is empty during cold start.
    RETURN;
  END IF;

  -- 162: message_type is a property of the message and stays on wh_inbox, while processed_at and
  -- instance_id are claim state and moved, so this needs both tables. The DELETE stays on wh_inbox
  -- because that is the row being removed; the lease row goes with it through the foreign key's
  -- ON DELETE CASCADE rather than a second statement that could be forgotten.
  RETURN QUERY
  DELETE FROM __SCHEMA__.wh_inbox AS i
  USING __SCHEMA__.wh_inbox_state ist
  WHERE ist.message_id = i.message_id
    AND i.message_type <> ALL(p_handled_types)
    AND ist.processed_at IS NULL
    AND ist.instance_id IS NULL
  -- ::TEXT casts required: the columns are VARCHAR(500) but the RETURNS TABLE
  -- declares TEXT, and plpgsql RETURN QUERY rejects the varchar->text mismatch (42804)
  RETURNING i.message_id, i.message_type::TEXT, i.handler_name::TEXT;
END;
$$ LANGUAGE plpgsql;
-- <docs>fundamentals/work-coordinator/handler-commit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HappyPath_MarksInboxProcessedAndStoresOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_DebugModeInRequest_RetainsInboxRowEvenWhenEventStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_ProductionMode_DeletesInboxRowOnEventStoredCompletionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_inbox_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_deleted BOOLEAN
) AS $$
DECLARE
  v_completion RECORD;
  v_current_status INTEGER;
  v_new_status INTEGER;
  v_stream_id UUID;
BEGIN
  IF jsonb_array_length(p_completions) = 0 THEN RETURN; END IF;

  FOR v_completion IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      (elem->>'Status')::INTEGER as status_flags
    FROM jsonb_array_elements(p_completions) as elem
  LOOP
    -- Get current status and stream_id
    -- 162: both columns are on the state table now (stream_id as an immutable copy), so the
    -- lookup never touches the wide row.
    SELECT ist.status, ist.stream_id
    INTO v_current_status, v_stream_id
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.message_id = v_completion.msg_id;

    -- Skip if message not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    v_new_status := v_current_status | v_completion.status_flags;

    IF p_debug_mode THEN
      -- Debug mode: Retain message for troubleshooting
      -- 162: status is work state too, so this stays one statement against one table.
      UPDATE __SCHEMA__.wh_inbox_state ist
      SET status = v_new_status,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE ist.message_id = v_completion.msg_id;

      RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;

    ELSE
      -- Production: Delete if EventStored flag set (inbox completion = event stored)
      IF (v_new_status & 2) = 2 THEN
        -- 162: unchanged. The lease row goes with it through ON DELETE CASCADE, which is why the
        -- foreign key exists rather than being tidiness: a lease left behind for a deleted message
        -- is a row every reclaim path would keep considering forever.
        DELETE FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, TRUE AS was_deleted;
      ELSE
        -- Event not yet stored, retain with updated status
        -- 162: same single statement as the debug branch above.
        UPDATE __SCHEMA__.wh_inbox_state ist
        SET status = v_new_status,
            processed_at = p_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE ist.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
-- Exactly one overload per framework function: this name is defined at more than one
-- arity across the migration set, and CREATE OR REPLACE at a different arity ADDS an
-- overload beside the old one rather than replacing it. The duplicate then makes every
-- unqualified reference ambiguous (42725) -- including this file's own COMMENT ON
-- FUNCTION -- which fails the whole startup pass and strands every later migration.
SELECT __SCHEMA__.drop_all_overloads('fetch_inbox_batch');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchSqlTests.cs:FetchInboxBatch_ReturnsRowsForOwnedStreams_InReceivedAtOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchSqlTests.cs:FetchInboxBatch_FiltersOutOtherInstancesRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchSqlTests.cs:FetchInboxBatch_FiltersRowsWithProcessedAtSet_DebugModeRetainedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_inbox_batch(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  handler_name VARCHAR(200),
  message_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  error TEXT,
  priority INTEGER
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Ordering invariant: sort by (stream_id, message_id). UUIDv7 message_ids ARE chronological;
  -- received_at is wall-clock at insert and may diverge under parallel transport delivery.
  -- See plans/ordered-stream-invariant.md.
  --
  -- 162: this function needs both tables, because it returns the message and selects on its claim
  -- state. The LEASE table drives the join: every predicate below is on it, so the narrow table is
  -- scanned and the wide row is fetched only for the rows that survive. The previous form scanned
  -- the wide row to apply the same predicate. i.* is enumerated rather than starred because the
  -- moved columns are no longer on wh_inbox and a star would silently change the result shape.
  RETURN QUERY
  WITH ranked AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.handler_name,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      ist.status,
      ist.attempts,
      ist.partition_number,
      ist.is_event,
      ist.error,
      i.priority,
      ROW_NUMBER() OVER (PARTITION BY ist.stream_id ORDER BY i.message_id) AS rank_in_stream
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    -- v0.658 slice 7: mirror of fetch_outbox_batch's Empty/NULL stream handling -- see the matching
    -- comment in the outbox query for the full rationale.
    WHERE (
        ist.stream_id = ANY(p_stream_ids)
        OR ((ist.stream_id IS NULL OR ist.stream_id = __EMPTY_UUID__::uuid)
            AND ist.message_id = ANY(p_stream_ids))
      )
      AND ist.instance_id = p_instance_id
      AND ist.lease_expiry > NOW()
      AND ist.processed_at IS NULL  -- inbox uses processed_at as both production-marker and debug-kept-marker
      AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= NOW())
  ),
  -- Running byte total in the SAME order the rows are returned, so the cut is a suffix of the
  -- slice and stream-FIFO is preserved. Measured on the payload columns because those are what
  -- cross the wire and land on the heap; the fixed-width columns are noise by comparison.
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id ORDER BY r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
    WHERE r.rank_in_stream <= p_max_per_stream
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.handler_name::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.error,
    b.priority
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.message_id;
END;
$$ LANGUAGE plpgsql;

-- <docs>messaging/dead-letters</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MoveToDeadLettersSqlTests.cs:MoveToDeadLetters_InboxRow_MovesIntoDlqAndDeletesSourceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DlqStackTracePreservationSqlTests.cs:MoveToDeadLetters_PreservesFullStackTextAcrossAllSourceTablesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MoveToDeadLettersFingerprintSqlTests.cs:MoveToDeadLetters_DistinctErrorTexts_DistinctFingerprintsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.move_to_dead_letters(
  p_dead_letter_id UUID,                                       -- caller generates (TrackedGuid.NewMedo on C# side)
  p_source_table  TEXT,
  p_source_id     UUID,
  p_failure_reason INTEGER,
  p_error_text    TEXT,
  p_instance_id   UUID,
  p_generation    TEXT
) RETURNS UUID AS $$
DECLARE
  v_stream_id      UUID;
  v_message_type   TEXT;
  v_destination    TEXT;
  v_perspective    TEXT;
  v_envelope       JSONB;
  v_metadata       JSONB;
  v_attempts       INTEGER;
  v_source_error   TEXT;
  v_error_text     TEXT;
  v_prior_recovery_attempts INTEGER;
BEGIN

  -- Snapshot per source table. Each branch reads the canonical columns and DELETEs in a
  -- single CTE so the row movement is atomic. The wh_outbox / wh_inbox tables carry the
  -- envelope + metadata directly; wh_perspective_events carries an event_id pointer that
  -- the recovery worker will rejoin against wh_event_store at recovery time.
  IF p_source_table = 'wh_outbox' THEN
    WITH moved AS (
      DELETE FROM __SCHEMA__.wh_outbox
      WHERE message_id = p_source_id
      RETURNING
        stream_id,
        message_type,
        destination,
        event_data,
        metadata,
        attempts
    )
    SELECT m.stream_id, m.message_type, m.destination,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, m.attempts
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts
    FROM moved m;
  ELSIF p_source_table = 'wh_inbox' THEN
    -- 162: attempts and error moved to wh_inbox_state, and a DELETE's RETURNING clause cannot reach
    -- another table's columns. The lease row is read in a sibling CTE instead, which is correct
    -- because every CTE in one statement sees the same pre-statement snapshot: the lease row is
    -- still visible here even though the DELETE's ON DELETE CASCADE is removing it in the same
    -- statement.
    --
    -- LEFT JOIN rather than an inner one, deliberately. If the lease row were somehow missing, an
    -- inner join would return no row, leave v_attempts NULL, and the guard below would raise after
    -- the message had already been deleted, which loses it. The outer join always returns the moved
    -- row, so a missing lease surfaces as the existing NULL-attempts guard rather than as a lost
    -- message.
    WITH lease AS (
      SELECT ist.attempts, ist.error
      FROM __SCHEMA__.wh_inbox_state ist
      WHERE ist.message_id = p_source_id
    ),
    moved AS (
      DELETE FROM __SCHEMA__.wh_inbox
      WHERE message_id = p_source_id
      RETURNING
        stream_id,
        message_type,
        event_data,
        metadata
    )
    SELECT m.stream_id, m.message_type, NULL::TEXT,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, l.attempts, l.error
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts, v_source_error
    FROM moved m LEFT JOIN lease l ON TRUE;
  ELSIF p_source_table = 'wh_perspective_events' THEN
    WITH moved AS (
      DELETE FROM __SCHEMA__.wh_perspective_events
      WHERE event_work_id = p_source_id
      RETURNING
        stream_id,
        perspective_name,
        event_id,
        attempts,
        error
    )
    SELECT m.stream_id, m.perspective_name,
           jsonb_build_object('event_id', m.event_id, 'perspective_name', m.perspective_name),
           '{}'::JSONB, m.attempts, m.error
      INTO v_stream_id, v_perspective, v_envelope, v_metadata, v_attempts, v_source_error
    FROM moved m;
    v_message_type := 'perspective_event';
  ELSE
    RAISE EXCEPTION 'move_to_dead_letters: unsupported source table %', p_source_table;
  END IF;

  -- If no row was found (already DLQ'd, already DELETEd by another path), no-op.
  IF v_attempts IS NULL THEN
    RETURN NULL;
  END IF;

  -- Issue #518: the retry budget belongs to the MESSAGE, not to one dead-letter row.
  -- move_to_dead_letters mints a NEW dead_letter_id on every re-failure, and the recovery
  -- worker's exhaustion check reads recovery_attempts off THAT row, so a message that fails
  -- again after recovery restarted from zero, HoldForReviewAfterExhaustion could never engage,
  -- and a single poison message cycled indefinitely (observed: one message dead-lettered 257
  -- times in 15 minutes; 46k rows from 7.6k distinct messages, enough churn to exhaust a shared
  -- database's connection limit). Seeding the new row with the attempts already spent on this
  -- same (source_table, source_id) makes the budget cumulative across incarnations, so the
  -- ladder terminates. A first-time failure still gets its full budget (COALESCE to 0).
  -- Scoped to the CURRENT generation on purpose: a new build is a new chance. Generation-tagged
  -- auto-replay ("we shipped a fix, replay the casualties") depends on previously-exhausted
  -- messages getting a fresh budget after a deploy; a globally-cumulative counter would hold
  -- them forever and silently kill that recovery path.
  SELECT COALESCE(SUM(recovery_attempts), 0) INTO v_prior_recovery_attempts
  FROM __SCHEMA__.wh_dead_letters
  WHERE source_table = p_source_table
    AND source_id = p_source_id
    AND generation IS NOT DISTINCT FROM p_generation;

  -- Migration 118: preserve the source row's stored terminal error (wh_perspective_events.error
  --, the actual apply exception) alongside the caller's promotion wrapper. Without this, DLQ
  -- rows carried only "attempts=N > max=M" and the root cause was unrecoverable once pod logs
  -- rotated. The fingerprint is computed on the ENRICHED text so failure modes cluster by the
  -- real exception instead of collapsing into one wrapper cluster.
  v_error_text := CASE
    WHEN v_source_error IS NOT NULL AND v_source_error <> ''
      THEN COALESCE(p_error_text || ', last error: ', '') || v_source_error
    ELSE p_error_text
  END;

  -- Slice 3a of release/v0.645.0-alpha.1, auto-fingerprint every row at INSERT
  -- time via Slice 2's compute_dead_letter_fingerprint. One source of truth
  -- (the SQL function), three call sites (this INSERT, plus Slice 6's
  -- aggregate_dead_letters version-aware backfill). NULL p_error_text →
  -- NULL fingerprint + NULL version so the column NULLability flows through.
  INSERT INTO __SCHEMA__.wh_dead_letters (
    dead_letter_id,
    source_table,
    source_id,
    stream_id,
    message_type,
    destination,
    perspective_name,
    envelope,
    metadata,
    failure_reason,
    error_text,
    error_fingerprint,
    error_fingerprint_version,
    attempts_when_dlq,
    dead_lettered_by,
    generation,
    recovery_attempts
  ) VALUES (
    p_dead_letter_id,
    p_source_table,
    p_source_id,
    v_stream_id,
    v_message_type,
    v_destination,
    v_perspective,
    v_envelope,
    v_metadata,
    p_failure_reason,
    v_error_text,
    __SCHEMA__.compute_dead_letter_fingerprint(v_error_text),
    CASE WHEN v_error_text IS NOT NULL
         THEN __SCHEMA__.current_dead_letter_fingerprint_version()
         ELSE NULL
    END,
    v_attempts,
    p_instance_id,
    p_generation,
    v_prior_recovery_attempts
  );

  RETURN p_dead_letter_id;
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:SingleMessage_StoresInboxAndDedupAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:DuplicateMessageId_SecondCallNoOpsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.store_inbox_messages(
  p_messages JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_newly_created BOOLEAN
) AS $$
#variable_conflict use_column
DECLARE
  c_field_flags CONSTANT TEXT := __ENVELOPE_FIELD_FLAGS__;
  v_msg RECORD;
  v_partition INTEGER;
  v_observations INTEGER;  -- 1 on first sight; N on the Nth redelivery
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_inbox_streams UUID[] := ARRAY[]::UUID[];
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      elem->>'HandlerName' as handler_name,
      elem->>'EnvelopeType' as envelope_type,
      elem->>'MessageType' as message_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- 149: the effective priority the consumer classified; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) as priority,
      -- EventFlags (062): same robust read as store_outbox_messages so collective events delivered
      -- cross-service via the inbox also route to the __collective__ sink.
      CASE
        WHEN elem->>c_field_flags IS NULL OR elem->>c_field_flags = '' THEN 0
        WHEN elem->>c_field_flags ~ '^[0-9]+$' THEN (elem->>c_field_flags)::INTEGER
        WHEN elem->>c_field_flags ILIKE '%Collective%' THEN 1
        ELSE 0
      END as flags,
      -- 146 (#727): a zero GUID is "unknown", not a producer; COALESCE below then falls back to this service.
      NULLIF((elem->>'SourceServiceId')::UUID, __EMPTY_UUID__::UUID) as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 121: DO UPDATE (was DO NOTHING) so a redelivery is COUNTED rather than silently swallowed.
    -- RETURNING gives the post-write count on both arms, so newness is read from the value
    -- (= 1) instead of from ROW_COUNT, which DO UPDATE would report as 1 either way.
    INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
      (message_id, first_seen_at, observation_count)
    VALUES (v_msg.msg_id, p_now, 1)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
      SET observation_count = dedup.observation_count + 1
    RETURNING dedup.observation_count INTO v_observations;

    IF v_observations = 1 THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- 114: emptiness probe, see store_outbox_messages; inbox pending = not processed
      -- and schedule-eligible (the drain-fetch predicate minus the lease dimension).
      IF v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
        v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

        -- 162: all three columns are work state now, so the probe reads the narrow table.
        --
        -- The ORDER BY is the remedy from 160 and 161, applied here BEFORE it bites rather than
        -- after. This is the third instance of the same shape: an emptiness probe by stream id with
        -- processed_at IS NULL under a bare LIMIT 1. The planner prices a sequential scan as though
        -- the first row it reads will match, because that predicate is true of nearly every row in
        -- a table carrying a backlog, and on a stream whose work is all done there is no match at
        -- all, so it reads the whole table. Ordering by the index key removes that plan instead of
        -- out-costing it, and stream_id is pinned by the equality so it changes no result. received_at
        -- is in the clause because ordering by stream_id alone is dropped as redundant.
        PERFORM 1 FROM __SCHEMA__.wh_inbox_state ist
          WHERE ist.stream_id = v_msg.stream_id
            AND ist.processed_at IS NULL
            AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= p_now)
          ORDER BY ist.stream_id, ist.received_at
          LIMIT 1
          FOR SHARE OF ist SKIP LOCKED;   -- 140: a locked pending row is being completed; treat as empty and ring
        IF NOT FOUND THEN
          v_empty_inbox_streams := array_append(v_empty_inbox_streams, v_msg.stream_id);
        END IF;
      END IF;

      -- 162: the message row and its claim state are two inserts now. Both carry a DO NOTHING
    -- conflict clause, so the pair is idempotent under the redelivery this function exists to
    -- absorb: a duplicate leaves the existing message AND its existing claim state untouched,
    -- rather than resetting a lease some instance is holding.
    INSERT INTO __SCHEMA__.wh_inbox (
      message_id,
      handler_name,
      message_type,
      event_data,
      metadata,
      scope,
      stream_id,
      is_event,
      flags,
      received_at,
      source_service_id,
      source_commit_sequence,
      priority
    ) VALUES (
      v_msg.msg_id,
      v_msg.handler_name,
      v_msg.message_type,
      COALESCE(v_msg.envelope_data, '{}'::jsonb),
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      COALESCE(v_msg.is_event, false),
      COALESCE(v_msg.flags, 0),
      p_now,
      COALESCE(v_msg.source_service_id, (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)),
      COALESCE(v_msg.source_commit_sequence, 0),
      v_msg.priority
    )
    ON CONFLICT ON CONSTRAINT wh_inbox_pkey DO NOTHING;

    -- The lease row, unleased and unattempted. A message with no lease row can never be claimed and
    -- would fail silently, so this insert is not optional bookkeeping: it is what makes the message
    -- visible to the claim at all.
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      status, processed_at, instance_id, lease_expiry, attempts
    ) VALUES (
      v_msg.msg_id,
      v_msg.stream_id,
      p_now,
      v_partition,
      v_msg.priority,
      COALESCE(v_msg.is_event, false),
      1,     -- Stored flag
      NULL,
      NULL,  -- No lease, immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      0      -- Initial attempts
    )
    ON CONFLICT (message_id) DO NOTHING;

      IF v_msg.stream_id IS NOT NULL
         AND v_msg.stream_id = ANY(v_empty_inbox_streams)
         AND NOT (v_msg.stream_id = ANY(v_notify_inbox_streams)) THEN
        v_notify_inbox_streams := array_append(v_notify_inbox_streams, v_msg.stream_id);
      END IF;

      -- Pinning is ownership/routing, unchanged by 114.
      IF v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
        INSERT INTO __SCHEMA__.wh_active_streams
          (stream_id, partition_number, assigned_instance_id, last_activity_at)
        VALUES
          (v_msg.stream_id, COALESCE(v_partition, 0), p_instance_id, p_now)
        ON CONFLICT (stream_id) DO NOTHING;
      END IF;
      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, TRUE AS was_newly_created;
    END IF;  -- end of the single-observation branch
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_INBOX__, v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;
-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmitChainInboxIndexTests.cs:EmitChainInboxIndex_HasExpectedPartialPredicateAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  v_local_service_id UUID;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
BEGIN
  -- Migration 087: resolve the LOCAL service id once. store_inbox_messages (062) COALESCEs a
  -- missing envelope SourceServiceId to the local id, so a wh_inbox row attributed to SELF (or
  -- zero) is a locally-originated event (loopback), its origin_service_id must stay NULL,
  -- matching the 046 contract ("NULL for locally-originated events").
  SELECT service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config LIMIT 1;

  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Reproduced in production on a consumer's
  -- service during job creation. Lock order is hashtext(stream_id::text), sorted ASC,
  -- ensures deadlock-free nesting between any pair of transactions touching overlapping stream
  -- sets. pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    -- 162: every column this pass reads is on wh_inbox_state, so the lock pass never touches the
    -- wide message row at all. chain_emitted_at moved here precisely so this predicate stays on one
    -- table and one partial index can cover it; see idx_inbox_state_chain_pending.
    SELECT DISTINCT ist.stream_id AS sid
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      -- 158: only rows this chain has never read (idx_inbox_state_chain_pending); every held row was
      -- read against the event store on every poll before, twice.
      AND ist.chain_emitted_at IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
    ORDER BY ist.stream_id
  ) AS streams_to_lock;

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   - are events (is_event = true)
  --   - have a stream_id
  --   - aren't yet in wh_event_store (idempotent, ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- Phase H step 10 slice 1: ORDER BY i.message_id (UUIDv7 = chronological at the source) so
  -- version assignment matches canonical event_id order. See _emit_event_store_chain above
  -- for the rationale, same fix applies to the inbox backfill path.
  -- Migration 061: i.flags carried into wh_event_store.flags (was dropped here previously).
  WITH inbox_events AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.flags,
      i.source_service_id,
      i.source_commit_sequence,
      ist.received_at,
      ROW_NUMBER() OVER (PARTITION BY ist.stream_id ORDER BY i.message_id) AS row_num
    -- 162: this pass needs the message body, so both tables. The LEASE table drives the join, so
    -- selection happens on the narrow table and the wide row is fetched only for the rows that
    -- survive it, which is the opposite of scanning the wide row to apply the same predicate.
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL  -- 158: see the lock pass above
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, see _emit_event_store_chain above for the rationale.
  -- Migration 072: materialise the extracted payload + built metadata ONCE (see outbox fn above).
  computed AS (
    SELECT
      ie.message_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(ie.message_type) AS event_type,
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload') AS body_data,
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN ie.metadata IS NOT NULL
             AND jsonb_typeof(ie.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((ie.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS body_meta,
      ie.scope,
      ie.row_num,
      ie.flags,
      -- Migration 087: normalize the received origin, self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = __EMPTY_UUID__::uuid
             OR ie.source_service_id = v_local_service_id
        THEN NULL
        ELSE ie.source_service_id
      END AS origin_service_id,
      NULLIF(ie.source_commit_sequence, 0) AS origin_commit_sequence
    FROM inbox_events ie
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, origin_service_id, origin_commit_sequence
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id), 0) + c.row_num,
      p_now,
      c.flags,
      -- Migration 087: stamp the origin identity the transport delivered (046 columns were never
      -- populated by the emit chain before this, consumer-side origin-keyed verification needs them).
      c.origin_service_id,
      CASE WHEN c.origin_service_id IS NULL THEN NULL ELSE c.origin_commit_sequence END
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- Migration 077 (full split): offload EVERY body (see outbox fn above).
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT
      COALESCE(c.origin_service_id, __EMPTY_UUID__::uuid),
      COALESCE(c.scope::jsonb ->> 't', ''),
      c.event_type,
      c.stream_id,
      bit_xor(hashtextextended(c.message_id::text, 0)),
      bit_xor(hashtextextended(c.message_id::text, 1)),
      COUNT(*)::int,
      p_now
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      AND COALESCE((c.body_meta ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
  )
  SELECT array_agg(event_id) INTO v_stored_event_ids FROM stored_events;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 158: stamp the held rows the chain is finished with, so the next poll reads none of them.
  -- "Finished" is the row's event being in the event store: either this pass inserted it, or it was
  -- already there and the pass above excluded the row for exactly that reason. A row whose insert hit
  -- ON CONFLICT DO NOTHING has no event-store row, so it stays unstamped and the next poll re-attempts
  -- it with a fresh version snapshot, which is the contract that conflict clause states. The stamp runs
  -- before the nothing-was-stored return below, because that return is the steady state of a busy
  -- instance (every held event already chained) and the case the stamp exists for. The stamp and the
  -- inserts share this transaction, so a failure leaves both undone.
  -- 162: the stamp is the write this migration helps most. It used to rewrite a row carrying the
  -- whole message body across twenty-four indexes to set one timestamp, measured at about 41 blocks
  -- per row stamped with only 30 of those spent finding the rows. It now writes the narrow table,
  -- and the predicate that finds them is local to it.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET chain_emitted_at = p_now
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Auto-create perspective_events for the newly-stored events.
  -- Phase H step 6 slice 2: populate partition_number for symmetric load balancing.
  -- Slice 26.14: route the lease through wh_active_streams (live owner wins; fall back to
  -- commit instance when no live owner). Mirror of the outbox-side change in
  -- _emit_event_store_chain.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- 100: reconcile self-declares the rewind. Any of THIS invocation's fresh work items whose
  -- event id slots BELOW its cursor's last-applied event is a straggler by construction, a
  -- backfilled event keeps its ORIGINAL id, while its fresh local commit_sequence sits above
  -- the cursor and hides it from the runtime inversion detector forever. Flag the cursor
  -- RewindRequired with the earliest straggler as trigger (min-merge with any existing
  -- trigger, mirroring complete_perspective_checkpoint's straggler path); the worker's
  -- existing rewind routing replays the stream through the corrected order.
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN s.straggler_event_id
        WHEN s.straggler_event_id < pc.rewind_trigger_event_id THEN s.straggler_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
  FROM (
    SELECT pe.stream_id, pe.perspective_name,
           (array_agg(pe.event_id ORDER BY pe.event_id))[1] AS straggler_event_id
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_perspective_cursors c
      ON c.stream_id = pe.stream_id AND c.perspective_name = pe.perspective_name
    WHERE pe.event_id = ANY(v_stored_event_ids)
      AND pe.processed_at IS NULL
      AND c.last_event_id IS NOT NULL
      AND pe.event_id < c.last_event_id
    GROUP BY pe.stream_id, pe.perspective_name
  ) s
  WHERE pc.stream_id = s.stream_id AND pc.perspective_name = s.perspective_name;

  -- Migration 061: collective-event routing (inbox path). See _emit_event_store_chain above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    c_collective_sink,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND (es.flags & c_flag_collective) = c_flag_collective
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. See _emit_event_store_chain above for
  -- the rationale and dedup semantics. Inbox backfill is generally a smaller fan-in
  -- than outbox-emit, but the NOTIFY is just as cheap and keeps the stamper hot path
  -- responsive whether events arrive locally or via transport.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;
-- ===========================================================================================
-- BATCH THREE: the two claim functions. Both become PURE wh_inbox_state functions.
-- ===========================================================================================
-- Checked column by column before substituting: claim_orphaned_inbox uses thirteen columns and
-- claim_work twelve, and after status moved there is not one column left on wh_inbox that either
-- of them reads. So the rewrite is the table name and nothing else: aliases, predicates, lane
-- structure, ordering gate and bounds are all untouched, which is deliberate. These are the two
-- functions where a behavioral change would be hardest to see and most expensive to get wrong, and
-- a substitution is reviewable in a way a rewrite is not.
--
-- This is also where the measured win lands. The gated pick fell from 582,640 blocks to 1,317 on a
-- churned fixture, and the durable part is the plan shape: the old form sequential-scanned 19,460
-- rows and ran the per-stream ordering probe on every one before sorting and taking ten, because
-- the planner would not walk a priority-ordered index on a table that wide. On the narrow table it
-- walks the index and stops after 206 rows. The bound is reached before the per-row work.

-- Exactly one overload per framework function: this name is defined at more than one
-- arity across the migration set, and CREATE OR REPLACE at a different arity ADDS an
-- overload beside the old one rather than replacing it. The duplicate then makes every
-- unqualified reference ambiguous (42725) -- including this file's own COMMENT ON
-- FUNCTION -- which fails the whole startup pass and strands every later migration.
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_inbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_AnInteractiveStream_IsClaimedAheadOfOlderStandardStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_AStreamWithAnInteractiveRowBehindStandardRows_IsPulledForward_InOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimOrphanedInbox_HonorsTheRowLimitItIsGivenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimOrphanedInbox_ChargesAnAttemptOnlyToRowsItActuallyClaimsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:ClaimOrphanedInbox_LeasesTheStream_WithTheRowLeaseExpiryAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
DECLARE
  -- 150: the bands (Whizbang.Core.Priority.WorkPriority) and the scheduling constants. The wait target and the
  -- floor are the framework's defaults for this release; a host's batch hook adjusts a stream's number in memory.
  c_interactive_band_end CONSTANT INTEGER := 99;
  c_standard_band_end CONSTANT INTEGER := 199;
  c_background_wait_target_seconds CONSTANT INTEGER := 300;
  c_background_floor_share CONSTANT NUMERIC := 0.1;
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION, not just re-emission. Without a limit here this statement leases every
  -- eligible row in one shot and charges an attempt to each, so an instance restarting onto a large
  -- backlog claims the whole thing instantly. The rows it cannot dispatch inside the lease expire
  -- un-dispatched, get re-claimed here, spend another attempt, and eventually dead-letter as
  -- MaxAttemptsExceeded having never reached a receptor - the error branch below is this statement
  -- stamping its own casualties.
  --
  -- The caller's claim limit previously reached claim_orphaned_perspective_events but not this
  -- function, so both the adaptive claim window and the outstanding budget were throttling a valve
  -- downstream of the flood: p_max_streams bounds only the RE-EMISSION of work already held
  -- (claim_work's eligible_inbox filters on instance_id = p_instance_id). That is why narrowing the
  -- window changed the rate of lease saturation without ever converging.
  --
  -- LIMIT NULL is unlimited in Postgres, so the default preserves the old behavior for any caller
  -- that has not been taught to pass a bound.
  WITH params AS (
    -- BIGINT: the unbounded default is INT max and max_rows + 1 below must not overflow.
    SELECT COALESCE(p_max_rows, 2147483647)::BIGINT AS max_rows,
           -- 150: a background stream that has waited past the background wait target competes as standard.
           p_now - (c_background_wait_target_seconds * INTERVAL '1 second') AS background_promote_before,
           -- 150: the share of a batch the background bucket always receives while it has pending streams.
           GREATEST(1, CEIL(LEAST(COALESCE(p_max_rows, 2147483647), 1000000) * c_background_floor_share))::BIGINT AS background_floor,
           -- 159: rows of one band and one ownership class the acquisition will look at. A multiple
           -- of the batch, and a constant against the backlog, which is the whole point: the window
           -- is what makes a poll cost the same whether the queue holds a thousand rows or a
           -- million. It cannot cost the acquisition its throughput, only its breadth: the lanes
           -- claim p_max_rows rows either way, and a narrower window means a lane meets fewer
           -- distinct streams and takes more rows from each (the few-mode branch below), which is
           -- the same batch drawn less widely. The window is over the OLDEST claimable rows and
           -- those are what the poll claims, so it advances every poll rather than re-reading one
           -- set forever.
           GREATEST(LEAST(COALESCE(p_max_rows, 1000), 1000000), 1)::BIGINT * 8 AS claim_window
  ),
  -- 145: ownership is a per-STREAM property. The two sets below are built ONCE per call from the small
  -- ledger tables and tested by hash membership. 138 evaluated the same predicate as correlated
  -- EXISTS probes on every pending inbox row, which is one of the two reasons the acquisition scan
  -- grew with the backlog instead of the batch (#714). "Live" keeps 025's meaning: a heartbeat within
  -- p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity.
  others_live AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
    WHERE ast.assigned_instance_id <> p_instance_id
      AND ast.lease_expiry > p_now
      AND (
        si.last_heartbeat_at >= p_stale_cutoff
        OR EXISTS (
          SELECT 1 FROM pg_stat_activity sa
          WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || si.instance_id::text
        )
      )
  ),
  mine_owned AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
  ),
  -- 159: the rows this call may take, read once, in an order an index carries. Every lane below
  -- selects from here instead of from wh_inbox_state, and each had re-derived the same predicate for
  -- itself:
  --
  --   WHERE processed_at IS NULL AND (instance_id IS NULL OR lease_expiry < p_now)
  --
  -- A disjunction has no index order, so each of the five walked the whole pending inbox and threw
  -- away what a live peer holds -- one plan removed 4,716 rows of 4,800 by filter to yield seven
  -- streams -- and fetched a heap page per row doing it, because the stream-ordered index carries
  -- neither is_event nor priority and a queue table under load is never all-visible, so nothing on
  -- it is ever really index-only. Five passes over the backlog per poll, several polls a second,
  -- every instance. Doubling the backlog doubled the poll.
  --
  -- Split in two, each half is a partial predicate a planner can prove, so 159's indexes carry the
  -- order: unowned rows by arrival, leased rows by lease expiry so the longest-expired come first.
  -- One walk per band per class, each stopped at the window, each covered by its index. The bucket
  -- is the leading key so a band draws from its own range: a large background backlog can never
  -- crowd an interactive row out of the window, and the background floor below still finds the
  -- background band populated. Rows of one stream keep their arrival order, which is what per-stream
  -- FIFO rests on, and the lanes' own band, kind, ownership and partition filters are unchanged --
  -- this narrows what they read, never what they choose.
  --
  -- The window is a bound the LIMITs below could not provide on their own. A lane stops at its
  -- LIMIT only when it can reach it, and during an import most pending rows belong to a peer or to
  -- another rank, so a lane that wants eleven streams and can see seven never stops early and runs
  -- to the end of the table. The bound has to be on rows examined, not on rows found.
  --
  -- The scheduled_for predicate rides along: a row that is not due yet is not claimable now, and
  -- every lane applied it.
  -- Each lane names its band as a LITERAL, and the reason is the only reason: a partial index is
  -- considered only when Postgres can prove the query's predicate implies the index's, and that
  -- proof is textual. The previous shape selected a band by comparing a CASE expression to a bucket
  -- column joined in from a VALUES list, which is not provable against ANY band predicate because
  -- the bucket is a join column and not a constant. Every band index was therefore unusable, and
  -- the lanes fell back to reading the whole pending set once per bucket -- O(backlog) on the
  -- hottest path in the system, while the three indexes they were meant to read went on charging
  -- every claim write for nothing. Measured on 300k pending rows with the held-lane index in
  -- place, which is the comparison that is actually fair: 11,102 buffers and 5,249ms against
  -- 5 buffers and under 20ms. The old shape DID reach an index -- the held-lane one -- but it
  -- leads with instance_id, so for the unowned set it reads the whole backlog per bucket and
  -- top-N sorts, because received_at sits late in its key. The band indexes are keyed on
  -- (received_at, message_id), which is the ORDER BY, so they stop at the LIMIT.
  --
  -- A plpgsql constant would reintroduce the same defect. c_interactive_band_end is a PARAMETER at
  -- plan time, and the generic plan plpgsql settles into after a few executions cannot fold it, so
  -- the bounds are written here as the same literals the index predicates carry. That agreement is
  -- held by PriorityLaneIndexUsabilityTests against WorkPriority, not by hope.
  --
  -- The four lanes partition the pending set with no overlap: interactive takes the whole band
  -- regardless of kind (its index has no is_event predicate), the two event lanes take their bands,
  -- and commands take everything else that is not an event. Each keeps its own claim_window, so a
  -- deep background band cannot consume the budget an interactive row needs.
  claimable AS MATERIALIZED (
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.priority <= 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.is_event = TRUE
       AND i.priority >= 99 + 1
       AND i.priority <= 199
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.is_event = TRUE
       AND i.priority > 199
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.is_event = FALSE
       AND i.priority > 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NOT NULL AND i.lease_expiry < p_now
       AND i.priority <= 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.lease_expiry, i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NOT NULL AND i.lease_expiry < p_now
       AND i.priority > 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.lease_expiry, i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
  ),
  -- 150 BUCKET LANES (priority step 3). Every row carries an effective priority (149); the claim schedules by the
  -- bucket the number falls in: interactive (1 to 99), standard (100 to 199), background (200 and up). Lane 0 is
  -- every stream with a pending interactive row anywhere in it: the fold is over all of a stream's pending rows,
  -- so an interactive row queued behind bulk rows on its own stream pulls the whole stream forward while the
  -- stream's rows are still taken in order (its predecessors are prerequisites). That set is small by nature and
  -- served by idx_inbox_pending_interactive, so ranking it is bounded by the pending interactive rows, not the
  -- backlog. Lanes 1 and 2 keep 145's breadth-first selection with an early stop, run once per band over the
  -- band's own arrival-order index, so each lane's cost still follows the batch. Background streams keep a floor
  -- of every batch (never starved by a steady standard flow), and a background stream that has waited past the
  -- background wait target competes as standard. Inside a lane a command keeps its place ahead of events (145's
  -- command lane, #721). Priority reorders streams, never rows within a stream.
  urgent_streams AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.priority <= c_interactive_band_end
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows FROM params)
  ),
  pick_urgent AS (
    SELECT i.message_id AS cand_message_id,
           0 AS lane,
           CASE WHEN i.is_event THEN 1 ELSE 0 END AS kind,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    JOIN urgent_streams us ON us.stream_id = i.stream_id
    WHERE TRUE
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
  ),
  urgent_taken AS (
    SELECT LEAST((SELECT count(*) FROM pick_urgent), (SELECT max_rows FROM params))::BIGINT AS n
  ),
  -- Commands outside the urgent streams: a request someone is waiting on keeps its lane ahead of events, inside
  -- the band its number puts it in. Served by idx_inbox_pending_commands; the set is small by nature.
  pick_commands AS (
    SELECT i.message_id AS cand_message_id,
           CASE WHEN (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params)) THEN 2 ELSE 1 END AS lane,
           0 AS kind,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE TRUE
      AND i.is_event = FALSE
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
  ),
  commands_taken AS (
    SELECT LEAST((SELECT count(*) FROM pick_commands),
                 GREATEST((SELECT max_rows FROM params) - (SELECT n FROM urgent_taken), 0))::BIGINT AS n
  ),
  budget AS (
    SELECT GREATEST((SELECT max_rows FROM params) - (SELECT n FROM urgent_taken) - (SELECT n FROM commands_taken), 0) AS remaining_total
  ),
  -- The background reservation: as many rows as the floor, bounded by what is actually pending (a bounded probe)
  -- and by what the batch has left after the urgent rows and the commands.
  background_reserved AS (
    SELECT LEAST((SELECT background_floor FROM params),
                 (SELECT count(*) FROM (
                    SELECT 1 FROM claimable i
                    WHERE TRUE
                      AND i.is_event = TRUE
                      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
                      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
                      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
                    LIMIT (SELECT background_floor FROM params)) probe),
                 (SELECT remaining_total FROM budget))::BIGINT AS n
  ),
  -- LANE 1: standard rows, plus background rows promoted past the wait target; breadth-first, early stop.
  eligible_event_streams_std AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.is_event = TRUE
      AND (i.priority BETWEEN c_interactive_band_end + 1 AND c_standard_band_end OR (i.priority > c_standard_band_end AND i.received_at < (SELECT background_promote_before FROM params)))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows + 1 FROM params)
  ),
  mode_std AS (
    SELECT (SELECT count(*) FROM eligible_event_streams_std) AS n_streams,
           GREATEST((SELECT remaining_total FROM budget) - (SELECT n FROM background_reserved), 0) AS remaining
  ),
  event_heads_std AS (
    SELECT i.message_id AS cand_message_id,
           1 AS lane,
           1 AS kind,
           1::BIGINT AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE (SELECT n_streams > remaining FROM mode_std)
      AND i.is_event = TRUE
      AND (i.priority BETWEEN c_interactive_band_end + 1 AND c_standard_band_end OR (i.priority > c_standard_band_end AND i.received_at < (SELECT background_promote_before FROM params)))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox_state j
        WHERE j.stream_id = i.stream_id
          AND j.processed_at IS NULL
          AND j.is_event = TRUE
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
          AND (j.instance_id IS NULL OR j.lease_expiry < p_now)
          AND (j.scheduled_for IS NULL OR j.scheduled_for <= p_now)
          AND (j.partition_number IS NULL
               OR (j.partition_number % p_active_instance_count) = p_instance_rank
               OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = j.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      )
    ORDER BY i.received_at, i.message_id
    LIMIT (SELECT remaining FROM mode_std)
  ),
  kk_std AS (
    SELECT GREATEST(1, CEIL(remaining::NUMERIC / GREATEST(n_streams, 1)))::INTEGER AS k FROM mode_std
  ),
  event_few_std AS (
    SELECT h.message_id AS cand_message_id,
           1 AS lane,
           1 AS kind,
           h.rn AS stream_seq,
           h.received_at AS cand_received_at
    FROM eligible_event_streams_std s
    CROSS JOIN kk_std
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at,
             ROW_NUMBER() OVER (ORDER BY i.received_at, i.message_id) AS rn
      FROM claimable i
      WHERE i.stream_id = s.stream_id
        AND i.is_event = TRUE
        AND (i.partition_number IS NULL
             OR (i.partition_number % p_active_instance_count) = p_instance_rank
             OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      ORDER BY i.received_at, i.message_id
      LIMIT kk_std.k
    ) h
    WHERE (SELECT n_streams <= remaining FROM mode_std)
  ),
  standard_taken AS (
    SELECT ((SELECT count(*) FROM event_heads_std) + (SELECT count(*) FROM event_few_std))::BIGINT AS n
  ),
  -- LANE 2: background rows inside the wait target; whatever the standard lane left, never less than the floor.
  eligible_event_streams_bg AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.is_event = TRUE
      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows + 1 FROM params)
  ),
  mode_bg AS (
    SELECT (SELECT count(*) FROM eligible_event_streams_bg) AS n_streams,
           GREATEST((SELECT remaining_total FROM budget) - (SELECT n FROM standard_taken), 0) AS remaining
  ),
  event_heads_bg AS (
    SELECT i.message_id AS cand_message_id,
           2 AS lane,
           1 AS kind,
           1::BIGINT AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE (SELECT n_streams > remaining FROM mode_bg)
      AND i.is_event = TRUE
      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox_state j
        WHERE j.stream_id = i.stream_id
          AND j.processed_at IS NULL
          AND j.is_event = TRUE
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
          AND (j.instance_id IS NULL OR j.lease_expiry < p_now)
          AND (j.scheduled_for IS NULL OR j.scheduled_for <= p_now)
          AND (j.partition_number IS NULL
               OR (j.partition_number % p_active_instance_count) = p_instance_rank
               OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = j.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      )
    ORDER BY i.received_at, i.message_id
    LIMIT (SELECT remaining FROM mode_bg)
  ),
  kk_bg AS (
    SELECT GREATEST(1, CEIL(remaining::NUMERIC / GREATEST(n_streams, 1)))::INTEGER AS k FROM mode_bg
  ),
  event_few_bg AS (
    SELECT h.message_id AS cand_message_id,
           2 AS lane,
           1 AS kind,
           h.rn AS stream_seq,
           h.received_at AS cand_received_at
    FROM eligible_event_streams_bg s
    CROSS JOIN kk_bg
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at,
             ROW_NUMBER() OVER (ORDER BY i.received_at, i.message_id) AS rn
      FROM claimable i
      WHERE i.stream_id = s.stream_id
        AND i.is_event = TRUE
        AND (i.partition_number IS NULL
             OR (i.partition_number % p_active_instance_count) = p_instance_rank
             OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      ORDER BY i.received_at, i.message_id
      LIMIT kk_bg.k
    ) h
    WHERE (SELECT n_streams <= remaining FROM mode_bg)
  ),
  pick AS (
    -- A stream with rows in two bands is picked by both lanes (each takes the stream's heads in order); keep
    -- the more urgent lane's row so no message is a candidate twice.
    SELECT DISTINCT ON (u.cand_message_id) u.cand_message_id, u.lane, u.kind, u.stream_seq, u.cand_received_at
    FROM (
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM pick_urgent
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM pick_commands
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_heads_std
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_few_std
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_heads_bg
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_few_bg
    ) u
    ORDER BY u.cand_message_id, u.lane
  ),
  -- 159: the batch is cut before the row lookup, not after it. The lanes together offer several
  -- times the batch, every one of them was fetched by primary key to be ordered and then discarded,
  -- and the whole ordering key (lane, kind, stream order, arrival, id) is already carried here --
  -- nothing the lookup returns takes part in it. A few batches of headroom is kept so SKIP LOCKED
  -- below still has rows to fall through to when a peer holds the head of the order; past that a
  -- contended poll returns a short batch and the next poll takes the rest, which is what SKIP LOCKED
  -- means everywhere else in this function.
  pick_ordered AS (
    SELECT u.cand_message_id, u.lane, u.kind, u.stream_seq, u.cand_received_at
    FROM pick u
    ORDER BY u.lane, u.kind, u.stream_seq, u.cand_received_at, u.cand_message_id
    LIMIT (SELECT max_rows * 4 FROM params)
  ),
  candidates AS (
    -- Lock under lane order. SKIP LOCKED skips rows a concurrent claimer holds, and the volatile predicates
    -- re-check under the lock - a row leased between pick and here is filtered exactly as before.
    SELECT i.message_id AS cand_message_id,
           pick.lane AS cand_lane,
           pick.kind AS cand_kind,
           pick.stream_seq AS cand_stream_seq,
           pick.cand_received_at
    FROM __SCHEMA__.wh_inbox_state i
    JOIN pick_ordered pick ON pick.cand_message_id = i.message_id
    WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND i.processed_at IS NULL
    ORDER BY pick.lane, pick.kind, pick.stream_seq, pick.cand_received_at, pick.cand_message_id
    LIMIT (SELECT max_rows FROM params)
    FOR UPDATE OF i SKIP LOCKED
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_inbox_state i
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: claim_orphaned is the SOLE source of attempt counting.
        -- Bumps unconditionally on every claim (fresh or re-claim) so attempts = 1 means
        -- "first attempt has started" (one-based). process_inbox_failures records error and
        -- releases the lease but does NOT bump - the next claim's bump captures attempt N+1.
        -- Single-source removes the double-counting that two bumps per failed cycle would cause.
        -- Without this, hung handlers (no exception thrown, lease eventually expires) looked
        -- identical to fresh messages in a consumer's production environment - an extended stuck-message backlog with
        -- attempts=0 across thousands of rows (production audit).
        attempts = i.attempts + 1,
        -- Attribute the attempt this claim is REPLACING. process_inbox_failures records
        -- error/failure_reason only when dispatch reported a failure; when the process is killed
        -- mid-dispatch (SIGKILL from a failed liveness probe, container replaced, handler hung past
        -- its lease) nothing reports anything - the lease just expires and the bump above spends
        -- another attempt in silence. The budget then runs out and the row dead-letters as
        -- "MaxAttemptsExceeded: attempts=N > max=M", which describes the counter and not the cause.
        --
        -- Observed in production as ~54k inbox rows averaging 11 attempts, every one with
        -- error IS NULL and failure_reason = 99 (Unknown) - no way to tell a crash-looping host
        -- from a genuinely failing handler. Stamp the abandonment so the row carries its own
        -- history: an expired lease still held by an instance (instance_id IS NOT NULL) means the
        -- previous attempt ended without ever reporting. Guarded on error IS NULL so a real
        -- recorded failure is never papered over - that error is the better diagnostic.
        failure_reason = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 6  -- MessageFailureReason.LeaseExpired
          ELSE i.failure_reason
        END,
        error = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 'Attempt ' || i.attempts || ' ended without a reported outcome: lease held by instance '
               || i.instance_id::text || ' expired at ' || i.lease_expiry::text
               || ' (process terminated mid-dispatch, or the handler outran its lease). '
               || 'No dispatch failure was recorded for that attempt.'
          ELSE i.error
        END
    -- Ownership and partition routing were resolved in `candidates` above, under a row lock. Only
    -- the two cheap invariants are re-asserted here, as defense against a lease that lapsed between
    -- selection and write:
    --
    --   OWNER PATH - a stream's live owner always claims its messages, ignoring partition modulo,
    --   which preserves per-stream FIFO and prevents the rank-churn wedge seen in production: when
    --   active_instance_count changes, modulo routing for a partition can shift to a different rank
    --   than the stream's existing owner. Without that branch the modulo-matched instance is blocked
    --   by the ownership NOT EXISTS and the owner is blocked by the modulo filter - neither claims.
    --
    --   UNOWNED / ABANDONED PATH - no live owner, so partition-based load balancing decides the rank.
    --   NULL partition_number (no stream binding) stays claimable by any rank. "Live" means a
    --   heartbeat within p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity; an
    --   instance killed by SIGKILL holds no meaningful ownership, and without the recency clause its
    --   dead lease would block cross-instance claims for the full lease duration (300 s default).
    --   See 011 (cleanup_stale_instances) for the eventual DELETE - this claim-time check is what
    --   makes recovery happen at the stale threshold rather than at lease expiry.
    FROM candidates c
    WHERE i.message_id = c.cand_message_id
      AND i.processed_at IS NULL
      AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
    RETURNING i.message_id AS c_message_id, i.stream_id AS c_stream_id, i.partition_number AS c_partition_number,
              c.cand_lane AS c_lane, c.cand_kind AS c_kind, c.cand_stream_seq AS c_stream_seq, c.cand_received_at AS c_received_at
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into REFRESH (row-only
  -- UPDATE for already-owned-with-live-lease streams) + PIN (INSERT...ON CONFLICT for
  -- the rare ownership-transition case, with ORDER BY stream_id for consistent lock
  -- acquisition). Symmetric with the fix in claim_orphaned_outbox (mig 024); see that
  -- migration for the full rationale. Eliminates the 40P01 deadlock observed in
  -- production (Whizbang PR #227).
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND c.c_stream_id IS NOT NULL
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, COALESCE(c.c_partition_number, 0) AS partition_number
      FROM claimed c
      WHERE c.c_stream_id IS NOT NULL
        AND NOT EXISTS (
          SELECT 1 FROM refreshed r WHERE r.refreshed_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO UPDATE
      SET last_activity_at = EXCLUDED.last_activity_at,
          assigned_instance_id = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.assigned_instance_id
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.assigned_instance_id
            ELSE ast.assigned_instance_id
          END,
          -- 148 (#731): the stream lease follows the assignment. Whoever the CASE above leaves as owner
          -- holds the lease: a stream this instance takes (unowned, orphaned by a deregistered instance,
          -- or already its own) is leased to p_lease_expiry; a stream another registered instance keeps
          -- is not touched.
          lease_expiry = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.lease_expiry
            WHEN ast.assigned_instance_id = EXCLUDED.assigned_instance_id THEN EXCLUDED.lease_expiry
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.lease_expiry
            ELSE ast.lease_expiry
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  -- 150: the result carries the pick order (bucket lane, commands before events, then breadth-first). UPDATE ... RETURNING
  -- alone promises no order, and a caller that consumes the rows in order relies on the lane.
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c
  ORDER BY c.c_lane, c.c_kind, c.c_stream_seq, c.c_received_at, c.c_message_id;
END;
$$ LANGUAGE plpgsql;

-- Exactly one overload per framework function: this name is defined at more than one
-- arity across the migration set, and CREATE OR REPLACE at a different arity ADDS an
-- overload beside the old one rather than replacing it. The duplicate then makes every
-- unqualified reference ambiguous (42725) -- including this file's own COMMENT ON
-- FUNCTION -- which fails the whole startup pass and strands every later migration.
SELECT __SCHEMA__.drop_all_overloads('claim_work');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkSqlTests.cs:ClaimWork_OutboxHasUnprocessedWork_ReturnsThatWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimWork_DoesNotAcquireMoreThanItsCallerAskedForAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_BackgroundStreams_KeepAFloorOfTheBatchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300,
  p_fresh_share DOUBLE PRECISION DEFAULT 0.5,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE,
  p_max_perspective_streams INTEGER DEFAULT NULL
) RETURNS TABLE(
  source VARCHAR(20),           -- __CATEGORY_OUTBOX__ | __CATEGORY_INBOX__ | 'receptor' | __CATEGORY_PERSPECTIVE__
  work_id UUID,
  work_stream_id UUID,
  partition_number INTEGER,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  message_data TEXT,
  metadata JSONB,
  status INTEGER,
  attempts INTEGER,
  is_newly_stored BOOLEAN,
  is_orphaned BOOLEAN,
  perspective_name VARCHAR(200),
  priority INTEGER,             -- 150: the inbox row's effective priority (NULL for other sources)
  received_at TIMESTAMPTZ       -- 150: the inbox row's arrival (NULL for other sources)
) AS $$
DECLARE
  c_source_outbox CONSTANT VARCHAR(20) := __CATEGORY_OUTBOX__;
  c_source_inbox CONSTANT VARCHAR(20) := __CATEGORY_INBOX__;
  v_has_any_work BOOLEAN;
BEGIN
  -- 157: this function runs under plan_cache_mode = force_custom_plan (see its closing clause), so
  -- the plans below and in the acquisition functions it calls are made for the tables as they are
  -- at each poll, never kept from a poll that found them empty.
  -- Empty-call short-circuit: cheap indexed EXISTS lookups on partial indexes.
  -- Each LIMIT 1 against an existing partial index is sub-millisecond when buffer-cached.
  -- Note: wh_receptor_processing uses completed_at (not processed_at) for the "is done" semantic.
  -- 115: coalesce-pending rows are not claim-pump work, they wait for the coalesce worker.
  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL AND coalesce_group IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox_state WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_receptor_processing WHERE completed_at IS NULL LIMIT 1);

  IF NOT v_has_any_work THEN
    RETURN;  -- empty result set; orphan-claim sub-functions never invoked
  END IF;

  -- Non-empty path: claim outbox work and return it.
  -- Inbox / receptor / perspective claim + return land in subsequent TDD cycles.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    v_count INTEGER;
    -- v0.661: track per-category RETURN QUERY rowcount so the drain-mode hint
    -- can be derived from ROW_COUNT instead of four fresh COUNT(*) queries.
    v_outbox_rows INTEGER := 0;
    v_inbox_rows INTEGER := 0;
    v_receptor_rows INTEGER := 0;
    v_perspective_rows INTEGER := 0;
    -- 158: the lane walks that re-offer held inbox and perspective streams.
    v_bucket INTEGER;
    v_is_event BOOLEAN;
    v_start UUID;
    v_remaining INTEGER;
    v_rows INTEGER;
  BEGIN
    -- Self-heal this instance's own registration before ranking against it. When a pod's heartbeat
    -- lapses past the stale cutoff (a GC pause, thread-pool starvation, a database failover), the
    -- stale-instance cleanup reaps its wh_service_instances row. Before this repair, every claim
    -- from that point on failed on the missing row, and because the failure aborted the claim, no
    -- work on the claim path was left to put the row back -- the instance stayed locked out until
    -- it was restarted. Repairing here closes that loop, using the identity the caller already
    -- passes in, so the restored row carries the real service name / host / process id rather than
    -- a placeholder.
    --
    -- Guarded by an indexed primary-key pre-check: claim_work is polled continuously, so an
    -- unconditional UPSERT would write a new row version per poll per instance and bloat the
    -- table. On the healthy path this performs no write at all. Only last_heartbeat_at is
    -- refreshed on conflict -- metadata stays owned by record_heartbeat.
    IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_service_instances
      WHERE instance_id = p_instance_id
        AND last_heartbeat_at >= v_stale_cutoff
    ) THEN
      INSERT INTO __SCHEMA__.wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES
        (p_instance_id, p_service_name, p_host_name, p_process_id, v_now, v_now)
      ON CONFLICT (instance_id) DO UPDATE SET
        last_heartbeat_at = EXCLUDED.last_heartbeat_at;
    END IF;

    SELECT instance_rank, active_instance_count INTO v_rank, v_count
    FROM __SCHEMA__.calculate_instance_rank(p_instance_id, v_stale_cutoff);

    -- v0.683, per-inner-function guards. The existing v_has_any_work short-circuit
    -- (top of the function) only fires when ALL four queues are empty. Under steady
    -- import load, that's rare, but the typical pattern is "one queue has work,
    -- the others don't." Without per-function guards, claim_work paid the full
    -- claim_orphaned_*/emit_chain scan cost on every call regardless. Each guard
    -- uses an existing partial index (idx_{outbox,inbox}_unprocessed_claiming WHERE
    -- processed_at IS NULL, etc.) so the EXISTS probe is sub-millisecond. Behavior
    -- is preserved: if a guard returns false, the corresponding inner function had
    -- no rows to claim anyway, so skipping its scan is a pure win.

    -- Claim orphaned / unowned outbox work, only if any outbox row is unprocessed
    -- AND either unowned or has an expired lease (the orphan predicate matched by
    -- claim_orphaned_outbox's WHERE clause).
    -- 158: two index probes, not one scan. A single predicate over "unowned or expired" has to examine
    -- every pending row to prove there is none, which on a busy instance is its whole holdings on
    -- every poll. Unowned rows are found under instance_id IS NULL through the outstanding-by-instance
    -- index (123); expired leases are the head of the lease-expiry index (158), so an instance whose
    -- leases are all live proves it at the first entry.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE processed_at IS NULL
        AND coalesce_group IS NULL  -- 115: pending singles are never orphan-claimable
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND coalesce_group IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- Bounded for the same reason as the inbox call below: without a limit this acquires the whole
      -- eligible backlog in one statement, and the caller's claim limit never reaches the flood.
      PERFORM __SCHEMA__.claim_orphaned_outbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        p_max_streams
      );
    END IF;

    -- Claim orphaned / unowned inbox work, same predicate shape.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- p_max_streams bounds ACQUISITION here, not just the re-emission below. Omitting it let this
      -- call lease the entire eligible backlog in one statement, charging an attempt to every row ,
      -- while the caller's claim window and outstanding budget bounded only what came back out of
      -- eligible_inbox. Both throttles sat downstream of the flood, so neither could ever have held.
      -- 145: acquisition has its own ROW bound. p_max_streams is a stream count; handing it to the
      -- acquisition as a row cap turned fat streams into one row per cycle (#714). The caller passes
      -- the rows it can afford; a caller that does not is bounded by the stream count as before.
      PERFORM __SCHEMA__.claim_orphaned_inbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        COALESCE(p_max_rows, p_max_streams), p_allow_steal
      );
    END IF;

    -- Claim orphaned perspective events, same predicate shape on
    -- wh_perspective_events.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- 145 (#719): perspective ACQUISITION has its own bound. The caller passes 0 while its drain channel is
      -- above its cap, so a perspective backlog is drained before more is leased; re-emission of held work
      -- below stays on p_max_streams so the drain keeps moving.
      -- 160: the perspective acquisition gets a ROW bound as well as its stream bound. It used to
      -- take a batch of streams and lease every pending event of each, so one poll leased whatever
      -- a consumer's streams happened to hold and cost accordingly. The caller's own budget is used
      -- when it passes one; otherwise a multiple of the batch, because handing a stream count
      -- straight to an acquisition as a row cap is 145's #714 mistake in reverse.
      PERFORM __SCHEMA__.claim_orphaned_perspective_events(
        p_instance_id, v_lease_expiry, v_now, COALESCE(p_max_perspective_streams, p_max_streams), v_rank, v_count,
        COALESCE(p_max_rows, GREATEST(COALESCE(p_max_perspective_streams, p_max_streams), 1) * 8)
      );
    END IF;

    -- Claim orphaned receptor work, wh_receptor_processing uses completed_at
    -- (not processed_at) for the "is done" semantic.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_receptor_processing
      WHERE completed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_receptor_work(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now
      );
    END IF;

    -- Back-fill wh_event_store + wh_perspective_events for inbox events claimed above.
    -- Replaces legacy process_work_batch Phase 4.5B + 4.6 self-healing, ensures that by the
    -- time an inbox event row reaches InboxDispatchWorker, its event_store row exists and
    -- perspective_events have been created so PerspectiveWorker can pick them up.
    --
    -- v0.683 guard: only call when this instance owns at least one unprocessed
    -- inbox event row with a stream_id. emit_chain's own internal NOT EXISTS
    -- check against wh_event_store filters out already-emitted rows; we don't
    -- repeat that check here because a production measurement
    -- showed the wrapping NOT EXISTS predicate at 42 ms mean (~4-5% of total
    -- DB time) under heavy inbox load, overwhelming the savings from skipping
    -- emit_chain. The simpler EXISTS uses idx_inbox_instance_lease and is
    -- sub-millisecond. The handler-delay backlog scenario where every event_id
    -- is already in wh_event_store is rare and is more appropriately addressed
    -- on the handler side (composite events) than in the work-pump.
    -- 158: only rows the chain has never read (idx_inbox_chain_pending). A holder in steady state
    -- has none, and the poll skips the chain without touching a held row.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state i
      WHERE i.instance_id = p_instance_id
        AND i.processed_at IS NULL
        AND i.is_event = true
        AND i.stream_id IS NOT NULL
        AND i.chain_emitted_at IS NULL
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__._emit_event_store_chain_for_inbox(p_instance_id, v_lease_expiry, v_now, p_partition_count);
    END IF;

    -- Return outbox work owned by this instance, oldest first, a batch of it.
    -- 158: a walk of idx_outbox_held_arrival that stops at the batch. The previous shape ranked every
    -- held row per stream with a window function it never read and sorted them all before taking the
    -- batch, so the poll priced itself by the holdings: a busy instance holds thousands of leased rows
    -- and polls several times a second. Same rows in the same order, ties broken by message id.
    RETURN QUERY
    -- Per-stream-drain projection (Phase H step 5b): claim_work returns stream_ids only for
    -- outbox. The OutboxDrainWorker consumes WorkBatch.OutboxStreamIds and pulls full payloads
    -- on demand via fetch_outbox_batch. Body columns are NULL, keeps the bytes-on-the-wire
    -- proportional to the active stream set, not the leased-row count × payload size.
    SELECT
      c_source_outbox               AS source,
      o.message_id                  AS work_id,
      o.stream_id                   AS work_stream_id,
      o.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      o.status,
      o.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      NULL::INTEGER                 AS priority,
      NULL::TIMESTAMPTZ             AS received_at
    FROM __SCHEMA__.wh_outbox o
    WHERE o.instance_id = p_instance_id
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL  -- 115: pending singles; the index predicate
      AND o.lease_expiry > v_now
      AND o.published_at IS NULL  -- skip debug-mode forensic rows (production never sets this, row is deleted)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
    ORDER BY o.created_at, o.message_id
    LIMIT p_max_streams;

    -- v0.661: track this category's RETURN QUERY rowcount so the drain-mode
    -- hint at function end can be derived from ROW_COUNT instead of a fresh
    -- COUNT(*) scan. See drain-mode hint block below.
    GET DIAGNOSTICS v_outbox_rows = ROW_COUNT;

    -- Return inbox work owned by this instance: one row per lane a held stream appears in, a batch
    -- of them.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_inbox_held_lanes one index-only probe per stream, lane by lane in the
    -- order the batch is ordered: most urgent bucket first (150), commands before events inside a
    -- bucket (145). Inside a lane the fresh and retried classes are walked side by side (126), each
    -- able to take the whole batch, because the share must not hold a slot empty when the other
    -- class is: what apportions them is the interleave below, not the walks. A lane's walk starts at
    -- a stream id drawn per poll and wraps once, so a lane larger than the batch does not enumerate
    -- the same streams on every poll; a lane no larger than the batch is enumerated whole. One step
    -- lands on one stream, so a lane costs exactly the streams it enumerates.
    --
    -- One row stands for a held stream in a lane: the oldest row the instance holds of it there,
    -- carrying that row's number, arrival and attempts. A stream whose rows span lanes is returned
    -- once per lane, exactly as before, and the batch hooks fold those rows to the stream's most
    -- urgent number and oldest arrival (ClaimedInboxStreamFolder), unchanged. What is no longer
    -- returned is a stream's further rows inside one lane: the drain consumes stream ids and pulls a
    -- stream's rows on demand (fetch_inbox_batch), so they carried nothing a caller read, and ranking
    -- them is what cost the poll. A stream met in both classes of a lane stands as its retried row,
    -- which is its head -- an attempt is charged at the head and everything older is processed, so a
    -- stream with a retried row has a retried head, and stream FIFO means the fresh rows behind it
    -- cannot dispatch anyway (126). The previous shape ranked every held row with three window
    -- functions on every poll and fetched every held row's heap page to do it, and a busy instance
    -- holds thousands of rows and polls several times a second.
    --
    -- GREATEST drops a NULL, so a NULL batch leaves nothing to fill and the re-offer returns nothing.
    -- Nothing passes NULL (the parameter defaults to 1000 and the coordinators pass an integer), and
    -- returning nothing is the safe reading of "no batch size": the previous LIMIT NULL meant no
    -- bound at all, which is the failure this migration exists to stop.
    v_remaining := GREATEST(p_max_streams, 0);
    <<inbox_lanes>>
    FOR v_bucket IN 0..2 LOOP
      FOREACH v_is_event IN ARRAY ARRAY[false, true] LOOP
        EXIT inbox_lanes WHEN v_remaining <= 0;
        v_start := gen_random_uuid();
        RETURN QUERY
        WITH RECURSIVE lanes AS (
          -- Two walks side by side, one per class, seeded at the drawn point. A seed is a bound and
          -- not a candidate.
          SELECT c.fresh_lane, v_start AS stream_id, false AS wrapped, true AS is_seed, 0 AS steps,
                 NULL::INTEGER AS priority, NULL::UUID AS message_id, NULL::TIMESTAMPTZ AS received_at,
                 NULL::INTEGER AS attempts, NULL::INTEGER AS partition_number, NULL::INTEGER AS status
          FROM (VALUES (true), (false)) AS c(fresh_lane)
          UNION ALL
          SELECT h.fresh_lane, n.stream_id, n.wrapped, false, h.steps + 1,
                 n.priority, n.message_id, n.received_at, n.attempts, n.partition_number, n.status
          FROM lanes h
          CROSS JOIN LATERAL (
            -- The lane's next stream after the current one, up to the drawn point once wrapped. The
            -- index is keyed (holder, bucket, kind, class, stream, arrival, id) and covers every
            -- column below, so this is one index-only probe: it lands on the stream's oldest row in
            -- the lane and the next stream is one probe away. The upper bound is written as a CASE
            -- rather than an OR so it stays an index condition; as a filter the walk would read past
            -- the drawn point instead of stopping at it.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, h.wrapped AS wrapped
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND i.stream_id > h.stream_id
               AND i.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            UNION ALL
            -- ...or, past the lane's last stream, its first one: the walk wraps, once.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, true
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND NOT h.wrapped
               AND i.stream_id <= v_start
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            LIMIT 1
          ) n
          -- One step, one stream: each class may take the whole batch, and the interleave apportions.
          WHERE h.steps < v_remaining
        ),
        inbox_candidates AS (
          -- A stream met in both classes stands as its retried row: that row is its head (126).
          SELECT DISTINCT ON (l.stream_id)
                 l.stream_id, l.priority, l.message_id, l.received_at, l.attempts,
                 l.partition_number, l.status
          FROM lanes l
          WHERE NOT l.is_seed
          ORDER BY l.stream_id, l.fresh_lane
        ),
        ranked_inbox AS (
          SELECT c.stream_id, c.priority, c.message_id, c.received_at, c.attempts,
                 c.partition_number, c.status,
                 (c.attempts = 0) AS is_fresh,
                 ROW_NUMBER() OVER (PARTITION BY (c.attempts = 0) ORDER BY c.received_at, c.message_id) AS class_rank
          FROM inbox_candidates c
        ),
        ordered_inbox AS (
          SELECT ri.stream_id, ri.priority, ri.message_id, ri.received_at, ri.attempts,
                 ri.partition_number, ri.status
          FROM ranked_inbox ri
          ORDER BY
            -- 126: fresh-head streams receive p_fresh_share of the batch, retried-head streams the
            -- remainder, each class in arrival order; an empty class hands its share to the other,
            -- because the key only competes candidates that exist.
            CASE WHEN ri.is_fresh
                 THEN (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(LEAST(p_fresh_share, 1.0), 0.000001)
                 ELSE (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(1.0 - LEAST(p_fresh_share, 1.0), 0.000001)
            END,
            ri.received_at,
            ri.message_id
          LIMIT v_remaining
        )
        -- Per-stream-drain projection (Phase H step 5d): inbox follows outbox into stream-ids-only.
        -- InboxDrainWorker reads stream_ids off IInboxDrainChannel and pulls payloads on demand
        -- via fetch_inbox_batch. Body columns are NULL -- keeps claim_work's bytes-on-the-wire
        -- proportional to active stream count.
        SELECT
          c_source_inbox                AS source,
          oi.message_id                 AS work_id,
          oi.stream_id                  AS work_stream_id,
          oi.partition_number,
          NULL::VARCHAR(200)            AS destination,
          NULL::VARCHAR(500)            AS message_type,
          NULL::VARCHAR(500)            AS envelope_type,
          NULL::TEXT                    AS message_data,
          NULL::JSONB                   AS metadata,
          oi.status,
          oi.attempts,
          false                         AS is_newly_stored,
          false                         AS is_orphaned,
          NULL::VARCHAR(200)            AS perspective_name,
          oi.priority                   AS priority,
          oi.received_at                AS received_at
        FROM ordered_inbox oi;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_remaining := v_remaining - v_rows;
      END LOOP;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_inbox_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- Return receptor work owned by this instance.
    -- Receptor work uses `id` as the work_id (not message_id) and `completed_at` as the "done" marker.
    -- Most fields are NULL, receptors carry their state in dedicated columns the worker reads
    -- directly via the work_id; the row here just signals "this receptor needs attention".
    RETURN QUERY
    SELECT
      'receptor'::VARCHAR(20)       AS source,
      rp.id                         AS work_id,
      rp.stream_id                  AS work_stream_id,
      rp.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      rp.status::INTEGER,
      rp.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      NULL::INTEGER                 AS priority,
      NULL::TIMESTAMPTZ             AS received_at
    FROM __SCHEMA__.wh_receptor_processing rp
    WHERE rp.instance_id = p_instance_id
      AND rp.lease_expiry > v_now
      AND rp.completed_at IS NULL
    LIMIT p_max_streams;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_receptor_rows = ROW_COUNT;

    -- Return perspective work as one row per distinct stream owned by this instance, most urgent
    -- bucket first, oldest event first within a bucket, a batch of streams.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_perspective_held_lanes one index-only probe per stream, bucket by bucket,
    -- most urgent first; the first entry of a stream in its bucket is its oldest held event there,
    -- which is what orders the streams within the bucket. The walk starts at a stream id drawn per
    -- poll and wraps once, so a bucket larger than the batch does not enumerate the same streams on
    -- every poll, and one no larger than the batch is enumerated whole. One step lands on one stream,
    -- so a bucket costs exactly the streams it enumerates. The previous shape aggregated every held
    -- event per poll to put streams with a hundred or fewer pending events ahead of larger ones; that
    -- tier guarded a batch of rows against one large stream, and the drain has been per stream with an
    -- unbounded channel since Phase H, so a large stream no longer displaces small ones.
    v_remaining := GREATEST(p_max_streams, 0);
    FOR v_bucket IN 0..2 LOOP
      EXIT WHEN v_remaining <= 0;
      v_start := gen_random_uuid();
      RETURN QUERY
      WITH RECURSIVE lane AS (
        -- Seeded at the drawn point; the seed is not a candidate.
        SELECT v_start AS stream_id, NULL::UUID AS event_id, false AS wrapped, true AS is_seed, 0 AS steps
        UNION ALL
        SELECT n.stream_id, n.event_id, n.wrapped, false, h.steps + 1
        FROM lane h
        CROSS JOIN LATERAL (
          -- The bucket's next stream after the current one, up to the drawn point once wrapped...
          (SELECT pe.stream_id, pe.event_id, h.wrapped AS wrapped
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND pe.stream_id > h.stream_id
             AND pe.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          UNION ALL
          -- ...or, past the bucket's last stream, its first one: the walk wraps.
          (SELECT pe.stream_id, pe.event_id, true
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND NOT h.wrapped
             AND pe.stream_id <= v_start
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          LIMIT 1
        ) n
        WHERE h.steps < v_remaining
      )
      SELECT
        'perspective_stream'::VARCHAR(20) AS source,
        NULL::UUID                        AS work_id,
        l.stream_id                       AS work_stream_id,
        NULL::INTEGER                     AS partition_number,
        NULL::VARCHAR(200)                AS destination,
        NULL::VARCHAR(500)                AS message_type,
        NULL::VARCHAR(500)                AS envelope_type,
        NULL::TEXT                        AS message_data,
        NULL::JSONB                       AS metadata,
        0::INTEGER                        AS status,
        0::INTEGER                        AS attempts,
        false                             AS is_newly_stored,
        false                             AS is_orphaned,
        NULL::VARCHAR(200)                AS perspective_name,
        NULL::INTEGER                     AS priority,
        NULL::TIMESTAMPTZ                 AS received_at
      FROM lane l
      WHERE NOT l.is_seed
      ORDER BY l.event_id, l.stream_id
      LIMIT v_remaining;

      GET DIAGNOSTICS v_rows = ROW_COUNT;
      v_remaining := v_remaining - v_rows;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_perspective_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- 130 doorbell debounce: finding work stamps this instance's watermark, the signal
    -- producers use to suppress redundant notifies while this drainer is awake. Rides
    -- inside the claim (zero extra round trips) and skips the empty case so the
    -- empty-call short-circuit's ~1 ms idle floor is untouched.
    -- 140: the stamp never waits (issue #699). The store's debounce touches the same rows inside
    -- its own transaction; under a burst the two met in opposite orders with the inbox row locks
    -- and deadlocked. A watermark row another session holds is skipped this tick: the stamp is a
    -- freshness hint and the next tick re-stamps it. Rows that do not exist yet are created; rows
    -- that exist and are free are stamped in place.
    WITH kinds AS (
      SELECT k.kind
      FROM (VALUES (c_source_outbox), (c_source_inbox), (__CATEGORY_PERSPECTIVE__)) AS k(kind)
    WHERE (k.kind = c_source_outbox AND v_outbox_rows > 0)
       OR (k.kind = c_source_inbox AND (v_inbox_rows > 0 OR v_receptor_rows > 0))
       -- 133: the perspective watermark must reflect DRAINABLE progress, not merely a
       -- claimed Stored-but-fence-held row. claim_work returns a perspective_stream row for
       -- any leased unprocessed perspective_event, but a row whose underlying event is still
       -- unstamped (commit_sequence IS NULL, held by the per-database ordering fence) cannot
       -- be surfaced by the fetch gate, the drainer spins with no progress. Arming the
       -- watermark for it lets the doorbell debounce (130/131) suppress the fence-clearing
       -- stamp's make-up ring, stranding visibility on the adaptive poll cap (issue #677).
       -- The EXISTS runs at most once: the k.kind guard short-circuits it away for the
       -- outbox/inbox VALUES rows.
       -- 158: the question is asked of a batch of the rows leased longest, not of every held row.
       -- Over all of them it was priced by the holdings whenever the fence held (#677 is exactly
       -- that state), joining every leased row to the event store per poll. A held row whose event is
       -- stamped is found among the oldest leases if it is found at all; when none of them is, the
       -- watermark stays unarmed and the make-up ring goes through, which is the safe direction.
       OR (k.kind = __CATEGORY_PERSPECTIVE__ AND v_perspective_rows > 0 AND EXISTS (
             SELECT 1
             FROM (
               SELECT pe.event_id
               FROM __SCHEMA__.wh_perspective_events pe
               WHERE pe.instance_id = p_instance_id
                 AND pe.lease_expiry > v_now
                 AND pe.processed_at IS NULL
               ORDER BY pe.lease_expiry
               LIMIT GREATEST(p_max_streams, 1)
             ) held
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = held.event_id
             WHERE es.commit_sequence IS NOT NULL))
    ),
    lockable AS (
      SELECT ns.instance_id, ns.payload_kind
      FROM __SCHEMA__.wh_notify_state ns
      JOIN kinds ON kinds.kind = ns.payload_kind
      WHERE ns.instance_id = p_instance_id
      FOR UPDATE OF ns SKIP LOCKED
    ),
    stamped AS (
      UPDATE __SCHEMA__.wh_notify_state ns
      SET last_work_at = NOW()
      FROM lockable
      WHERE ns.instance_id = lockable.instance_id AND ns.payload_kind = lockable.payload_kind
      RETURNING ns.payload_kind
    )
    INSERT INTO __SCHEMA__.wh_notify_state (instance_id, payload_kind, last_work_at)
    SELECT p_instance_id, kinds.kind, NOW()
    FROM kinds
    WHERE NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_notify_state ns2
      WHERE ns2.instance_id = p_instance_id AND ns2.payload_kind = kinds.kind)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    -- Drain-mode hint: if any of the four return categories filled its LIMIT
    -- (rows == p_max_streams), there's likely more eligible work for this
    -- instance, RAISE NOTICE so the C# claim worker skips its wait and
    -- re-polls immediately. Survives pgbouncer (protocol message, not a
    -- session-state thing).
    --
    -- v0.661 forensic (gate.hold_duration_ms histogram during a consumer's
    -- draft-job import): the prior implementation ran four separate
    -- COUNT(*) queries here (one per category, plus a COUNT(DISTINCT
    -- stream_id) on wh_perspective_events). Under import load with millions
    -- of leased rows per instance, those counts dominated claim_work hold
    -- time, ClaimWorkAsync at p99 5031 ms / avg 128 ms. We don't need
    -- exact counts; we only need to know whether ANY category filled its
    -- LIMIT. ROW_COUNT after each RETURN QUERY gives us that for free.
    IF v_outbox_rows = p_max_streams
       OR v_inbox_rows = p_max_streams
       OR v_receptor_rows = p_max_streams
       OR v_perspective_rows = p_max_streams THEN
      RAISE NOTICE 'whizbang.has_more=true';
    END IF;
  END;

  RETURN;
END;
-- 157: custom plans for the poll and everything it calls. The queue tables are empty between loads,
-- and a session that polled while they were empty kept generic plans made for empty tables; once
-- the tables filled those plans scanned them whole, nested, on every poll, until the next analyze
-- invalidated them. Measured: a poll that takes well under a second with fresh plans did not finish
-- inside the command timeout with the empty-table plans. Planning per call costs milliseconds.
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;


-- ===========================================================================================
-- BATCH FOUR: the five functions the re-derived enumeration found.
-- ===========================================================================================
-- Six were found; one needs no change. commit_handler_batch_bulk matched the search only through a
-- COMMENT mentioning wh_inbox, and has no statement against the table. Recorded rather than silently
-- skipped, because "matched a text search" and "touches the table" are different claims and this
-- work has now conflated them six times.


-- <docs>operations/infrastructure/partitioning</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorDeepPathTests.cs:RecomputePartitionNumbersAsync_MismatchedRows_RecomputesAllThreeTablesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.cs:NoFunctionLocksTheWorkTablesOutOfCanonicalOrderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.recompute_partition_numbers(
  p_partition_count INTEGER
) RETURNS TABLE(
  table_name TEXT,
  rows_recomputed BIGINT
) AS $$
DECLARE
  v_inbox_count BIGINT;
  v_outbox_count BIGINT;
  v_active_streams_count BIGINT;
BEGIN
  IF p_partition_count IS NULL OR p_partition_count <= 0 THEN
    RAISE EXCEPTION 'recompute_partition_numbers: p_partition_count must be a positive integer (got %)', p_partition_count;
  END IF;

  -- wh_outbox goes FIRST, and the order is the whole point rather than a preference. This
  -- function is the only multi-table writer that took the work tables in a different order from
  -- the rest: it locked wh_inbox_state before wh_outbox, while renew_leases, deregister_instance
  -- and cleanup_stale_instances all lock wh_outbox before wh_inbox_state. Two transactions that
  -- take the same two tables in opposite orders deadlock the moment their row sets overlap, and
  -- these do overlap at exactly the worst moment: a partition recompute is triggered by the same
  -- scale event that runs a deregistration and a stale-instance sweep. The victim is reported
  -- waiting on a wh_outbox row lock, which says nothing about which pair caused it.
  --
  -- The two statements are independent -- separate tables, separate counters, and the report is
  -- built from a fixed VALUES list below -- so the order is free to be the canonical one:
  -- wh_outbox, wh_inbox, wh_inbox_state, wh_perspective_events, wh_active_streams.
  WITH updated AS (
    UPDATE __SCHEMA__.wh_outbox
    SET partition_number = __SCHEMA__.compute_partition(stream_id, p_partition_count)
    WHERE stream_id IS NOT NULL
      AND processed_at IS NULL
      AND partition_number IS DISTINCT FROM __SCHEMA__.compute_partition(stream_id, p_partition_count)
    RETURNING 1
  )
  SELECT COUNT(*) INTO v_outbox_count FROM updated;

  -- wh_inbox: only recompute rows that have a stream binding AND whose stored
  -- partition_number disagrees with the canonical value. NULL partition_number
  -- (no stream binding) is the explicitly-tolerated fallback path in claim_orphaned_inbox
  -- and must not be touched.
  WITH updated AS (
    -- 162: this is the function that makes partition_number MUTABLE, and therefore the reason it
    -- had to move rather than be copied. It looks like static routing data derived from the stream
    -- id, and it is rewritten whenever the partition count changes. Every column here is on the
    -- state table, so the update never touches the message row.
    UPDATE __SCHEMA__.wh_inbox_state
    SET partition_number = __SCHEMA__.compute_partition(stream_id, p_partition_count)
    WHERE stream_id IS NOT NULL
      AND processed_at IS NULL
      AND partition_number IS DISTINCT FROM __SCHEMA__.compute_partition(stream_id, p_partition_count)
    RETURNING 1
  )
  SELECT COUNT(*) INTO v_inbox_count FROM updated;

  -- wh_active_streams: refresh stale partition_numbers. Lease/ownership are untouched ,
  -- a recompute does not change who currently owns a stream, only the partition_number
  -- column used by claim_orphaned_inbox/_outbox modulo routing.
  WITH updated AS (
    UPDATE __SCHEMA__.wh_active_streams
    SET partition_number = __SCHEMA__.compute_partition(stream_id, p_partition_count)
    WHERE partition_number IS DISTINCT FROM __SCHEMA__.compute_partition(stream_id, p_partition_count)
    RETURNING 1
  )
  SELECT COUNT(*) INTO v_active_streams_count FROM updated;

  RETURN QUERY VALUES
    ('wh_inbox'::TEXT, v_inbox_count),
    ('wh_outbox'::TEXT, v_outbox_count),
    ('wh_active_streams'::TEXT, v_active_streams_count);
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorDeepPathTests.cs:CleanupCompletedStreamsAsync_NullStreamList_ReturnsZeroAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.cleanup_completed_streams(
  p_stream_ids UUID[]
) RETURNS INTEGER AS $$
DECLARE
  v_evicted INTEGER;
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  -- Single set-based DELETE: remove active_streams rows for input stream_ids that
  -- have no pending work across the three work tables. The "no pending" check filters
  -- on processed_at IS NULL (outbox, inbox, perspective_events), same shape used by
  -- claim_orphaned_*. Eviction is safe because the next event-store call for that
  -- stream re-runs the wh_active_streams UPSERT.
  WITH evicted AS (
    DELETE FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = ANY(p_stream_ids)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = a.stream_id
          AND o.processed_at IS NULL
      )
      AND NOT EXISTS (
        -- 162: stream_id is a write-once copy here and processed_at moved, so the probe reads the
        -- narrow table. This runs per candidate stream, so keeping it off the wide row matters.
        SELECT 1 FROM __SCHEMA__.wh_inbox_state ist
        WHERE ist.stream_id = a.stream_id
          AND ist.processed_at IS NULL
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = a.stream_id
          AND pe.processed_at IS NULL
      )
    RETURNING a.stream_id
  )
  SELECT COUNT(*)::INTEGER INTO v_evicted FROM evicted;

  RETURN COALESCE(v_evicted, 0);
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/work-coordinator/failure-and-recovery</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorLifecycleAndJanitorTests.cs:NotifyScheduledRetryDueAsync_DueOutboxAndInboxRows_ReturnsDistinctStreamCountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorLifecycleAndJanitorTests.cs:NotifyScheduledRetryDueAsync_NoQualifyingRows_ReturnsZeroAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.notify_scheduled_retry_due()
RETURNS TABLE(category TEXT, stream_count INTEGER) AS $$
DECLARE
  v_outbox_streams UUID[];
  v_inbox_streams UUID[];
  v_now TIMESTAMPTZ := NOW();
BEGIN
  -- Outbox rows whose scheduled_for time has elapsed and which haven't been processed.
  -- Filter to NOT NULL stream_id because notify_instance_owners can't deliver to a row
  -- with no owning stream. (Stream-less retries are handled by the regular claim poll.)
  --
  -- Multi-schema fix: tables MUST be __SCHEMA__-qualified. Unqualified FROM clauses get
  -- resolved via search_path which defaults to public, when the function is invoked
  -- from an inventory/bff schema (the InMemory + ECommerce hosts pattern) it tried to
  -- read public.wh_outbox / public.wh_inbox, which don't exist; every 10 s cycle threw
  -- 42P01 and the surrounding noise stalled perspective discovery.
  SELECT ARRAY_AGG(DISTINCT stream_id) INTO v_outbox_streams
  FROM __SCHEMA__.wh_outbox
  WHERE processed_at IS NULL
    AND scheduled_for IS NOT NULL
    AND scheduled_for <= v_now
    AND stream_id IS NOT NULL;

  -- Inbox rows, same shape.
  SELECT ARRAY_AGG(DISTINCT stream_id) INTO v_inbox_streams
  -- 162: processed_at and scheduled_for moved and stream_id is a write-once copy, so every term is
  -- on the state table.
  FROM __SCHEMA__.wh_inbox_state
  WHERE processed_at IS NULL
    AND scheduled_for IS NOT NULL
    AND scheduled_for <= v_now
    AND stream_id IS NOT NULL;

  -- Note on wh_perspective_events: the current schema doesn't carry a scheduled_for column.
  -- Retries on perspective events ride the claim_orphaned_perspective_events lease-expiry
  -- path, which is NOTIFY-eligible via the regular work signals. If scheduled_for is added
  -- to wh_perspective_events in the future, add a third SELECT/notify call here.

  IF v_outbox_streams IS NOT NULL AND array_length(v_outbox_streams, 1) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_OUTBOX__, v_outbox_streams);
    category := __CATEGORY_OUTBOX__;
    stream_count := array_length(v_outbox_streams, 1);
    RETURN NEXT;
  END IF;

  IF v_inbox_streams IS NOT NULL AND array_length(v_inbox_streams, 1) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_INBOX__, v_inbox_streams);
    category := __CATEGORY_INBOX__;
    stream_count := array_length(v_inbox_streams, 1);
    RETURN NEXT;
  END IF;
END;
$$ LANGUAGE plpgsql;

-- <docs>operations/workers/stuck-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreFindStuckRowsTests.cs:FindStuckInboxRows_PostgresBacking_DelegatesToSqlFunctionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.find_stuck_inbox_rows(
  p_max_attempts INTEGER,
  p_limit INTEGER
) RETURNS TABLE (
  message_id UUID,
  message_type TEXT,
  stream_id UUID,
  attempts INTEGER,
  claimed_since TIMESTAMPTZ
) LANGUAGE SQL STABLE AS $$
  -- 162: message_type is a property of the message and stays; attempts and processed_at moved. The
  -- STATE table drives the join, so selection and ordering happen on the narrow table and the wide
  -- row is fetched only for the rows that come back, which p_limit already bounds.
  SELECT ist.message_id,
         i.message_type::TEXT,
         ist.stream_id,
         ist.attempts,
         ist.received_at
  FROM __SCHEMA__.wh_inbox_state ist
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
  WHERE ist.attempts > p_max_attempts
    AND ist.processed_at IS NULL
  ORDER BY ist.attempts DESC, ist.received_at ASC
  LIMIT p_limit;
$$;

-- <docs>messaging/dead-letters</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PriorityOnTheWireSqlTests.cs:RecoverDeadLetter_InboxRow_ReentersAsBackgroundAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DeadLetterRecoverySqlTests.cs:RecoverDeadLetter_InboxRowWithEmptyStreamId_NormalizesToNullAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.recover_dead_letter(
  p_dead_letter_id UUID
) RETURNS BOOLEAN AS $$
DECLARE
  v_source_table   TEXT;
  v_source_id      UUID;
  v_redelivered    BIGINT;
  v_stream_id      UUID;
  v_message_type   TEXT;
  v_destination    TEXT;
  v_perspective    TEXT;
  v_envelope       JSONB;
  v_metadata       JSONB;
  v_event_data     JSONB;
  v_partition      INTEGER;
BEGIN
  -- Atomically claim the row by transitioning to Recovering AND fetch its forensic
  -- payload. If another worker raced us OR the row is already terminal, the UPDATE
  -- affects zero rows and we return false.
  WITH claimed AS (
    UPDATE __SCHEMA__.wh_dead_letters
    SET recovery_status = 1,                  -- Recovering
        recovery_attempts = recovery_attempts + 1,
        last_recovery_at = NOW()
    WHERE dead_letter_id = p_dead_letter_id
      AND recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
      AND recovered_at IS NULL
    RETURNING source_table, source_id, stream_id, message_type, destination, perspective_name, envelope, metadata
  )
  SELECT c.source_table, c.source_id, c.stream_id, c.message_type, c.destination, c.perspective_name, c.envelope, c.metadata
  INTO v_source_table, v_source_id, v_stream_id, v_message_type, v_destination, v_perspective, v_envelope, v_metadata
  FROM claimed c;

  IF v_source_table IS NULL THEN
    RETURN FALSE;  -- already claimed by another worker or already terminal
  END IF;

  -- v0.657 slice 4: DLQ replay self-repair. If the DLQ row preserves a
  -- Guid.Empty stream_id (a pattern observed in production, producer bug from before the
  -- v0.657 storage-time Reject guard shipped), normalize to NULL on the
  -- INSERT back into the source table. Otherwise the recovered row immediately
  -- re-sticks under the same silent-stuck pattern that DLQ'd it in the first
  -- place: stream_id=Empty bypasses the NULL-only `??` coalesce in the C#
  -- coordinator (pre-v0.657) and the slice-3 coordinator backstop sees Empty
  -- as "no real stream identity" → WorkId fallback. NULL is the documented
  -- singleton-stream marker; that's the value we want on recovery.
  IF v_stream_id = __EMPTY_UUID__::uuid THEN
    v_stream_id := NULL;
  END IF;

  -- Extract the original event_data from the envelope JSONB.
  v_event_data := v_envelope -> 'event_data';
  v_partition := CASE WHEN v_stream_id IS NULL THEN 0 ELSE 0 END;  -- partition recomputed on store_*_messages path; fixed to 0 here is fine because claim_orphaned_* recomputes via wh_active_streams

  -- Re-emit into the appropriate source table with attempts=0.
  IF v_source_table = 'wh_outbox' THEN
    INSERT INTO __SCHEMA__.wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number, priority)
    VALUES (v_source_id, v_destination, v_message_type, 'recovered', v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition, __PRIORITY_BACKGROUND__)
    ON CONFLICT (message_id) DO NOTHING;  -- already re-published; idempotent
  ELSIF v_source_table = 'wh_inbox' THEN
    INSERT INTO __SCHEMA__.wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, priority)
    VALUES (v_source_id, COALESCE(v_perspective, 'recovered'), v_message_type, v_event_data, v_metadata, NOW(), v_stream_id, __PRIORITY_BACKGROUND__)
    ON CONFLICT (message_id) DO NOTHING;
    -- 125: count the re-delivery this recovery just caused.
    --
    -- 121 replaced count-based poison detection with an observation counter the framework keeps
    -- itself, because a broker delivery counter cannot bound a redelivery loop. store_inbox_messages
    -- increments wh_message_deduplication.observation_count on every arrival and
    -- PoisonMessageDetector reads that count. Recovery re-delivers by INSERTing straight into
    -- wh_inbox, which bypasses that path, so before this every recovery-driven arrival was
    -- invisible: a message could be recovered without limit because no pass was ever observed and
    -- attempts is reset to 0 on the way in.
    --
    -- Charged only when the INSERT actually inserted. ON CONFLICT DO NOTHING means a double-recovery
    -- race delivered nothing, and charging for a delivery that did not happen would push a healthy
    -- message toward quarantine.
    -- 162: this GET DIAGNOSTICS reads ROW_COUNT of the statement IMMEDIATELY above it, so nothing
    -- may be inserted between them. An earlier draft put the work-state INSERT here and the counter
    -- silently began measuring that instead, which zeroed the redelivery charge and made recovery
    -- invisible to poison detection. The state row is created below, after the count is taken.
    GET DIAGNOSTICS v_redelivered = ROW_COUNT;
    IF v_redelivered > 0 THEN
      INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
        (message_id, first_seen_at, observation_count)
      VALUES (v_source_id, NOW(), 1)
      ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
        SET observation_count = dedup.observation_count + 1;
    END IF;
    -- status, attempts and partition_number are work state, so recovery constructs the state row as
    -- well as the message. A recovered message with no state row could never be claimed and would
    -- fail silently, which is the shape this pair exists to prevent. attempts resets to 0 on the way
    -- in, which is the point of recovery: the budget is deliberately refunded.
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      status, processed_at, instance_id, lease_expiry, attempts)
    VALUES (v_source_id, v_stream_id, NOW(), v_partition, __PRIORITY_BACKGROUND__, FALSE,
            0, NULL, NULL, NULL, 0)
    ON CONFLICT (message_id) DO NOTHING;

  ELSIF v_source_table = 'wh_perspective_events' THEN
    -- Perspective recovery uses the event_id snapshot to recreate the work row.
    INSERT INTO __SCHEMA__.wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    VALUES (v_source_id, v_stream_id, v_perspective, (v_envelope ->> 'event_id')::UUID, v_partition, 0, 0, NOW(), __PRIORITY_BACKGROUND__)
    ON CONFLICT (event_work_id) DO NOTHING;
  ELSIF v_source_table = 'broker' THEN
    -- Broker-imported rows (wh_import_dead_letter, migration 119) re-enter through the inbox
    -- front door: normal dispatch, composite fan-out, and the internal max-attempts ladder all
    -- apply unchanged. A row that still cannot be processed on the current build parks again in
    -- wh_dead_letters via move_to_dead_letters, visible, fingerprinted, attempt-accounted ,
    -- instead of orbiting the broker's opaque DLQ.
    INSERT INTO __SCHEMA__.wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, priority)
    VALUES (v_source_id, 'broker-recovered', v_message_type, v_event_data, v_metadata, NOW(), v_stream_id, __PRIORITY_BACKGROUND__)
    ON CONFLICT (message_id) DO NOTHING;
    -- 125: count the re-delivery this recovery just caused.
    --
    -- 121 replaced count-based poison detection with an observation counter the framework keeps
    -- itself, because a broker delivery counter cannot bound a redelivery loop. store_inbox_messages
    -- increments wh_message_deduplication.observation_count on every arrival and
    -- PoisonMessageDetector reads that count. Recovery re-delivers by INSERTing straight into
    -- wh_inbox, which bypasses that path, so before this every recovery-driven arrival was
    -- invisible: a message could be recovered without limit because no pass was ever observed and
    -- attempts is reset to 0 on the way in.
    --
    -- Charged only when the INSERT actually inserted. ON CONFLICT DO NOTHING means a double-recovery
    -- race delivered nothing, and charging for a delivery that did not happen would push a healthy
    -- message toward quarantine.
    -- 162: this GET DIAGNOSTICS reads ROW_COUNT of the statement IMMEDIATELY above it, so nothing
    -- may be inserted between them. An earlier draft put the work-state INSERT here and the counter
    -- silently began measuring that instead, which zeroed the redelivery charge and made recovery
    -- invisible to poison detection. The state row is created below, after the count is taken.
    GET DIAGNOSTICS v_redelivered = ROW_COUNT;
    IF v_redelivered > 0 THEN
      INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
        (message_id, first_seen_at, observation_count)
      VALUES (v_source_id, NOW(), 1)
      ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
        SET observation_count = dedup.observation_count + 1;
    END IF;
    -- status, attempts and partition_number are work state, so recovery constructs the state row as
    -- well as the message. A recovered message with no state row could never be claimed and would
    -- fail silently, which is the shape this pair exists to prevent. attempts resets to 0 on the way
    -- in, which is the point of recovery: the budget is deliberately refunded.
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      status, processed_at, instance_id, lease_expiry, attempts)
    VALUES (v_source_id, v_stream_id, NOW(), v_partition, __PRIORITY_BACKGROUND__, FALSE,
            0, NULL, NULL, NULL, 0)
    ON CONFLICT (message_id) DO NOTHING;

  ELSE
    -- Unknown source table, leave as Recovering for an operator to investigate.
    RAISE WARNING 'recover_dead_letter: unsupported source table %', v_source_table;
    RETURN FALSE;
  END IF;

  -- Mark Recovered.
  UPDATE __SCHEMA__.wh_dead_letters
  SET recovery_status = 3,
      recovered_at = NOW(),
      retried_on_generations =
        CASE WHEN generation = ANY(retried_on_generations) THEN retried_on_generations
             ELSE array_append(retried_on_generations, generation) END
  WHERE dead_letter_id = p_dead_letter_id;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;