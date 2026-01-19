-- Migration: 026_ClaimOrphanedReceptorWork.sql
-- Date: 2025-12-25
-- Description: Creates claim_orphaned_receptor_work function for claiming orphaned receptor processing work.
--              Uses partition-based load balancing to distribute work across instances.
-- Dependencies: 001-025 (requires wh_receptor_processing, wh_active_streams tables)

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_receptor_work(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS TABLE(
  processing_id UUID,
  stream_id UUID
) AS $$
BEGIN
  RETURN QUERY
  UPDATE wh_receptor_processing rp
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry,
      claimed_at = p_now
  WHERE (rp.instance_id IS NULL OR rp.lease_expiry < p_now)
    AND rp.completed_at IS NULL
    -- Partition-based load balancing using stream_id
    AND (
      rp.partition_number IS NULL
      OR (rp.partition_number % p_active_instance_count) = p_instance_rank
    )
    -- Respect stream ownership
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = rp.stream_id
        AND ast.assigned_instance_id = p_instance_id
        AND ast.lease_expiry > p_now
    )
  RETURNING rp.id AS processing_id, rp.stream_id AS stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_receptor_work IS
'Claims orphaned receptor processing work with expired or null leases. Uses partition-based load balancing. Respects stream ownership. Returns claimed processing IDs for Orphaned flag in orchestrator response.';
