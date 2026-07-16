-- Migration: 074_ReclassifyEventsEphemeral.sql
-- Date: 2026-07-15
-- Description: E1 #13c1 — reclassification primitive. When a type that already has stored Sourced events
--              is made [Ephemeral], reclassify_events_ephemeral retroactively stamps EventFlags.Ephemeral
--              on its historical rows and offloads their inline bodies to wh_event_body (exactly what the
--              emit chain would have done at store time), so the tier-1 reaper (073, perform_maintenance
--              Task 8) then cleans them up consumption-gated. Homogeneity guard: skip + count any stream
--              that would become MIXED (contains the target type AND a Sourced event of ANOTHER type) so
--              the all-Sourced-or-all-Ephemeral invariant is never violated. Idempotent; already-ephemeral
--              rows are ignored.
--              Takes the FULL NAME SET of one logical type (current ClrTypeName + FormerNames) so a renamed
--              type's history is matched under every name it was ever stored as, AND its own former-name
--              events are NOT mistaken for "another type" by the homogeneity guard. The C# layer (#13c2)
--              supplies that name set from the generated catalog entry, keyed by pinned_id for rename
--              stability.
-- Dependencies: 063 (normalize_event_type + event_type encoding), 072 (wh_event_body), 062 (flags column)

SELECT __SCHEMA__.drop_all_overloads('reclassify_events_ephemeral');

CREATE OR REPLACE FUNCTION __SCHEMA__.reclassify_events_ephemeral(p_event_types TEXT[])
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  streams_blocked BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  v_names TEXT[];
  v_events BIGINT := 0;
  v_streams BIGINT := 0;
  v_blocked BIGINT := 0;
BEGIN
  -- Normalize every name the logical type was ever stored under (current + former).
  SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_names
  FROM unnest(p_event_types) AS t;

  IF v_names IS NULL OR array_length(v_names, 1) IS NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  -- Count streams that would become MIXED: they hold the target type (under any of its names) AND a Sourced
  -- (flags & 8 = 0) event whose type is NOT one of those names. Reclassifying the target there would violate
  -- the homogeneous-stream invariant, so these are skipped by the offload/stamp below and reported here.
  SELECT COUNT(DISTINCT es.stream_id) INTO v_blocked
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_type = ANY(v_names)
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store b
      WHERE b.stream_id = es.stream_id
        AND NOT (b.event_type = ANY(v_names))
        AND (b.flags & c_flag_ephemeral) = 0
    );

  -- 1) Retroactively offload inline bodies to wh_event_body (what the emit chain does at store time for an
  --    ephemeral event). MUST run before the NULL-out below, while event_data is still populated. Scoped to
  --    the target type's Sourced rows in NON-blocked (would-stay-homogeneous) streams.
  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  SELECT es.event_id, es.event_data, es.metadata
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_type = ANY(v_names)
    AND (es.flags & c_flag_ephemeral) = 0
    AND es.event_data IS NOT NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store b
      WHERE b.stream_id = es.stream_id
        AND NOT (b.event_type = ANY(v_names))
        AND (b.flags & c_flag_ephemeral) = 0
    )
  ON CONFLICT (event_id) DO NOTHING;

  -- 2) Stamp ephemeral + null the inline body in one UPDATE per row, capturing the affected streams.
  WITH reclassified AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = es.flags | c_flag_ephemeral,
        event_data = NULL,
        metadata = NULL
    WHERE es.event_type = ANY(v_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(v_names))
          AND (b.flags & c_flag_ephemeral) = 0
      )
    RETURNING es.stream_id
  )
  SELECT COUNT(*), COUNT(DISTINCT stream_id) INTO v_events, v_streams FROM reclassified;

  RETURN QUERY SELECT v_events, v_streams, v_blocked;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reclassify_events_ephemeral IS
'E1 #13c1 — reclassify a formerly-Sourced event type to Ephemeral across its stored history. Takes the full name set of ONE logical type (current + former names): stamp EventFlags.Ephemeral (flags | 8) + offload inline bodies to wh_event_body so the tier-1 reaper (perform_maintenance Task 8) cleans them up consumption-gated. Skips + counts streams that would become mixed (target type + a Sourced event of another type) to preserve the homogeneous-stream invariant. Idempotent; already-ephemeral rows are left untouched. Returns (events_reclassified, streams_reclassified, streams_blocked).';
