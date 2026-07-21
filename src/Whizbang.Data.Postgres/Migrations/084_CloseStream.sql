-- Migration: 084_CloseStream.sql
-- Date: 2026-07-18
-- Description: A1 (Archival & Compaction) increment 1 — the gated-truncate primitive for "closing the books"
--              on a durable Sourced stream. close_stream(stream_id, through_version) enforces two guards then
--              truncates the period's detail:
--                (1) CONSUMPTION GATE — every perspective that reads the stream must have processed every event
--                    at/below the close point (no unprocessed wh_perspective_events work item survives), so a
--                    projection mid-catch-up is never robbed of unread events. Mirrors the ephemeral reaper's
--                    work-item anti-join (073 Task 8).
--                (2) CARRY-FORWARD GUARD — at least one event must survive ABOVE the close point (the domain's
--                    closing event / new origin). "Closing the books" always leaves an opening balance; a total
--                    truncation is a bug, not a close.
--              When both hold it DELETEs wh_event_body + wh_event_store rows at/below the close point. This is
--              DISCARD-ONLY — moving the detail to cold storage is increment 2. No FK references wh_event_store
--              (verified #13b3), so the delete is safe; processed work items below the point are cleaned by
--              perform_maintenance Task 3. Standalone + on-demand (like prune_ancient_ephemeral_pointers),
--              invoked via IWorkCoordinator.CloseStreamAsync — NOT folded into perform_maintenance. Skipped
--              under debug_mode (forensic retention, like the reaper). The domain appends its closing event
--              (e.g. MonthClosed{openingBalance}) BEFORE calling this; the function only gates + truncates.
-- Dependencies: 046 (wh_event_store.version), 009 (wh_perspective_events), 032 (wh_settings/debug_mode),
--               072/077 (wh_event_body)

CREATE OR REPLACE FUNCTION __SCHEMA__.close_stream(p_stream_id UUID, p_through_version BIGINT)
RETURNS TABLE(close_status TEXT, events_truncated BIGINT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_blocked BIGINT;
  v_carry BIGINT;
  v_deleted BIGINT;
BEGIN
  -- debug_mode retains forensic history — a close is a truncation, so skip it entirely (like the reaper).
  SELECT (setting_value = 'true') INTO v_debug FROM wh_settings WHERE setting_key = 'debug_mode';
  IF COALESCE(v_debug, FALSE) THEN
    RETURN QUERY SELECT 'debug_skipped'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (1) Consumption gate: any perspective work item for an event in this stream at/below the close point that
  -- is still UNPROCESSED blocks the close.
  SELECT count(*) INTO v_blocked
  FROM __SCHEMA__.wh_perspective_events pe
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND pe.processed_at IS NULL;

  IF v_blocked > 0 THEN
    RETURN QUERY SELECT 'blocked'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (2) Carry-forward guard: refuse to discard a stream's entire history — there must be a surviving event
  -- above the close point (the domain's closing event / new origin).
  SELECT count(*) INTO v_carry
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version > p_through_version;

  IF v_carry = 0 THEN
    RETURN QUERY SELECT 'no_carry_forward'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Truncate the detail at/below the close point: bodies first, then pointers (no FK, but keep it tidy).
  DELETE FROM __SCHEMA__.wh_event_body eb
  USING __SCHEMA__.wh_event_store es
  WHERE eb.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version <= p_through_version;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;

  RETURN QUERY SELECT 'closed'::TEXT, v_deleted;
END;
$$ LANGUAGE plpgsql;
