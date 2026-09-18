-- Migration: 162_InboxLeaseSideTable.sql
-- Date: 2026-09-17
-- Description: The inbox lease moves into a narrow table of its own, so a claim stops paying for
--              every index on a wide message row.
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
--              TWENTY of the twenty-four indexes in the fixture name one of the EIGHT mutable
--              columns. They belong to claiming and they leave with it, which is the real argument
--              for this change: wh_inbox drops from twenty-four indexes to FOUR, so EVERY write to
--              it gets cheaper, not only the claim.
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
-- Objects: wh_inbox_lease, wh_inbox (drops instance_id, lease_expiry, attempts, processed_at, scheduled_for, failure_reason, error, chain_emitted_at)

-- See "THE LOCK ON THE LINE BELOW IS NOT REDUNDANT" above before touching this statement.
LOCK TABLE __SCHEMA__.wh_inbox IN ACCESS EXCLUSIVE MODE;

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_inbox_lease (
  -- The join key, and the only column shared with wh_inbox.
  message_id       UUID PRIMARY KEY,
  -- Everything below is a column claim_orphaned_inbox reads or writes. The set is DERIVED from the
  -- function rather than chosen: a column the claim path needs that stays behind turns the
  -- per-stream ordering gate into a cross-table join, which is the one thing that would make this
  -- slower rather than faster.
  -- IMMUTABLE COPIES. These stay on wh_inbox as well, and that is safe precisely because nothing
  -- rewrites them after the row is inserted: two copies of a value that never changes cannot
  -- diverge. They are copied here so the per-stream ordering gate and the lane picks read one narrow
  -- table instead of joining back to a wide one, which is the whole performance case.
  stream_id        UUID,
  received_at      TIMESTAMPTZ NOT NULL,
  partition_number INTEGER,
  priority         INTEGER     NOT NULL DEFAULT 100,
  is_event         BOOLEAN     NOT NULL DEFAULT FALSE,
  -- THE EIGHT MUTABLE COLUMNS. These MOVE: they are dropped from wh_inbox, so each lives in exactly
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
  failure_reason   INTEGER,
  error            TEXT,
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
  CONSTRAINT fk_inbox_lease_message FOREIGN KEY (message_id)
    REFERENCES __SCHEMA__.wh_inbox (message_id) ON DELETE CASCADE
);

COMMENT ON TABLE __SCHEMA__.wh_inbox_lease IS
  'Claim state for wh_inbox rows (162), split out so a claim does not rewrite a wide, heavily '
  'indexed message row. instance_id and lease_expiry each appear in twelve of wh_inbox''s indexes, '
  'so a claim can never be a HOT update and pays full index maintenance on every write; measured '
  'HOT share across six million updates in a deployed fleet was under two tenths of one percent. '
  'One row per wh_inbox row, created and deleted with it.';

INSERT INTO __SCHEMA__.wh_inbox_lease (
  message_id, stream_id, received_at, partition_number, priority, is_event,
  processed_at, instance_id, lease_expiry, attempts, scheduled_for, failure_reason, error,
  chain_emitted_at)
SELECT
  message_id, stream_id, received_at, partition_number, priority, is_event,
  processed_at, instance_id, lease_expiry, attempts, scheduled_for, failure_reason, error,
  chain_emitted_at
FROM __SCHEMA__.wh_inbox
ON CONFLICT (message_id) DO NOTHING;

-- The lanes the claim picks from. Partial on processed_at IS NULL so they track pending work rather
-- than settled history, and split by ownership so neither lane reads the other's rows.
CREATE INDEX IF NOT EXISTS idx_inbox_lease_unowned
  ON __SCHEMA__.wh_inbox_lease (priority, received_at, message_id)
  WHERE processed_at IS NULL AND instance_id IS NULL;
CREATE INDEX IF NOT EXISTS idx_inbox_lease_expired
  ON __SCHEMA__.wh_inbox_lease (priority, lease_expiry, received_at)
  WHERE processed_at IS NULL AND instance_id IS NOT NULL;
-- The per-stream ordering gate walks a stream's pending rows in arrival order. Every column it reads
-- is on this table, so the gate stays a single-table query rather than becoming a join.
-- The emit chain's driving read, now entirely local to this table. Mirrors
-- idx_inbox_chain_pending, which cannot survive on wh_inbox because it keys on instance_id and
-- predicates on processed_at, both of which move.
CREATE INDEX IF NOT EXISTS idx_inbox_lease_chain_pending
  ON __SCHEMA__.wh_inbox_lease (instance_id)
  WHERE processed_at IS NULL AND is_event = TRUE AND stream_id IS NOT NULL
    AND chain_emitted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_inbox_lease_stream_order
  ON __SCHEMA__.wh_inbox_lease (stream_id, received_at, message_id)
  WHERE processed_at IS NULL;

-- ===========================================================================================
-- BUILD SCAFFOLD. TEMPORARY. REMOVED IN THE SAME COMMIT THAT ADDS THE COLUMN DROPS.
-- ===========================================================================================
-- These triggers keep wh_inbox's seven mutable columns and wh_inbox_lease in step while the
-- sixteen functions are being rewritten one batch at a time. Without them a rewritten function
-- reading the lease table sits beside an un-rewritten one still writing wh_inbox, the two
-- representations disagree, and the existing suite fails for reasons that say nothing about the
-- rewrite being verified.
--
-- This is the expand/migrate/contract mechanism used as a DEVELOPMENT scaffold, not as a shipping
-- strategy, and the difference is the whole point. Staging was rejected because a consumer would
-- run a version where the old columns exist but are ignored, so a claim writing an ignored column
-- believes it holds a lease it does not, and dispatches work a second claimer will also take. No
-- consumer ever sees these triggers: they and the column drops are one commit.
--
-- Created AFTER the backfill above, deliberately, so the backfill does not fire them once per row.
-- pg_trigger_depth() > 1 means we were fired by the other trigger rather than by a statement, which
-- is how the two directions avoid recursing into each other.
CREATE OR REPLACE FUNCTION __SCHEMA__._scaffold_sync_inbox_to_lease() RETURNS TRIGGER AS $$
BEGIN
  IF pg_trigger_depth() > 1 THEN RETURN NULL; END IF;
  INSERT INTO __SCHEMA__.wh_inbox_lease (
    message_id, stream_id, received_at, partition_number, priority, is_event,
    processed_at, instance_id, lease_expiry, attempts, scheduled_for, failure_reason, error,
    chain_emitted_at)
  VALUES (
    NEW.message_id, NEW.stream_id, NEW.received_at, NEW.partition_number, NEW.priority,
    NEW.is_event, NEW.processed_at, NEW.instance_id, NEW.lease_expiry, NEW.attempts,
    NEW.scheduled_for, NEW.failure_reason, NEW.error, NEW.chain_emitted_at)
  ON CONFLICT (message_id) DO UPDATE SET
    processed_at = EXCLUDED.processed_at, instance_id = EXCLUDED.instance_id,
    lease_expiry = EXCLUDED.lease_expiry, attempts = EXCLUDED.attempts,
    scheduled_for = EXCLUDED.scheduled_for, failure_reason = EXCLUDED.failure_reason,
    error = EXCLUDED.error, chain_emitted_at = EXCLUDED.chain_emitted_at;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION __SCHEMA__._scaffold_sync_lease_to_inbox() RETURNS TRIGGER AS $$
BEGIN
  IF pg_trigger_depth() > 1 THEN RETURN NULL; END IF;
  UPDATE __SCHEMA__.wh_inbox SET
    processed_at = NEW.processed_at, instance_id = NEW.instance_id,
    lease_expiry = NEW.lease_expiry, attempts = NEW.attempts,
    scheduled_for = NEW.scheduled_for, failure_reason = NEW.failure_reason, error = NEW.error,
    chain_emitted_at = NEW.chain_emitted_at
  WHERE message_id = NEW.message_id;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_scaffold_inbox_to_lease ON __SCHEMA__.wh_inbox;
CREATE TRIGGER trg_scaffold_inbox_to_lease
  AFTER INSERT OR UPDATE ON __SCHEMA__.wh_inbox
  FOR EACH ROW EXECUTE FUNCTION __SCHEMA__._scaffold_sync_inbox_to_lease();

-- UPDATE only on this side: the row is created by the inbox insert above, so an INSERT trigger here
-- would only ever duplicate work.
DROP TRIGGER IF EXISTS trg_scaffold_lease_to_inbox ON __SCHEMA__.wh_inbox_lease;
CREATE TRIGGER trg_scaffold_lease_to_inbox
  AFTER UPDATE ON __SCHEMA__.wh_inbox_lease
  FOR EACH ROW EXECUTE FUNCTION __SCHEMA__._scaffold_sync_lease_to_inbox();

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
    -- 162: the inbox's claim state lives in wh_inbox_lease. Reading it here also means this count
    -- no longer touches the wide message row at all.
    (SELECT count(*) FROM __SCHEMA__.wh_inbox_lease il
      WHERE il.instance_id = p_instance_id
        AND il.processed_at IS NULL
        AND il.lease_expiry > NOW()),
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
    UPDATE __SCHEMA__.wh_inbox_lease il
    SET attempts = GREATEST(il.attempts - 1, 0),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE il.instance_id = p_instance_id
      AND il.processed_at IS NULL
      AND il.stream_id = ANY(p_inbox_stream_ids);
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
-- plans/inbox-lease-side-table.md rather than pointed at an invented path.
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
  UPDATE __SCHEMA__.wh_inbox_lease il
  SET
      -- GREATEST(...,0) keeps the budget from going negative under a duplicated release (retry,
      -- at-least-once flush, a shutdown path that runs twice). A negative budget would make the row
      -- effectively un-dead-letterable, trading this bug for an unbounded-retry one.
      attempts = GREATEST(il.attempts - 1, 0),
      -- Clear the lease so the row is immediately claimable again rather than invisible until it
      -- would have expired. Handing work back is the whole point; making the caller wait out a lease
      -- it explicitly relinquished would just reintroduce the stall.
      instance_id = NULL,
      lease_expiry = NULL
  WHERE il.message_id = ANY(p_message_ids)
    -- Scoped to the caller's OWN claim. A release from an instance that does not hold the row is a
    -- no-op: without this, one worker could unlock a row another instance is actively dispatching
    -- and two workers would handle the same message concurrently. This predicate is also what makes
    -- the call idempotent: after the first release instance_id is NULL, so a repeat matches nothing
    -- and cannot refund twice.
    AND il.instance_id = p_instance_id
    -- Never disturb rows that already completed.
    AND il.processed_at IS NULL;

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
      UPDATE __SCHEMA__.wh_inbox_lease
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM __SCHEMA__.wh_inbox_lease il
        WHERE il.message_id = ANY(p_ids)
          AND il.processed_at IS NULL
          AND il.stream_id = ast.stream_id
          AND il.instance_id = ast.assigned_instance_id;
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
  UPDATE __SCHEMA__.wh_inbox_lease SET instance_id = NULL, lease_expiry = NULL
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
    -- 162: this is the one batch-one function that writes BOTH tables, because status is a property
    -- of the message and the rest is claim state. Two statements in one transaction, claim state
    -- first so the backoff reads attempts from the table that owns it.
    --
    -- Attempts are counted by claim_orphaned_inbox alone, so the backoff reads attempts as it
    -- stands: the value after the claim's bump is the attempt that just failed.
    UPDATE __SCHEMA__.wh_inbox_lease il
    SET error = v_failure.error_message,
        failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
        -- Exponential backoff: 30s * 2^attempts, capped at 5 minutes
        scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(il.attempts, 10)), 10)),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE il.message_id = v_failure.msg_id;

    UPDATE __SCHEMA__.wh_inbox i
    SET status = i.status | v_failure.status_flags | 32768  -- Set Failed bit (32768)
    WHERE i.message_id = v_failure.msg_id;
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
    -- 162: the inbox's claim state lives in wh_inbox_lease, so releasing a dead instance's inbox
    -- leases no longer rewrites the message rows. This is the reclaim path that runs most often on
    -- a fleet losing pods, and it was the one paying the most per row released.
    UPDATE __SCHEMA__.wh_inbox_lease
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
  USING __SCHEMA__.wh_inbox_lease il
  WHERE il.message_id = i.message_id
    AND i.message_type <> ALL(p_handled_types)
    AND il.processed_at IS NULL
    AND il.instance_id IS NULL
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
      (elem->>'MessageId')::UUID as msg_id,
      (elem->>'Status')::INTEGER as status_flags
    FROM jsonb_array_elements(p_completions) as elem
  LOOP
    -- Get current status and stream_id
    SELECT i.status, i.stream_id
    INTO v_current_status, v_stream_id
    FROM __SCHEMA__.wh_inbox i
    WHERE i.message_id = v_completion.msg_id;

    -- Skip if message not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    v_new_status := v_current_status | v_completion.status_flags;

    IF p_debug_mode THEN
      -- Debug mode: Retain message for troubleshooting
      -- 162: status belongs to the message; processed_at and the lease are claim state. Two
      -- statements in one transaction rather than one across two tables.
      UPDATE __SCHEMA__.wh_inbox i
      SET status = v_new_status
      WHERE i.message_id = v_completion.msg_id;
      UPDATE __SCHEMA__.wh_inbox_lease il
      SET processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE il.message_id = v_completion.msg_id;

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
        -- 162: same split as the debug branch above.
        UPDATE __SCHEMA__.wh_inbox i
        SET status = v_new_status
        WHERE i.message_id = v_completion.msg_id;
        UPDATE __SCHEMA__.wh_inbox_lease il
        SET processed_at = p_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE il.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
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
      il.stream_id,
      i.handler_name,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.status,
      il.attempts,
      i.partition_number,
      i.is_event,
      il.error,
      i.priority,
      ROW_NUMBER() OVER (PARTITION BY il.stream_id ORDER BY i.message_id) AS rank_in_stream
    FROM __SCHEMA__.wh_inbox_lease il
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = il.message_id
    -- v0.658 slice 7: mirror of fetch_outbox_batch's Empty/NULL stream handling -- see the matching
    -- comment in the outbox query for the full rationale.
    WHERE (
        il.stream_id = ANY(p_stream_ids)
        OR ((il.stream_id IS NULL OR il.stream_id = __EMPTY_UUID__::uuid)
            AND il.message_id = ANY(p_stream_ids))
      )
      AND il.instance_id = p_instance_id
      AND il.lease_expiry > NOW()
      AND il.processed_at IS NULL  -- inbox uses processed_at as both production-marker and debug-kept-marker
      AND (il.scheduled_for IS NULL OR il.scheduled_for <= NOW())
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
