-- Migration: 172_OutboxLockOrder.sql
-- Date: 2026-09-28
-- Description: One lock order for the outbox and its stream ledger, and a continuation that never
--              waits (#936).
--
--              With stream runs (171), concurrent outbox drains deadlocked (40P01). On a deployed
--              service of several instances, the first run of a job that stored a few hundred
--              one-row streams stalled the outbox tail for about forty seconds, until the claim loop
--              offered again the streams the failed drain had been continuing. Nothing was lost.
--
--              The cycle, reproduced by OutboxStreamRunDeadlockSqlTests and read from the server log:
--                * claim_orphaned_outbox (inside claim_work) refreshes wh_active_streams for the
--                  streams whose rows it just leased, as one UPDATE ... FROM claimed. It locked the
--                  ledger rows in the order the join yielded, which follows the order the rows were
--                  leased (arrival).
--                * wh_continue_outbox_streams, on the SAME instance's drain session, renewed
--                  last_activity_at on the ledger rows of the streams it continued, also one
--                  UPDATE ... FROM, in the order it was asked.
--                Two multi-row writers of one instance's ledger rows, in two unrelated orders:
--                  claim        holds ledger(S1)  waits ledger(S2)
--                  continuation holds ledger(S2)  waits ledger(S1)      -> 40P01
--                Both sides wait "for ShareLock on transaction ... while updating tuple in relation
--                wh_active_streams". The continuation was the victim, and the drain worker did not
--                retry it, so its streams waited for the claim loop.
--
--              The lock-order rule, for every statement that locks more than one outbox or ledger row:
--                1. Tables in one order: wh_outbox, then wh_active_streams (the canonical table order
--                   162 set: wh_outbox, wh_inbox, wh_inbox_state, wh_perspective_events,
--                   wh_active_streams).
--                2. Rows in one order within a table: wh_outbox by (stream_id, created_at,
--                   message_id), idx_outbox_stream_run's key and the order a stream publishes in;
--                   wh_active_streams by stream_id, the order the claim's PIN path already used.
--                3. A statement with no need to wait does not wait: it locks with SKIP LOCKED and treats
--                   a locked row as absent. Only a write that must happen (a lease extension, a
--                   completion, a failure) waits, and then only in the order of rules 1 and 2.
--                4. A lock re-asserts the predicate that chose the row, so a row that changed hands
--                   after the statement's snapshot is not held by a statement that will not write it.
--
--              What changes:
--                * wh_continue_outbox_streams: the ledger renewal locks ORDER BY stream_id with SKIP
--                  LOCKED (rule 3). The continuation now waits on nothing, so it cannot be in a cycle.
--                  Its run lock re-asserts takeability (rule 4).
--                * claim_orphaned_outbox: the REFRESH path locks its ledger rows ORDER BY stream_id
--                  before updating them (rule 2); it still waits, because the stream lease it extends
--                  is not optional. Its run lock re-asserts takeability (rule 4): before this, a run
--                  row the continuation leased after the claim's snapshot was locked by the claim and
--                  never written, and the completion of that row waited for the whole claim_work.
--                * complete_outbox_published: rows locked ORDER BY (stream_id, created_at, message_id)
--                  before the delete (or the debug-mode update), not in primary-key order.
--                * process_outbox_failures: failures applied in (stream_id, created_at, message_id)
--                  order, so the failed row and then the rest of its run are one ascending pass.
--                * renew_leases, outbox branch: the outbox rows, then the ledger rows, each in its
--                  order. The inbox and perspective branches are unchanged.
--              And in the drain worker: a continuation that loses a deadlock is retried at once from
--              the same cursors (the failed statement changed nothing) instead of leaving its streams
--              to the claim loop.
--
--              Cost and plans. Every lock subquery is driven by the keys the statement was given or
--              the rows it just claimed: primary-key lookups on wh_outbox or wh_active_streams, a Sort
--              of that set, then LockRows. The sort is of the batch, never the table
--              (ai-docs/load-under-bulk-import.md). The heads, the run walks and the re-offer are
--              untouched, so ClaimWorkPlanShapeTests' bounds and 171's idx_outbox_stream_run range
--              scans hold as they were. No index is added or dropped.
--
--              Not changed, recorded: claim_work still writes wh_active_streams in more than one pass
--              per poll (the outbox REFRESH, the outbox PIN, then the inbox and perspective
--              acquisitions' own passes). Each outbox pass is ascending, but two instances' claims
--              could still interleave across passes on a stream both believe unowned. That needs a
--              stale snapshot on both sides at once and was not reproduced; merging the passes would
--              reopen the unique-index contention PR #227 split them to avoid.
--
-- Dependencies: 171 (claim_orphaned_outbox, process_outbox_failures, wh_continue_outbox_streams,
--               copied verbatim with the deltas above), 162 (renew_leases), 029
--               (complete_outbox_published)
-- Objects: claim_orphaned_outbox, process_outbox_failures, wh_continue_outbox_streams,
--          complete_outbox_published, renew_leases
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_outbox: last word 171_OutboxStreamRuns.sql, plus the ordered refresh and the
-- rechecked run lock. Signature unchanged.
-- ---------------------------------------------------------------------------------------------
-- The arity differs across the corpus (171 added two parameters), so every definition site clears the
-- overloads first (OverloadGuardsCoverEveryDefinitionSiteTests). The signature is 171's, unchanged.
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_outbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ClaimOrphanedOutbox_RefreshesTheStreamLedgerInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimOrphanedOutbox_RunStopsAtTheFirstRowItMayNotTakeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ClaimOrphanedOutbox_FirstClaim_BumpsToOneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  -- 171: how many consecutive rows one stream may lease in one call. 1 is the previous behavior.
  p_run_length INTEGER DEFAULT 1,
  -- 171: how many of the oldest rows choose the streams that move. NULL means p_max_rows, the
  -- previous behavior; claim_work passes its stream window.
  p_max_heads INTEGER DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
DECLARE
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  -- LEAST ignores a NULL, so a NULL head cap falls back to the row bound, and a NULL row bound (an
  -- untaught caller) stays unlimited exactly as before.
  v_heads INTEGER := LEAST(p_max_heads, p_max_rows);
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION — see claim_orphaned_inbox (mig 025) for the full rationale. Unbounded, this
  -- statement leases every eligible row at once and charges an attempt to each, so the caller's
  -- claim limit governs only what comes back out, never how much gets taken. LIMIT NULL is
  -- unlimited in Postgres, so the default preserves the previous behavior for untaught callers.
  WITH candidates AS (
    -- The HEADS: the oldest claimable rows, which choose the streams that move this call. Full
    -- predicate, under a row lock: selecting by age and filtering for ownership afterwards would let
    -- another instance's rows permanently fill this instance's window. (148, verbatim, plus the two
    -- columns the run needs.)
    SELECT o.message_id AS cand_message_id, o.stream_id AS cand_stream_id, o.created_at AS cand_created_at
    FROM __SCHEMA__.wh_outbox o
    WHERE (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
      AND (
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = o.stream_id
            AND ast.assigned_instance_id = p_instance_id
            AND ast.lease_expiry > p_now
        )
        OR
        (
          (o.partition_number IS NULL
           OR (o.partition_number % p_active_instance_count) = p_instance_rank)
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = o.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND ast.lease_expiry > p_now
              AND EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_service_instances si
                WHERE si.instance_id = ast.assigned_instance_id
                  AND (
                    si.last_heartbeat_at >= p_stale_cutoff
                    OR EXISTS (
                      SELECT 1 FROM pg_stat_activity sa
                      WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || si.instance_id::text
                    )
                  )
              )
          )
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_outbox earlier
        WHERE earlier.stream_id = o.stream_id
          AND earlier.created_at < o.created_at
          AND earlier.scheduled_for IS NOT NULL
          AND earlier.scheduled_for > p_now
          AND earlier.processed_at IS NULL
          -- #668 H3: a coalesce-pending row parks with scheduled_for = created + MaxDelay,
          -- which made it an 'earlier scheduled row' that blocked EVERY later row on the
          -- same stream from claiming for the whole window — re-armed by each new coalesce
          -- row, i.e. permanently under sustained ingest. Coalesce rows are not deferred
          -- deliveries; they are parked for folding and must not gate their stream.
          AND earlier.coalesce_group IS NULL
      )
    ORDER BY o.created_at
    LIMIT v_heads
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- 171: the streams whose runs extend past their heads. A NULL or empty stream id is a singleton
  -- (the drain looks it up by message id), so it has no run to extend. With a run of 1 there is
  -- nothing to extend either, and the walk below never executes.
  run_streams AS (
    SELECT DISTINCT c.cand_stream_id AS run_stream_id
    FROM candidates c
    WHERE v_run > 1
      AND c.cand_stream_id IS NOT NULL
      AND c.cand_stream_id <> __EMPTY_UUID__::uuid
  ),
  -- Each stream's share of the row bound: the run length, or an even split of the bound when the
  -- chosen streams cannot all have a full run. Walking only the share is what keeps the walk priced
  -- by the batch: streams x share is about the row bound, never streams x run length.
  run_share AS (
    SELECT CASE
             WHEN p_max_rows IS NULL THEN v_run
             ELSE LEAST(v_run, GREATEST(1, CEIL(p_max_rows::NUMERIC / GREATEST(count(*), 1))::INTEGER))
           END AS share
    FROM run_streams
  ),
  run_walk AS (
    SELECT w.w_message_id, w.w_stream_id, w.w_created_at, w.w_takeable, w.w_pos
    FROM run_streams rs
    CROSS JOIN LATERAL (
      -- The stream's pending rows from its first entry, in its own order, skipping the ones this
      -- instance already holds under a live lease (they are its own run already, in flight). One
      -- range scan on idx_outbox_stream_run: Index Cond stream_id = rs.run_stream_id, the ORDER BY
      -- is the index order, the lease test is an index filter on INCLUDEd columns, and the LIMIT
      -- stops the scan at the share.
      SELECT o.message_id AS w_message_id,
             o.stream_id AS w_stream_id,
             o.created_at AS w_created_at,
             -- COALESCEd because bool_and below skips a NULL, which would read as "not blocking".
             COALESCE((o.instance_id IS NULL OR o.lease_expiry < p_now)
               AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now), FALSE) AS w_takeable,
             ROW_NUMBER() OVER (ORDER BY o.created_at, o.message_id) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = rs.run_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND (o.instance_id IS DISTINCT FROM p_instance_id
             OR o.lease_expiry IS NULL
             OR o.lease_expiry <= p_now)
      ORDER BY o.created_at, o.message_id
      LIMIT (SELECT share FROM run_share)
    ) w
  ),
  -- A run is a prefix: it ends at the first row this instance may not take -- a row another
  -- instance holds under a live lease, or a retry deferred into the future. Taking a row beyond it
  -- would publish past a row that has not been published.
  run_open AS (
    SELECT rw.*, bool_and(rw.w_takeable) OVER (PARTITION BY rw.w_stream_id ORDER BY rw.w_pos) AS w_open
    FROM run_walk rw
  ),
  -- 172: the lock re-asserts that the row is still takeable. The walk above read the rows under
  -- the statement's snapshot; a row another session leased and committed since then (the drain's
  -- continuation, a sibling's claim) passes a lock that tests only its id, and this statement then
  -- held it until claim_work committed while never updating it, so that session's completion of
  -- the row waited for the whole poll. With the predicate the lock's recheck drops it, and it ends
  -- the run as a gap does.
  run_locked AS (
    SELECT o.message_id AS locked_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id IN (SELECT ro.w_message_id FROM run_open ro WHERE ro.w_open)
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- ...and at the first row another session holds locked, for the same reason: a lock skipped in
  -- the middle of a run would leave a gap in it.
  run_first_gap AS (
    SELECT ro.w_stream_id AS gap_stream_id, MIN(ro.w_pos) AS gap_pos
    FROM run_open ro
    WHERE ro.w_open
      AND NOT EXISTS (SELECT 1 FROM run_locked rl WHERE rl.locked_message_id = ro.w_message_id)
    GROUP BY ro.w_stream_id
  ),
  run_rows AS (
    SELECT ro.w_message_id, ro.w_pos, ro.w_created_at
    FROM run_open ro
    LEFT JOIN run_first_gap g ON g.gap_stream_id = ro.w_stream_id
    WHERE ro.w_open
      AND (g.gap_pos IS NULL OR ro.w_pos < g.gap_pos)
  ),
  -- The heads always, then run rows round-robin by position (every stream's 2nd row before any
  -- stream's 3rd), cut at the row bound. Cutting by position keeps each stream's rows a prefix.
  chosen AS (
    SELECT u.chosen_message_id
    FROM (
      SELECT a.chosen_message_id, MIN(a.chosen_pos) AS chosen_pos, MIN(a.chosen_created_at) AS chosen_created_at
      FROM (
        SELECT c.cand_message_id AS chosen_message_id, 0::BIGINT AS chosen_pos, c.cand_created_at AS chosen_created_at
        FROM candidates c
        UNION ALL
        SELECT r.w_message_id, r.w_pos, r.w_created_at
        FROM run_rows r
      ) a
      GROUP BY a.chosen_message_id
    ) u
    ORDER BY u.chosen_pos, u.chosen_created_at, u.chosen_message_id
    LIMIT p_max_rows
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = o.attempts + 1
    FROM chosen c
    WHERE o.message_id = c.chosen_message_id
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL  -- 115: coalesce-pending singles are never leased by the pump
    -- Ownership, partition routing and the stream-ordering check were all resolved in `candidates`
    -- above, under a row lock, and a run shares its head's stream and so its ownership. Only the
    -- cheap invariants are re-asserted here, against a lease that lapsed between selection and write.
    --
    --   OWNER PATH — a stream's live owner always claims its messages, partition modulo ignored,
    --   which prevents the rank-churn wedge described in migration 025.
    --   UNOWNED / ABANDONED PATH — partition-based load balancing for streams with no live owner;
    --   "live" means a fresh heartbeat OR a registered LISTEN connection in pg_stat_activity.
    --   STREAM ORDERING — an earlier message in the same stream awaiting a future retry blocks the
    --   later ones, so per-stream order survives a scheduled retry.
    RETURNING o.message_id AS c_message_id, o.stream_id AS c_stream_id, o.partition_number AS c_partition_number
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into two paths to
  -- eliminate the unique-index leaf-page deadlock observed in production under N pods ×
  -- 250 ms polling. See Whizbang PR #227 for the full diagnosis.
  --
  -- REFRESH path (steady-state, >99% of claims under load): if this instance already
  -- owns the stream with a live lease, the prior UPSERT was wasted work that only
  -- bumped last_activity_at on a row we already owned, yet still took the unique-index
  -- leaf-page lock on each INSERT...ON CONFLICT. A plain row UPDATE achieves the same
  -- semantic (refresh last_activity_at) without touching the unique-index INSERT path
  -- at all → no leaf-page contention, no deadlock possible on this code path.
  --
  -- 172: the refresh takes its ledger rows in stream_id order, the order PIN below already uses. As a
  -- plain UPDATE ... FROM claimed it locked them in whatever order the join produced, which follows
  -- the order the rows were leased, and the drain's continuation (wh_continue_outbox_streams) wrote
  -- the same instance's ledger rows in the order it was asked: two multi-row writers of one set of
  -- rows in two orders, which is a deadlock as soon as two streams are shared (#936). The refresh
  -- must wait for a row it cannot lock (the lease it extends is not optional), so it waits in the one
  -- order every waiting writer of wh_active_streams uses.
  refresh_locked AS (
    SELECT ast.stream_id AS locked_stream_id
    FROM (SELECT DISTINCT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL) cs
    JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = cs.c_stream_id
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    ORDER BY ast.stream_id
    FOR UPDATE OF ast
  ),
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM refresh_locked rl
    WHERE ast.stream_id = rl.locked_stream_id
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  -- PIN path (rare): only fires for streams NOT covered by REFRESH — first-time pinning
  -- (producer-side strategy-flush left assigned_instance_id NULL), abandoned-owner
  -- reassignment, or orphan-claim transferring ownership across instances. ORDER BY
  -- stream_id forces concurrent pods to acquire the unique-index leaf-page locks in a
  -- consistent order, which prevents lock-cycle deadlocks on this remaining path as
  -- well (lock-ordering precludes cycle formation).
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
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
  'Acquires unowned/abandoned pending outbox rows for an instance (see 024 for the ownership rationale, 115 for the '
  'coalesce-aware selection, 148 for the stream lease). 171: the oldest claimable rows (at most p_max_heads) choose '
  'the streams that move, and each leases up to p_run_length of its next consecutive rows as a prefix, within '
  'p_max_rows in total; a run of 1 is the previous behavior. 172: ledger rows are refreshed in stream_id order, and a '
  'run row another session leased since the snapshot is not locked.';

-- ---------------------------------------------------------------------------------------------
-- process_outbox_failures: last word 171_OutboxStreamRuns.sql, plus the ordered visit. Signature
-- unchanged.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ProcessOutboxFailures_TakesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ProcessOutboxFailures_MidRun_ReleasesTheRestOfTheRunToBeRetriedInOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxInboxFailureReasonSqlTests.cs:OutboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ProcessOutboxFailures_DoesNotDoubleCountAttemptsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_outbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
  v_schedule_id TEXT;
  v_delivery INTEGER;
  -- 171: where the failed row sat, so the rest of its run can be released behind it.
  v_holder UUID;
  v_stream UUID;
  v_created TIMESTAMPTZ;
BEGIN
  IF jsonb_array_length(p_failures) = 0 THEN RETURN; END IF;

  -- 172: the failures are visited in (stream_id, created_at, message_id) order, not the order the
  -- flush delivered them in. Each one locks its row and then the rest of its run, which lie after it
  -- in that same order, so a flush takes its rows in one ascending pass, the order every other
  -- waiting writer of wh_outbox uses (complete_outbox_published, renew_leases). The read that sorts
  -- them takes no lock. A failure whose row is gone sorts last and updates nothing, as before.
  FOR v_failure IN
    SELECT f.msg_id, f.status_flags, f.error_message, f.failure_reason
    FROM (
      SELECT
        (e.elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
        (e.elem->>'CompletedStatus')::INTEGER as status_flags,
        e.elem->>'Error' as error_message,
        COALESCE(e.elem->>'Reason', e.elem->>'FailureReason')::INTEGER as failure_reason,
        e.ord
      FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS e(elem, ord)
    ) f
    LEFT JOIN __SCHEMA__.wh_outbox fo ON fo.message_id = f.msg_id
    ORDER BY fo.stream_id NULLS LAST, fo.created_at, f.msg_id, f.ord
  LOOP
    SELECT o.metadata->>'scheduleId', COALESCE((o.metadata->>'deliveryGuarantee')::INTEGER, 0),
           o.instance_id, o.stream_id, o.created_at
    INTO v_schedule_id, v_delivery, v_holder, v_stream, v_created
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = v_failure.msg_id;

    IF v_schedule_id IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, error_message)
      VALUES (v_schedule_id::UUID, v_failure.msg_id, p_now, 1, v_failure.error_message);
    END IF;

    IF v_schedule_id IS NOT NULL AND v_delivery = 1 THEN
      -- AT-MOST-ONCE (delivery_guarantee = 1): never redeliver. Parked terminally; the failure is
      -- durably recorded in wh_schedule_runs above, which is what makes this safe.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),
          scheduled_for = 'infinity'::TIMESTAMPTZ,             -- terminal: no retry, ever
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;
    ELSE
      -- Default (at-least-once, and every non-schedule message): retry with backoff. Attempts are
      -- counted by claim_orphaned_outbox alone, so the backoff reads o.attempts as it stands.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
          -- Exponential backoff: 30s * 2^attempts, capped at 5 minutes
          scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(o.attempts, 10)), 10)),
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;

      -- 171: release the rest of the run. The rows of the same stream after the failed one that its
      -- holder still leases were claimed with it and must not publish ahead of its retry. Before
      -- this they stayed leased, the next drain of the stream fetched them, and they went out
      -- before the row that failed. Released, they are unowned and the acquisition's ordering rule
      -- (an earlier row deferred into the future blocks the later ones) holds them behind the
      -- retry. The attempt their claim charged is refunded: none of them was attempted. One range
      -- scan on idx_outbox_stream_run from the failed row's position; the holder test is an index
      -- filter on an INCLUDEd column.
      IF v_holder IS NOT NULL AND v_stream IS NOT NULL AND v_stream <> __EMPTY_UUID__::uuid THEN
        UPDATE __SCHEMA__.wh_outbox o
        SET instance_id = NULL,
            lease_expiry = NULL,
            attempts = GREATEST(o.attempts - 1, 0)
        WHERE o.stream_id = v_stream
          AND (o.created_at, o.message_id) > (v_created, v_failure.msg_id)
          AND o.instance_id = v_holder
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
          AND o.coalesce_group IS NULL;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.process_outbox_failures(JSONB, TIMESTAMPTZ) IS
  'Processes outbox message failures: sets the Failed flag, records the error and reason, and '
  'schedules the retry with exponential backoff (capped at 5 minutes), releasing the lease for '
  'reclaim. A failing schedule occurrence also lands a wh_schedule_runs Failed row, and an '
  'at-most-once schedule (delivery_guarantee=1) is parked terminally instead of retried. Reads the '
  'element the runtime writes (MessageId, Reason) as well as the older name (FailureReason). 171: a '
  'retried row releases the rest of its run (later rows of its stream its holder still leases, attempt '
  'refunded), so the stream is retried in order. 172: the failures are applied in (stream_id, created_at, message_id) '
  'order, so the rows are locked in the order every waiting writer of wh_outbox uses.';

-- ---------------------------------------------------------------------------------------------
-- wh_continue_outbox_streams: last word 171_OutboxStreamRuns.sql, plus the skip-locked ledger
-- renewal and the rechecked run lock. Signature unchanged.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_continue_outbox_streams');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ContinueOutboxStreams_WhileAnotherSessionHoldsTheStreamLedger_NeverWaitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_LeasesTheNextRunAndReturnsOnlyRowsAfterTheCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_AStreamThisInstanceDoesNotOwn_IsNotContinuedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_continue_outbox_streams(
  p_instance_id UUID,
  p_stream_ids UUID[],
  -- The last message the drain PUBLISHED on each stream, position for position with p_stream_ids.
  -- Rows this instance holds at or before it are published and waiting for their completion to
  -- land; they are neither leased again nor returned.
  p_after_message_ids UUID[],
  p_run_length INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  commit_sequence BIGINT,
  origin_service_id UUID,
  origin_commit_sequence BIGINT,
  error TEXT,
  priority INTEGER
) AS $$
#variable_conflict use_column
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  v_run_ids UUID[];
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Lease each stream's next run. Only a stream whose lease this instance holds is continued: the
  -- claim took that lease with the stream's earlier rows, and continuing under it is what lets the
  -- drain move on without waiting for the next claim cycle while keeping the stream on one instance.
  -- The rows join the stream's lease rather than starting a new one.
  WITH asked AS (
    SELECT a.a_stream_id, a.a_after_id
    FROM unnest(p_stream_ids, p_after_message_ids) AS a(a_stream_id, a_after_id)
    WHERE a.a_stream_id IS NOT NULL
      AND a.a_stream_id <> __EMPTY_UUID__::uuid
  ),
  owned AS (
    SELECT DISTINCT ON (ak.a_stream_id) ak.a_stream_id AS o_stream_id, ak.a_after_id AS o_after_id,
           ast.lease_expiry AS o_lease_expiry
    FROM asked ak
    JOIN __SCHEMA__.wh_active_streams ast
      ON ast.stream_id = ak.a_stream_id
     AND ast.assigned_instance_id = p_instance_id
     AND ast.lease_expiry > v_now
    ORDER BY ak.a_stream_id
  ),
  run_walk AS (
    SELECT ow.o_stream_id, ow.o_lease_expiry, w.w_message_id, w.w_mine, w.w_takeable, w.w_pos
    FROM owned ow
    CROSS JOIN LATERAL (
      -- The stream's pending rows in its own order, skipping the ones already published and waiting
      -- for their completion (held here, at or before the cursor). Rows this instance holds after
      -- the cursor are unpublished work of the run already and count toward it. Same range scan as
      -- the claim's run walk: idx_outbox_stream_run, Index Cond stream_id = $, LIMIT the run.
      SELECT o.message_id AS w_message_id,
             COALESCE(o.instance_id = p_instance_id AND o.lease_expiry > v_now, FALSE) AS w_mine,
             -- COALESCEd because bool_and below skips a NULL, which would read as "not blocking".
             COALESCE((o.instance_id IS NULL OR o.lease_expiry < v_now)
               AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now), FALSE) AS w_takeable,
             ROW_NUMBER() OVER (ORDER BY o.created_at, o.message_id) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = ow.o_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND NOT COALESCE(o.instance_id = p_instance_id
                         AND o.lease_expiry > v_now
                         AND o.message_id <= ow.o_after_id, FALSE)
      ORDER BY o.created_at, o.message_id
      LIMIT v_run
    ) w
  ),
  -- A prefix, as in the claim: it ends at the first row neither held here nor takeable.
  run_open AS (
    SELECT rw.*, bool_and(rw.w_mine OR rw.w_takeable) OVER (PARTITION BY rw.o_stream_id ORDER BY rw.w_pos) AS w_open
    FROM run_walk rw
  ),
  -- 172: as in the claim, the lock re-asserts that the row is still takeable, so a row another session
  -- leased since the snapshot is neither held nor leased here; it ends the run as a gap does.
  run_locked AS (
    SELECT o.message_id AS locked_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id IN (SELECT ro.w_message_id FROM run_open ro WHERE ro.w_open AND NOT ro.w_mine)
      AND (o.instance_id IS NULL OR o.lease_expiry < v_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
      AND o.processed_at IS NULL
    FOR UPDATE OF o SKIP LOCKED
  ),
  run_first_gap AS (
    SELECT ro.o_stream_id AS gap_stream_id, MIN(ro.w_pos) AS gap_pos
    FROM run_open ro
    WHERE ro.w_open
      AND NOT ro.w_mine
      AND NOT EXISTS (SELECT 1 FROM run_locked rl WHERE rl.locked_message_id = ro.w_message_id)
    GROUP BY ro.o_stream_id
  ),
  run_rows AS (
    SELECT ro.w_message_id, ro.w_mine, ro.o_lease_expiry
    FROM run_open ro
    LEFT JOIN run_first_gap g ON g.gap_stream_id = ro.o_stream_id
    WHERE ro.w_open
      AND (g.gap_pos IS NULL OR ro.w_pos < g.gap_pos)
  ),
  leased AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = r.o_lease_expiry,
        -- Single-source attempt counting, as in claim_orphaned_outbox: a lease charges one.
        attempts = o.attempts + 1
    FROM run_rows r
    WHERE o.message_id = r.w_message_id
      AND NOT r.w_mine
      AND (o.instance_id IS NULL OR o.lease_expiry < v_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
    RETURNING o.message_id AS leased_message_id
  ),
  -- 172: the continuation never waits. Its outbox locks already skip what another session holds;
  -- this is its only other write, and it used to wait. It only moves last_activity_at, and the
  -- session holding a ledger row is the claim refreshing it or the lease renewal extending it, both
  -- of which keep the stream's lease current themselves, so a locked row is skipped. Waiting here,
  -- holding the run rows it had just locked, against the claim's refresh of the same instance's
  -- ledger rows in another order, was the deadlock of #936. A statement that waits for nothing
  -- cannot be part of a cycle.
  renew_locked AS (
    SELECT ast.stream_id AS locked_stream_id
    FROM owned ow
    JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = ow.o_stream_id
    WHERE ast.assigned_instance_id = p_instance_id
    ORDER BY ast.stream_id
    FOR UPDATE OF ast SKIP LOCKED
  ),
  renewed AS (
    -- The stream's activity follows its work, as the claim's refresh does. Nothing reads this CTE;
    -- a data-modifying CTE runs exactly once whether or not the statement reads it.
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = v_now
    FROM renew_locked rl
    WHERE ast.stream_id = rl.locked_stream_id
    RETURNING ast.stream_id
  )
  SELECT array_agg(x.run_id)
  INTO v_run_ids
  FROM (
    SELECT r.w_message_id AS run_id FROM run_rows r WHERE r.w_mine
    UNION
    SELECT l.leased_message_id FROM leased l
  ) x;

  IF v_run_ids IS NULL THEN
    RETURN;
  END IF;

  -- The run's rows, as fetch_outbox_batch (151) returns them: the same columns, the same event
  -- store join, the same order, and the same byte budget that trims a slice's tail and never its
  -- head. Addressed by primary key, so this reads exactly the run.
  RETURN QUERY
  WITH ranked AS (
    SELECT
      o.*,
      es.commit_sequence AS es_commit_sequence,
      es.origin_service_id AS es_origin_service_id,
      es.origin_commit_sequence AS es_origin_commit_sequence,
      ROW_NUMBER() OVER (
        PARTITION BY o.stream_id
        ORDER BY es.commit_sequence ASC NULLS LAST, o.message_id
      ) AS rank_in_stream
    FROM __SCHEMA__.wh_outbox o
    LEFT JOIN __SCHEMA__.wh_event_store es
      ON o.is_event AND es.event_id = o.message_id
    WHERE o.message_id = ANY(v_run_ids)
      AND o.instance_id = p_instance_id
      AND o.lease_expiry > v_now
      AND o.processed_at IS NULL
      AND o.published_at IS NULL
  ),
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id
              ORDER BY r.es_commit_sequence ASC NULLS LAST, r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.destination::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.envelope_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.es_commit_sequence,
    b.es_origin_service_id,
    b.es_origin_commit_sequence,
    b.error,
    b.priority
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.es_commit_sequence ASC NULLS LAST, b.message_id;
END;
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.wh_continue_outbox_streams(UUID, UUID[], UUID[], INTEGER, BIGINT) IS
  'Continues outbox streams from the lease this instance already holds (171): for each stream it owns, leases '
  'up to p_run_length of the stream''s next consecutive rows as a prefix (stopping at a row it may not take), '
  'and returns the rows it holds after the last one the drain published, in fetch_outbox_batch''s shape and '
  'order. Lets the drain move a long stream on without waiting for the next claim cycle. 172: it never waits for a '
  'lock: run rows and ledger rows another session holds are skipped.';

-- ---------------------------------------------------------------------------------------------
-- complete_outbox_published: last word 029_ProcessWorkBatch.sql, plus the ordered lock. Signature
-- unchanged.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:CompleteOutboxPublished_DeletesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:CompleteOutboxPublished_InDebugMode_UpdatesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CompleteOutboxPublishedSqlTests.cs:CompleteOutboxPublished_Production_DeletesRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.complete_outbox_published(
  p_ids UUID[],
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS INTEGER AS $$
DECLARE
  v_affected INTEGER;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  IF p_debug_mode THEN
    -- Debug mode: retain the row for forensics; stamp published_at + processed_at
    -- and set the Published bit. eligible_outbox filters published_at IS NULL so
    -- claim_work treats the row as if deleted on subsequent polls.
    UPDATE __SCHEMA__.wh_outbox o
    SET processed_at = NOW(),
        published_at = NOW(),
        status = o.status | 4,         -- Published flag
        instance_id = NULL,
        lease_expiry = NULL
    FROM (
      -- 172: locked in (stream_id, created_at, message_id) order; see the production branch.
      SELECT l.message_id
      FROM __SCHEMA__.wh_outbox l
      WHERE l.message_id = ANY(p_ids)
        AND l.processed_at IS NULL
      ORDER BY l.stream_id, l.created_at, l.message_id
      FOR UPDATE OF l
    ) locked
    WHERE o.message_id = locked.message_id
      AND o.processed_at IS NULL;
  ELSE
    -- Production mode: row exits the table on success — structurally immune to
    -- claim_work re-issuing it on the next poll cycle.
    --
    -- 172: the rows are locked in (stream_id, created_at, message_id) order before they are deleted.
    -- The bare DELETE locked them in primary-key order, which is not the order any other writer of
    -- the outbox uses; a completion flush that also reports failures holds these rows while
    -- process_outbox_failures takes more, so the two passes must run the same way. Primary-key
    -- lookups, then a sort of the batch: priced by the batch.
    DELETE FROM __SCHEMA__.wh_outbox o
    USING (
      SELECT l.message_id
      FROM __SCHEMA__.wh_outbox l
      WHERE l.message_id = ANY(p_ids)
        AND l.processed_at IS NULL
      ORDER BY l.stream_id, l.created_at, l.message_id
      FOR UPDATE OF l
    ) locked
    WHERE o.message_id = locked.message_id
      AND o.processed_at IS NULL;
  END IF;

  GET DIAGNOSTICS v_affected = ROW_COUNT;
  RETURN v_affected;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_outbox_published IS
'Completes outbox rows after successful transport publish. Production (p_debug_mode=FALSE, default): DELETEs the row — structurally immune to claim_work re-issuing it. Debug mode (p_debug_mode=TRUE): retains the row, stamps published_at + processed_at + Published bit; eligible_outbox filters published_at IS NULL so claim_work skips it. Coalesced + batched by C# OutboxCompletionFlushWorker. Idempotent — unknown ids silently no-op. Returns rows-affected (deletions in prod, updates in debug). 172: rows are locked in (stream_id, created_at, message_id) order.';

-- ---------------------------------------------------------------------------------------------
-- renew_leases: last word 162_InboxWorkStateSideTable.sql, plus the ordered outbox branch.
-- Signature unchanged.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:RenewLeases_ExtendsTheStreamLease_ForTheOwnersStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:RenewLeases_LeavesAStreamAssignedToAnotherInstanceAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RenewLeasesSqlTests.cs:RenewLeases_OutboxCategory_ExtendsLeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:RenewLeases_ForTheOutbox_TakesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:RenewLeases_ForTheOutbox_RenewsTheStreamLedgerInStreamOrderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.renew_leases(
  p_category TEXT,
  p_ids UUID[],
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS INTEGER AS $$
DECLARE
  v_new_expiry TIMESTAMPTZ := NOW() + (p_lease_seconds || ' seconds')::INTERVAL;
  v_updated INTEGER;
  -- 172: the outbox branch's held streams, read before the ledger is locked.
  v_streams UUID[];
  v_holders UUID[];
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  CASE p_category
    WHEN __CATEGORY_OUTBOX__ THEN
      -- 172: outbox rows in (stream_id, created_at, message_id) order, then ledger rows in stream_id
      -- order: the table order and the row orders every waiting writer of the outbox and the ledger
      -- uses. As bare multi-row UPDATEs both followed whatever order the plan produced, and the
      -- ledger write waits against the claim's refresh of the same rows.
      UPDATE __SCHEMA__.wh_outbox o
        SET lease_expiry = v_new_expiry
        FROM (
          SELECT l.message_id
          FROM __SCHEMA__.wh_outbox l
          WHERE l.message_id = ANY(p_ids)
            AND l.processed_at IS NULL
          ORDER BY l.stream_id, l.created_at, l.message_id
          FOR UPDATE OF l
        ) locked
        WHERE o.message_id = locked.message_id;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      -- The streams and their holders are read first, without a lock, so the ledger statement below
      -- names only the ledger (WorkTablesAreLockedInOneOrderTests reads text order as lock order).
      SELECT array_agg(h.stream_id), array_agg(h.instance_id)
        INTO v_streams, v_holders
        FROM (
          SELECT DISTINCT o.stream_id, o.instance_id
          FROM __SCHEMA__.wh_outbox o
          WHERE o.message_id = ANY(p_ids)
            AND o.processed_at IS NULL
            AND o.stream_id IS NOT NULL
            AND o.instance_id IS NOT NULL
        ) h;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM (
          SELECT a.stream_id
          FROM unnest(v_streams, v_holders) AS held(stream_id, instance_id)
          JOIN __SCHEMA__.wh_active_streams a
            ON a.stream_id = held.stream_id
           AND a.assigned_instance_id = held.instance_id
          ORDER BY a.stream_id
          FOR UPDATE OF a
        ) locked
        WHERE ast.stream_id = locked.stream_id;
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

COMMENT ON FUNCTION __SCHEMA__.renew_leases(TEXT, UUID[], INTEGER) IS
  'Batched lease extension per category, for the supplied ids that are not yet processed, and the stream lease of the '
  'streams they hold (148). 172: the outbox branch locks its outbox rows in (stream_id, created_at, message_id) order '
  'and then its ledger rows in stream_id order.';
