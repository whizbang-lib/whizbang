-- Migration: 025_ClaimOrphanedInbox.sql
-- Date: 2026-04-21 (owner-preferring claim — fixes rank-churn wedge, production incident)
-- Description: Creates claim_orphaned_inbox function for claiming orphaned inbox messages.
--              Owner-preferring semantics: a stream's live owner always claims its
--              messages; partition-based load balancing applies only to unowned /
--              abandoned-owner streams.
-- Dependencies: 001-024 (requires wh_inbox, wh_active_streams tables, compute_partition function)

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_inbox');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  WITH claimed AS (
    UPDATE __SCHEMA__.wh_inbox i
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: claim_orphaned is the SOLE source of attempt counting.
        -- Bumps unconditionally on every claim (fresh or re-claim) so attempts = 1 means
        -- "first attempt has started" (one-based). process_inbox_failures records error and
        -- releases the lease but does NOT bump — the next claim's bump captures attempt N+1.
        -- Single-source removes the double-counting that two bumps per failed cycle would cause.
        -- Without this, hung handlers (no exception thrown, lease eventually expires) looked
        -- identical to fresh messages in a consumer's production environment — an extended stuck-message backlog with
        -- attempts=0 across thousands of rows (production audit).
        attempts = i.attempts + 1,
        -- Attribute the attempt this claim is REPLACING. process_inbox_failures records
        -- error/failure_reason only when dispatch reported a failure; when the process is killed
        -- mid-dispatch (SIGKILL from a failed liveness probe, container replaced, handler hung past
        -- its lease) nothing reports anything — the lease just expires and the bump above spends
        -- another attempt in silence. The budget then runs out and the row dead-letters as
        -- "MaxAttemptsExceeded: attempts=N > max=M", which describes the counter and not the cause.
        --
        -- Observed in production as ~54k inbox rows averaging 11 attempts, every one with
        -- error IS NULL and failure_reason = 99 (Unknown) — no way to tell a crash-looping host
        -- from a genuinely failing handler. Stamp the abandonment so the row carries its own
        -- history: an expired lease still held by an instance (instance_id IS NOT NULL) means the
        -- previous attempt ended without ever reporting. Guarded on error IS NULL so a real
        -- recorded failure is never papered over — that error is the better diagnostic.
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
    WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
      AND i.processed_at IS NULL
      AND (
        -- OWNER PATH — if this instance already owns the stream (live, non-expired lease
        -- in wh_active_streams), it always claims. Partition modulo is IGNORED. This
        -- preserves per-stream FIFO and prevents the rank-churn wedge observed in
        -- production: when active_instance_count changes, partition-modulo
        -- routing for a partition number can shift to a different rank than the stream's
        -- existing owner's rank. Without this branch, the modulo-matched instance is
        -- blocked by the ownership NOT EXISTS below AND the owner is blocked by the
        -- modulo filter — neither can claim.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = i.stream_id
            AND ast.assigned_instance_id = p_instance_id
            AND ast.lease_expiry > p_now
        )
        OR
        -- UNOWNED / ABANDONED PATH — stream has no live owner. Partition-based load
        -- balancing decides which rank picks it up. NULL partition_number (message has
        -- no stream binding) remains claimable by any rank.
        (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank)
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = i.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND ast.lease_expiry > p_now
              AND EXISTS (
                -- A live instance = one whose last heartbeat is within the abandon threshold
                -- (p_stale_cutoff). An instance that stopped heartbeating (SIGKILL, crash,
                -- container replaced) holds no meaningful ownership: its wh_active_streams
                -- lease reflects intent that will never be realised. Without the heartbeat-
                -- recency clause the dead lease remains "live" for up to lease_duration_seconds
                -- (300 s by default), blocking fresh cross-instance claims for minutes.
                -- See migration 029 (process_work_batch) for where v_stale_cutoff is computed
                -- and 011 (cleanup_stale_instances) for the eventual DELETE once the row is
                -- past the threshold; this claim-time check is what makes the system actually
                -- recover at the threshold boundary rather than at the lease-expiry boundary.
                SELECT 1 FROM __SCHEMA__.wh_service_instances si
                WHERE si.instance_id = ast.assigned_instance_id
                  -- Slice 2b of zero-idle-polling — owner counts as alive if
                  -- EITHER its heartbeat is fresh OR a Whizbang LISTEN
                  -- connection is currently registered in pg_stat_activity
                  -- (the latter is TCP-fresh regardless of heartbeat cadence).
                  -- Additive — existing heartbeat-only semantics preserved.
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
'Claims orphaned inbox messages with expired or null leases. Owner-preferring: a stream''s live owner always claims its messages (FIFO per-stream, immune to rank churn from scale events). Partition-modulo load balancing applies only to streams with no live owner. An abandoned (non-heartbeating) instance''s lease does NOT block cross-instance claims, giving SIGKILL-tolerant recovery bounded by the stale threshold rather than the 300 s active-streams lease. Returns claimed message IDs for Orphaned flag in orchestrator response.';
