-- Migration: 024_ClaimOrphanedOutbox.sql
-- Date: 2025-12-25
-- Description: Creates claim_orphaned_outbox function for claiming orphaned outbox messages.
--              Uses partition-based load balancing to distribute work across instances.
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
BEGIN
  RETURN QUERY
  UPDATE wh_outbox o
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry
  WHERE (o.instance_id IS NULL OR o.lease_expiry < p_now)
    AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
    AND o.processed_at IS NULL
    -- Partition-based load balancing: claim only messages for this instance's partitions
    AND (
      o.partition_number IS NULL
      OR (o.partition_number % p_active_instance_count) = p_instance_rank
    )
    -- STREAM OWNERSHIP CHECK: Only claim if stream is not owned by another *live* instance.
    -- See claim_orphaned_inbox (migration 025) for the full rationale — abandoned instances
    -- (past the stale cutoff) hold no blocking ownership, so fresh cross-instance claims
    -- recover within the stale threshold rather than waiting for the 300 s active-streams
    -- lease to expire naturally.
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
  RETURNING o.message_id AS message_id, o.stream_id AS stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox IS
'Claims orphaned outbox messages with expired or null leases. Uses partition-based load balancing to distribute work. Respects stream ownership for stream-based messages — but only when the owning instance is still heartbeating (ast.assigned_instance_id present in wh_service_instances with last_heartbeat_at >= p_stale_cutoff). An abandoned (non-heartbeating) instance''s lease does NOT block cross-instance claims, giving SIGKILL-tolerant recovery bounded by the stale threshold rather than the 300 s active-streams lease. Returns claimed message IDs for Orphaned flag in orchestrator response.';
