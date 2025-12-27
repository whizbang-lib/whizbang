-- Migration: 025_ClaimOrphanedInbox.sql
-- Date: 2025-12-25
-- Description: Creates claim_orphaned_inbox function for claiming orphaned inbox messages.
--              Uses partition-based load balancing to distribute work across instances.
-- Dependencies: 001-024 (requires wh_inbox, wh_active_streams tables, compute_partition function)

CREATE OR REPLACE FUNCTION claim_orphaned_inbox(
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
  UPDATE wh_inbox i
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry
  WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
    AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
    AND i.processed_at IS NULL
    -- Partition-based load balancing: claim only messages for this instance's partitions
    AND (
      i.partition_number IS NULL
      OR (i.partition_number % p_active_instance_count) = p_instance_rank
    )
    -- STREAM OWNERSHIP CHECK: Only claim if stream is not owned by another instance
    -- This allows claiming messages from unowned streams (first claim gets ownership)
    AND NOT EXISTS (
      SELECT 1 FROM wh_active_streams ast
      WHERE ast.stream_id = i.stream_id
        AND ast.assigned_instance_id != p_instance_id
        AND ast.lease_expiry > p_now
    )
  RETURNING i.message_id AS message_id, i.stream_id AS stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION claim_orphaned_inbox IS
'Claims orphaned inbox messages with expired or null leases. Uses partition-based load balancing to distribute work. Respects stream ownership for stream-based messages. Returns claimed message IDs for Orphaned flag in orchestrator response.';
