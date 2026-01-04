-- Migration: 027_ClaimOrphanedPerspectiveEvents.sql
-- Date: 2025-12-25
-- Description: Creates claim_orphaned_perspective_events function for claiming orphaned perspective events.
--              Ensures sequential ordering within stream/perspective. Respects stream ownership.
-- Dependencies: 001-026 (requires wh_perspective_events, wh_active_streams tables from migration 009)

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_perspective_events(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200)
) AS $$
BEGIN
  RETURN QUERY
  UPDATE wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry
  WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
    AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
    AND pe.processed_at IS NULL
    -- Respect stream ownership
    AND EXISTS (
      SELECT 1 FROM wh_active_streams ast
      WHERE ast.stream_id = pe.stream_id
        AND ast.assigned_instance_id = p_instance_id
        AND ast.lease_expiry > p_now
    )
    -- Critical: Ensure ordering - no earlier uncompleted events in same perspective
    AND NOT EXISTS (
      SELECT 1 FROM wh_perspective_events earlier
      WHERE earlier.stream_id = pe.stream_id
        AND earlier.perspective_name = pe.perspective_name
        AND earlier.sequence_number < pe.sequence_number
        AND (
          -- Earlier event is claimed by another instance
          (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
          OR
          -- Earlier event is scheduled for future retry
          (earlier.scheduled_for > p_now)
        )
    )
  RETURNING pe.event_work_id AS event_work_id, pe.stream_id AS stream_id, pe.perspective_name AS perspective_name;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_perspective_events IS
'Claims orphaned perspective events with expired or null leases. Ensures sequential ordering within stream/perspective by checking for earlier uncompleted events. Respects stream ownership. Returns claimed work IDs for Orphaned flag in orchestrator response.';
