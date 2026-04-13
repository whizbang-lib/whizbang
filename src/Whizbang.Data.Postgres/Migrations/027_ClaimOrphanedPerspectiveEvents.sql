-- Migration: 027_ClaimOrphanedPerspectiveEvents.sql
-- Date: 2025-12-25 (updated 2026-04-13 for full-stream capture)
-- Description: Creates claim_orphaned_perspective_events function for claiming orphaned perspective events.
--              Full-stream capture: once a stream is selected, ALL its unleased messages are leased.
--              The p_max_messages budget determines how many streams are selected (not a hard cap on messages).
--              Ensures sequential ordering within stream/perspective. Respects stream ownership.
-- Dependencies: 001-026 (requires wh_perspective_events, wh_active_streams tables from migration 009)

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_perspective_events(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_max_messages INTEGER DEFAULT 500
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
  -- Count messages per (stream, perspective) pair
  stream_message_counts AS (
    SELECT
      ce.stream_id,
      ce.perspective_name,
      COUNT(*)::INTEGER as msg_count
    FROM claimable_events ce
    GROUP BY ce.stream_id, ce.perspective_name
  ),
  -- Select streams using cumulative budget: once a stream is selected, ALL its messages are claimed.
  -- Running total determines when to stop adding new streams.
  selected_streams AS (
    SELECT
      smc.stream_id,
      smc.perspective_name,
      smc.msg_count,
      SUM(smc.msg_count) OVER (ORDER BY smc.stream_id, smc.perspective_name) as running_total
    FROM stream_message_counts smc
  ),
  -- Include all streams whose running total (at start of that stream) is within budget
  budget_streams AS (
    SELECT ss.stream_id, ss.perspective_name
    FROM selected_streams ss
    WHERE ss.running_total - ss.msg_count < p_max_messages
  )
  -- Claim ALL events for selected streams (full-stream capture)
  UPDATE wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry
  FROM claimable_events ce
  INNER JOIN budget_streams bs
    ON ce.stream_id = bs.stream_id
    AND ce.perspective_name = bs.perspective_name
  WHERE pe.event_work_id = ce.event_work_id
  RETURNING pe.event_work_id AS event_work_id, pe.stream_id AS stream_id, pe.perspective_name AS perspective_name;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_perspective_events IS
'Claims orphaned perspective events with full-stream capture: once a stream is selected within the message budget, ALL its unleased events are claimed. Ensures sequential ordering within stream/perspective by checking for earlier uncompleted events. Respects stream ownership. Returns claimed work IDs.';
