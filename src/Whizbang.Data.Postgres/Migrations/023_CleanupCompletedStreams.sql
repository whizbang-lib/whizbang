-- Migration: 023_CleanupCompletedStreams.sql
-- Date: 2025-12-25
-- Description: Creates cleanup_completed_streams function for removing streams with no pending work.
--              Checks outbox (unpublished), inbox (unstored), and perspective events (unprocessed).
-- Dependencies: 001-022 (requires wh_active_streams, wh_outbox, wh_inbox, wh_perspective_events tables)

CREATE OR REPLACE FUNCTION cleanup_completed_streams(
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_stream_id UUID;
  v_has_pending_work BOOLEAN;
BEGIN
  -- Query temp_completed_perspectives to get candidate stream_ids
  FOR v_stream_id IN (
    SELECT DISTINCT tcp.stream_id FROM temp_completed_perspectives tcp
  )
  LOOP
    -- Check if stream has any pending work across all work tables
    v_has_pending_work := (
      -- Unpublished outbox messages
      EXISTS (
        SELECT 1 FROM wh_outbox o
        WHERE o.stream_id = v_stream_id
          AND (o.status & 4) != 4  -- Published flag not set
      )
      OR
      -- Unstored inbox messages
      EXISTS (
        SELECT 1 FROM wh_inbox i
        WHERE i.stream_id = v_stream_id
          AND (i.status & 2) != 2  -- EventStored flag not set
      )
      OR
      -- Unprocessed perspective events
      EXISTS (
        SELECT 1 FROM wh_perspective_events pe
        WHERE pe.stream_id = v_stream_id
          AND pe.processed_at IS NULL
      )
    );

    -- Remove stream from active_streams if no pending work
    IF NOT v_has_pending_work THEN
      DELETE FROM wh_active_streams a
      WHERE a.stream_id = v_stream_id;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION cleanup_completed_streams IS
'Removes streams from wh_active_streams when all work is complete. Checks for unpublished outbox messages, unstored inbox messages, and unprocessed perspective events. Called by orchestrator after processing completions.';
