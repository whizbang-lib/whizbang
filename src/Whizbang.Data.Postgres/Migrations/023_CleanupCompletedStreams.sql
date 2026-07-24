-- Migration: 023_CleanupCompletedStreams.sql
-- Date: 2025-12-25 (rewritten 2026-05-01 — Phase H step 6 slice 1)
-- Description: Removes wh_active_streams entries for streams that have no pending work
--              across wh_outbox / wh_inbox / wh_perspective_events. Called after batch
--              completions land so the next event for an evicted stream rebinds via the
--              UPSERT path in store_outbox_messages / store_inbox_messages.
-- Dependencies: 001-022 (requires wh_active_streams, wh_outbox, wh_inbox, wh_perspective_events tables)

SELECT __SCHEMA__.drop_all_overloads('cleanup_completed_streams');

CREATE OR REPLACE FUNCTION __SCHEMA__.cleanup_completed_streams(
  p_stream_ids UUID[]
) RETURNS INTEGER AS $$
DECLARE
  v_evicted INTEGER;
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  -- Single set-based DELETE: remove active_streams rows for input stream_ids that
  -- have no pending work across the three work tables. The "no pending" check filters
  -- on processed_at IS NULL (outbox, inbox, perspective_events) — same shape used by
  -- claim_orphaned_*. Eviction is safe because the next event-store call for that
  -- stream re-runs the wh_active_streams UPSERT.
  WITH evicted AS (
    DELETE FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = ANY(p_stream_ids)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = a.stream_id
          AND o.processed_at IS NULL
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox i
        WHERE i.stream_id = a.stream_id
          AND i.processed_at IS NULL
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = a.stream_id
          AND pe.processed_at IS NULL
      )
    RETURNING a.stream_id
  )
  SELECT COUNT(*)::INTEGER INTO v_evicted FROM evicted;

  RETURN COALESCE(v_evicted, 0);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.cleanup_completed_streams IS
'Evicts wh_active_streams rows for the supplied stream_ids whose pending-work tables (wh_outbox / wh_inbox / wh_perspective_events) are empty. Called from completion-flush workers after a batch lands so the next event for an evicted stream rebinds via store_*_messages UPSERT. Returns count of streams evicted.';
