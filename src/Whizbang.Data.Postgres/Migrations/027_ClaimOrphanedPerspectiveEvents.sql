-- Migration: 027_ClaimOrphanedPerspectiveEvents.sql
-- Date: 2025-12-25 (updated 2026-04-13 for full-stream capture, stream count limit)
-- Description: Creates claim_orphaned_perspective_events function for claiming orphaned perspective events.
--              Full-stream capture: once a stream is selected, ALL its unleased messages are leased.
--              p_max_streams limits how many distinct streams are selected (default 500).
--              Ensures sequential ordering within stream/perspective. Respects stream ownership.
-- Dependencies: 001-026 (requires wh_perspective_events, wh_active_streams tables from migration 009)

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_perspective_events(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_max_streams INTEGER DEFAULT 500
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200)
) AS $$
BEGIN
  RETURN QUERY
  WITH claimable_events AS (
    -- Find all events eligible for claiming (orphaned or unleased)
    SELECT
      pe.event_work_id,
      pe.stream_id,
      pe.perspective_name,
      pe.event_id
    FROM wh_perspective_events pe
    WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND pe.processed_at IS NULL
      -- Respect stream ownership
      AND (
        NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = pe.stream_id
        )
        OR EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = pe.stream_id
            AND (
              ast.assigned_instance_id = p_instance_id
              OR ast.lease_expiry <= p_now
              OR NOT EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_service_instances si
                WHERE si.instance_id = ast.assigned_instance_id
              )
            )
        )
      )
      -- Ensure ordering - no earlier uncompleted events in same perspective
      AND NOT EXISTS (
        SELECT 1 FROM wh_perspective_events earlier
        WHERE earlier.stream_id = pe.stream_id
          AND earlier.perspective_name = pe.perspective_name
          AND earlier.event_id < pe.event_id
          AND (
            (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
            OR (earlier.scheduled_for > p_now)
          )
      )
  ),
  -- Select up to p_max_streams distinct streams from claimable events
  selected_streams AS (
    SELECT DISTINCT ce.stream_id
    FROM claimable_events ce
    LIMIT p_max_streams
  )
  -- Claim ALL events for selected streams (full-stream capture)
  UPDATE wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry
  FROM claimable_events ce
  INNER JOIN selected_streams ss ON ce.stream_id = ss.stream_id
  WHERE pe.event_work_id = ce.event_work_id
  RETURNING pe.event_work_id AS event_work_id, pe.stream_id AS stream_id, pe.perspective_name AS perspective_name;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_perspective_events IS
'Claims orphaned perspective events with full-stream capture: selects up to p_max_streams distinct streams, then leases ALL unleased events for each selected stream. Ensures sequential ordering within stream/perspective by checking for earlier uncompleted events. Respects stream ownership. Returns claimed work IDs.';
