-- Migration: 138_BoundedInboxAcquisitionIndex
-- Date: 2026-09-06
-- Description: Inbox acquisition is index-bounded and deterministic.
--
--   ROOT CAUSE it fixes: the `pick` stage of claim_orphaned_inbox ranks EVERY eligible pending inbox
--   row per stream (ROW_NUMBER() OVER (PARTITION BY stream_id ORDER BY received_at)) on EVERY claim
--   cycle, then keeps p_max_rows. No index matched that window order, so the planner ran a Seq Scan
--   of the whole inbox heap (~2.4 KB/row: envelope + metadata) plus a Sort - O(backlog) per poll.
--   Measured on an 88k-row bulk-import backlog: ~200 MB read and ~700 ms per call, called every
--   linger poll by every instance. That alone consumed most of the database CPU; the commit path
--   (commit_handler_batch) then starved behind it, crossed the client command timeout, dropped its
--   completions, and the lease-expiry → re-claim → poison-gate cascade throttled the drain to
--   ~100 rows/min while real rows dead-lettered as MaxAttemptsExceeded.
--
--   THE FIX (1/2 - this migration, semantics-preserving): a covering partial index in exactly the
--   window's order, carrying every column the predicate reads. The unchanged query becomes an
--   Index Only Scan with no Sort: no heap, no temp. Measured on a copy of the same backlog:
--   971 buffers vs 14,724 (15x) and 272-326 ms vs 469-633 ms (2x). The remaining cost is the
--   window over all eligible rows; bounding that algorithmically (per-stream ownership evaluated
--   once per stream, head-of-stream enumeration) is the follow-up migration - it was prototyped
--   with 1000/1000 parity against this function on that copy.
--
--   THE FIX (2/2): (received_at, message_id) is a TOTAL order. Bulk imports store many rows with
--   identical received_at; ordering on received_at alone made the per-stream rank and the
--   acquisition cut arbitrary - the same backlog could be claimed in a different order every cycle,
--   and the plan was free to re-sort. UUIDv7 message ids are chronological at the source, so the
--   tiebreak is also the intended order. The index key includes message_id for the same reason:
--   index order == window order, so the planner never re-sorts.
--
--   The two DROP INDEX statements retire ad-hoc experiment indexes that exist only on the
--   environment where this was diagnosed (no-ops elsewhere). claim_orphaned_inbox is re-issued
--   verbatim from 025 with only the two ORDER BY clauses extended; signature unchanged.
--
-- Tests: tests/Whizbang.Data.EFCore.Postgres.Tests/InboxAcquisitionIndexSqlTests.cs
--        tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs (parity)

CREATE INDEX IF NOT EXISTS idx_inbox_pending_stream_order
  ON __SCHEMA__.wh_inbox (stream_id, received_at, message_id)
  INCLUDE (instance_id, lease_expiry, scheduled_for, partition_number)
  WHERE processed_at IS NULL;

COMMENT ON INDEX __SCHEMA__.idx_inbox_pending_stream_order IS
  'Covering partial index in the acquisition window order (stream_id, received_at, message_id) over pending rows. '
  'Lets claim_orphaned_inbox rank per-stream eligibility index-only with no Sort (138). Also serves the per-stream '
  'inbox fetch (stream_id, received_at prefix).';

-- Ad-hoc experiment indexes superseded by the one above (present only where 138 was diagnosed).
DROP INDEX IF EXISTS __SCHEMA__.idx_inbox_stream_received_pending;
DROP INDEX IF EXISTS __SCHEMA__.idx_inbox_pending_stream_received;

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
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
  WITH pick AS (
    -- The FULL predicate belongs here, not a cheap pre-filter. Selecting rows by age alone and
    -- filtering for ownership afterwards would let another instance's rows permanently occupy this
    -- instance's window - it would take the limit in candidates, match none of them, and claim
    -- nothing while its own work waited behind them.
    -- #568: the ordering is breadth-first across streams (each stream's Nth row competes
    -- with other streams' Nth rows), so one bulk stream's flood cannot fill the whole
    -- acquisition window while a later interactive stream's first row waits outside it.
    -- Window functions cannot share a level with FOR UPDATE, so selection happens here and
    -- the lock (with a re-check of the volatile predicates) happens in candidates below.
    SELECT i.message_id AS cand_message_id,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM __SCHEMA__.wh_inbox i
    WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
      AND i.processed_at IS NULL
      AND (
        -- OWNER PATH - see the rationale on the original predicate below.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = i.stream_id
            AND ast.assigned_instance_id = p_instance_id
            AND ast.lease_expiry > p_now
        )
        OR
        -- UNOWNED / ABANDONED PATH
        (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank)
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = i.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND ast.lease_expiry > p_now
              AND EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_service_instances si
                WHERE si.instance_id = ast.assigned_instance_id
                  AND (
                    si.last_heartbeat_at >= p_stale_cutoff
                    OR EXISTS (
                      SELECT 1 FROM pg_stat_activity sa
                      WHERE sa.application_name = 'whizbang-' || si.instance_id::text
                    )
                  )
              )
          )
        )
      )
  ),
  candidates AS (
    -- Lock under breadth-first order. SKIP LOCKED skips rows a concurrent claimer holds, and
    -- the volatile predicates re-check under the lock - a row leased between pick and here is
    -- filtered exactly as the old single-level shape would have skipped it.
    SELECT i.message_id AS cand_message_id
    FROM __SCHEMA__.wh_inbox i
    JOIN pick ON pick.cand_message_id = i.message_id
    WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND i.processed_at IS NULL
    ORDER BY pick.stream_seq, pick.cand_received_at, pick.cand_message_id
    LIMIT p_max_rows
    FOR UPDATE OF i SKIP LOCKED
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_inbox i
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
    RETURNING i.message_id AS c_message_id, i.stream_id AS c_stream_id, i.partition_number AS c_partition_number
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into REFRESH (row-only
  -- UPDATE for already-owned-with-live-lease streams) + PIN (INSERT...ON CONFLICT for
  -- the rare ownership-transition case, with ORDER BY stream_id for consistent lock
  -- acquisition). Symmetric with the fix in claim_orphaned_outbox (mig 024); see that
  -- migration for the full rationale. Eliminates the 40P01 deadlock observed in
  -- production (Whizbang PR #227).
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND c.c_stream_id IS NOT NULL
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now
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
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_inbox IS
  'Acquires unowned/abandoned pending inbox rows for an instance in breadth-first per-stream order, bounded by '
  'p_max_rows (see 025 for the ownership rationale). 138: the order is total - (received_at, message_id) - and the '
  'pick is served index-only by idx_inbox_pending_stream_order.';
