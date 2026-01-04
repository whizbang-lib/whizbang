-- Migration: 024_ClaimOrphanedOutbox.sql
-- Date: 2025-12-25
-- Description: Creates claim_orphaned_outbox function for claiming orphaned outbox messages.
--              Uses partition-based load balancing to distribute work across instances.
-- Dependencies: 001-023 (requires wh_outbox, wh_active_streams tables, compute_partition function)

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER
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
    -- STREAM OWNERSHIP CHECK: Only claim if stream is not owned by another instance
    -- This allows claiming messages from unowned streams (first claim gets ownership)
    AND NOT EXISTS (
      SELECT 1 FROM wh_active_streams ast
      WHERE ast.stream_id = o.stream_id
        AND ast.assigned_instance_id != p_instance_id
        AND ast.lease_expiry > p_now
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
'Claims orphaned outbox messages with expired or null leases. Uses partition-based load balancing to distribute work. Respects stream ownership for stream-based messages. Returns claimed message IDs for Orphaned flag in orchestrator response.';
