-- Migration: 024_ClaimOrphanedOutbox.sql
-- Date: 2026-04-21 (owner-preferring claim — fixes rank-churn wedge, production incident)
-- Description: Creates claim_orphaned_outbox function for claiming orphaned outbox messages.
--              Owner-preferring semantics: a stream's live owner always claims its
--              messages; partition-based load balancing applies only to unowned /
--              abandoned-owner streams.
-- Dependencies: 001-023 (requires wh_outbox, wh_active_streams tables, compute_partition function)

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_outbox');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(
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
    UPDATE wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = o.attempts + 1
    WHERE (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND (
        -- OWNER PATH — if this instance owns the stream (live, non-expired lease),
        -- it always claims. Partition modulo is IGNORED. Prevents the rank-churn
        -- wedge described in migration 025.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = o.stream_id
            AND ast.assigned_instance_id = p_instance_id
            AND ast.lease_expiry > p_now
        )
        OR
        -- UNOWNED / ABANDONED PATH — partition-based load balancing for streams
        -- with no live owner.
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
                  AND si.last_heartbeat_at >= p_stale_cutoff
              )
          )
        )
      )
      -- STREAM ORDERING CHECK: Don't claim if there's an earlier message in the same stream
      -- that's scheduled for future retry (blocks later messages until retry time passes)
      AND NOT EXISTS (
        SELECT 1 FROM wh_outbox earlier
        WHERE earlier.stream_id = o.stream_id
          AND earlier.created_at < o.created_at
          AND earlier.scheduled_for IS NOT NULL
          AND earlier.scheduled_for > p_now
          AND earlier.processed_at IS NULL
      )
    RETURNING o.message_id AS c_message_id, o.stream_id AS c_stream_id, o.partition_number AS c_partition_number
  ),
  -- Slice 6 fix: see claim_orphaned_perspective_events (mig 027) for full rationale.
  -- Any successful UNOWNED-path claim UPSERTs wh_active_streams so the ownership ledger
  -- stays current. INSERT creates a row when producer-side pinning never fired (NULL
  -- p_instance_id from strategy-flush); DO UPDATE refreshes last_activity_at always
  -- and re-binds assigned_instance_id only when NULL or pointing to a dead instance.
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at)
    SELECT DISTINCT c.c_stream_id, COALESCE(c.c_partition_number, 0), p_instance_id, p_now
    FROM claimed c
    WHERE c.c_stream_id IS NOT NULL
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

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox IS
'Claims orphaned outbox messages with expired or null leases. Owner-preferring: a stream''s live owner always claims its messages (FIFO per-stream, immune to rank churn from scale events). Partition-modulo load balancing applies only to streams with no live owner. An abandoned (non-heartbeating) instance''s lease does NOT block cross-instance claims, giving SIGKILL-tolerant recovery bounded by the stale threshold rather than the 300 s active-streams lease. Returns claimed message IDs for Orphaned flag in orchestrator response.';
