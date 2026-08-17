-- Migration: 085_EventArchive.sql
-- Date: 2026-07-18
-- Description: A1 (Archival & Compaction) increment 2 — cold-storage archive. wh_event_archive holds a full
--              cold copy of a closed stream's detail (the wh_event_store pointer columns + the wh_event_body
--              body columns), written by close_stream BEFORE the truncate when archiving is requested — so the
--              period detail is preserved, auditable, and retrievable while the hot event store stays lean.
--              Archived rows are out of every hot read by construction (they are DELETEd from wh_event_store).
--              close_stream gains p_archive; the archive INSERT and the truncate run in ONE transaction (the
--              function body), so a failure rolls BOTH back — the detail is never lost (stays hot) nor left
--              half-archived. Discard (p_archive=FALSE) is the increment-1 behaviour, unchanged.
-- Dependencies: 084 (close_stream), 046 (wh_event_store.version), 072/077 (wh_event_body)

-- Cold-storage table: a full copy of the archived event (pointer + body). Kept out of the hot event store.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_archive (
  event_id       UUID PRIMARY KEY,
  stream_id      UUID NOT NULL,
  aggregate_id   UUID,
  aggregate_type VARCHAR(500),
  event_type     VARCHAR(500) NOT NULL,
  event_data     JSONB,
  metadata       JSONB,
  scope          JSONB,
  version        INTEGER NOT NULL,
  created_at     TIMESTAMPTZ,
  archived_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_event_archive_stream ON __SCHEMA__.wh_event_archive (stream_id, version);

COMMENT ON TABLE __SCHEMA__.wh_event_archive IS
  'A1: cold-storage archive of closed-stream detail. Written by close_stream(p_archive=TRUE) BEFORE the '
  'truncate, so detail is preserved + auditable + retrievable out of the hot event store. Retrieved via '
  'IWorkCoordinator.GetArchivedEventsAsync. The Postgres-default IArchiveStore; blob offload is pluggable.';

-- close_stream gains p_archive. Adding a defaulted param would leave the old 2-arg overload as a sibling and
-- make a 2-arg call ambiguous, so drop it first; the C# adapter always passes all three args.
DROP FUNCTION IF EXISTS __SCHEMA__.close_stream(UUID, BIGINT);

CREATE OR REPLACE FUNCTION __SCHEMA__.close_stream(
  p_stream_id UUID, p_through_version BIGINT, p_archive BOOLEAN DEFAULT FALSE)
RETURNS TABLE(close_status TEXT, events_truncated BIGINT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_blocked BIGINT;
  v_carry BIGINT;
  v_deleted BIGINT;
BEGIN
  -- debug_mode retains forensic history — a close is a truncation, so skip it entirely (like the reaper).
  SELECT (setting_value = 'true') INTO v_debug FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode';
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

  -- Archive BEFORE truncate. Same transaction as the DELETEs below => atomic: a failure here rolls back the
  -- whole close, so the detail is never lost (stays hot) nor left half-archived. Body columns come from
  -- wh_event_body (post-split every body lives there); LEFT JOIN tolerates an already-reaped body defensively.
  IF p_archive THEN
    INSERT INTO __SCHEMA__.wh_event_archive
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at)
    SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
           eb.event_data, eb.metadata, es.scope, es.version, es.created_at
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND es.version <= p_through_version
    ON CONFLICT (event_id) DO NOTHING;
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
