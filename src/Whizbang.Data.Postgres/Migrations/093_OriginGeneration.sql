-- 093_OriginGeneration.sql
--
-- #80-F: origin generation — seal coherence across LEGITIMATE history mutation
-- (docs: resilience/stream-integrity; proposal: bounded-integrity-reconciliation).
--
-- A close ("close the books" truncation, 084/085/087) or a reclassification (074/078/087)
-- changes what a fold over already-sealed history computes. Without a signal, every consumer's
-- seal disagrees with the origin forever, and the next full sweep alarms on what was deliberate.
-- The two mutation sites now do two things:
--   1. REFOLD the affected sealed epochs INLINE — the origin's own manifest answers are correct
--      immediately, not after the next nightly sweep.
--   2. BUMP the origin generation — carried on every manifest, so consumers reset their seals
--      and re-verify (cheap: the origin answers from epochs) instead of alarming.
-- Sealed-range divergence WITHOUT a generation change remains what it always was: damage.
--
-- The consumer-side guard (integrity_seal_generation_guard) is one atomic call: unchanged
-- generation = proceed; changed = reset the seal to zero, record the new generation, and tell
-- the caller to skip this comparison round (its windows were aligned to the old world). The
-- reset happens once per generation change.
--
-- Dependencies: 092 (epochs, refold, wh_integrity_seals), 087 (the functions re-created here)

INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('integrity_origin_generation', '0', 'integer',
        'Bumped whenever folded history legitimately mutates (close_stream truncation, reclassification). Carried on manifests so consumers reset seals instead of alarming.')
ON CONFLICT (setting_key) DO NOTHING;

-- 092's wh_integrity_seals predates the generation; additive + replay-safe.
ALTER TABLE __SCHEMA__.wh_integrity_seals
  ADD COLUMN IF NOT EXISTS origin_generation BIGINT NOT NULL DEFAULT 0;

CREATE OR REPLACE FUNCTION __SCHEMA__._wh_bump_origin_generation() RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  UPDATE wh_settings
  SET setting_value = ((setting_value)::bigint + 1)::text
  WHERE setting_key = 'integrity_origin_generation';
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_bump_origin_generation IS
'Internal: advances the origin generation after a legitimate mutation of folded history.';

-- Refolds every sealed epoch covering [p_min_seq, p_max_seq] on one lane. No frontier row = the
-- lane never sealed anything = nothing stale to fix (refold_digest_epochs also clamps, but the
-- width lookup must not fail on a missing lane).
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refold_epochs_covering(
  p_lane    UUID,
  p_min_seq BIGINT,
  p_max_seq BIGINT
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_width BIGINT;
BEGIN
  SELECT f.epoch_width INTO v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = p_lane;
  IF v_width IS NULL THEN
    RETURN;
  END IF;
  PERFORM __SCHEMA__.refold_digest_epochs(p_lane, p_min_seq / v_width, p_max_seq / v_width);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_refold_epochs_covering IS
'Internal: refolds the sealed epochs whose sequence ranges a legitimate mutation touched.';

-- The consumer-side guard. TRUE = generation coherent, compare away. FALSE = the origin's
-- history legitimately moved: the seal is reset to zero, the new generation recorded, and the
-- caller skips this comparison round. First contact records the offered generation and proceeds.
CREATE OR REPLACE FUNCTION __SCHEMA__.integrity_seal_generation_guard(
  p_origin     UUID,
  p_generation BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_gen BIGINT;
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_seals (origin_service_id, sealed_through, origin_generation, updated_at)
  VALUES (p_origin, 0, p_generation, NOW())
  ON CONFLICT (origin_service_id) DO NOTHING;

  SELECT s.origin_generation INTO v_gen
  FROM __SCHEMA__.wh_integrity_seals s
  WHERE s.origin_service_id = p_origin;

  IF v_gen = p_generation THEN
    RETURN TRUE;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_seals
  SET sealed_through = 0, origin_generation = p_generation, updated_at = NOW()
  WHERE origin_service_id = p_origin;
  RETURN FALSE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.integrity_seal_generation_guard IS
'Consumer guard: unchanged generation proceeds; a changed one resets the seal once and skips the round — deliberate history mutation is re-verified, never alarmed on.';

-- ============================================================================
-- close_stream — re-created VERBATIM from 087 + (#80-F) inline epoch refold + generation bump.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.close_stream(
  p_stream_id UUID, p_through_version BIGINT, p_archive BOOLEAN DEFAULT FALSE)
RETURNS TABLE(close_status TEXT, events_truncated BIGINT) AS $$
DECLARE
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
  v_debug BOOLEAN;
  v_blocked BIGINT;
  v_carry BIGINT;
  v_deleted BIGINT;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
BEGIN
  -- debug_mode retains forensic history — a close is a truncation, so skip it entirely (like the reaper).
  -- (bare wh_settings — public allow-list object; 087's copy carried baselined debt here)
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

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows this
  -- close is about to truncate — the sealed epochs covering those ranges refold AFTER the delete
  -- (they must fold the post-close world), and the ranges are unknowable once the rows are gone.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, c_zero) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id
      AND es.version <= p_through_version
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Migration 087 (A1c): subtract the about-to-be-truncated rows from wh_stream_digests BEFORE
  -- the delete (bodies must still exist for the metadata predicate). Only rows that ever
  -- contributed to a digest (non-ephemeral, not at-most-once) are XOR-ed back out — XOR is
  -- self-inverse, so folding the same hashes again removes them. A missing bucket is a no-op;
  -- the periodic full-sweep verification heals any residual drift.
  UPDATE __SCHEMA__.wh_stream_digests d
  SET digest_lo = d.digest_lo # s.digest_lo,
      digest_hi = d.digest_hi # s.digest_hi,
      event_count = d.event_count - s.event_count,
      updated_at = NOW()
  FROM (
    SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
           COALESCE(es.scope ->> 't', '') AS scope_tenant,
           es.event_type,
           es.stream_id,
           bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
           bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
           COUNT(*)::int AS event_count
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id
      AND es.version <= p_through_version
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 1, 2, 3, 4
  ) s
  WHERE d.origin_service_id = s.origin_service_id
    AND d.scope_tenant = s.scope_tenant
    AND d.event_type = s.event_type
    AND d.stream_id = s.stream_id;

  -- Drop buckets the subtraction emptied (a fully-truncated (type, stream) bucket). Separate
  -- statement so the UPDATE above is visible.
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.stream_id = p_stream_id AND d.event_count <= 0;

  -- Truncate the detail at/below the close point: bodies first, then pointers (no FK, but keep it tidy).
  DELETE FROM __SCHEMA__.wh_event_body eb
  USING __SCHEMA__.wh_event_store es
  WHERE eb.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version <= p_through_version;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;

  -- Migration 093 (#80-F): refold the sealed epochs the truncation touched (post-delete world),
  -- then bump the generation ONCE — consumers reset their seals and re-verify instead of
  -- alarming on deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT 'closed'::TEXT, v_deleted;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- reclassify_events_ephemeral — re-created VERBATIM from 087 + (#80-F) inline epoch refold +
-- generation bump. Reclassified rows leave the fold (flags & 8 excluded), which is the same
-- legitimate-mutation shape as a truncation.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.reclassify_events_ephemeral(p_event_types TEXT[])
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  streams_blocked BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
  v_names TEXT[];
  v_events BIGINT := 0;
  v_streams BIGINT := 0;
  v_blocked BIGINT := 0;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
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

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows the
  -- stamp below will remove from the fold — same predicates as the UPDATE, evaluated BEFORE it.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, c_zero) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.event_type = ANY(v_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(v_names))
          AND (b.flags & c_flag_ephemeral) = 0
      )
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Full split (078): bodies live in wh_event_body from birth for BOTH classes, so reclassification is
  -- purely a flags stamp — there is no inline body to offload or null out anymore.
  WITH reclassified AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = es.flags | c_flag_ephemeral
    WHERE es.event_type = ANY(v_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(v_names))
          AND (b.flags & c_flag_ephemeral) = 0
      )
    RETURNING es.event_id, es.stream_id, es.event_type, es.scope, es.origin_service_id
  ),
  -- Migration 087 (A1c): the reclassified rows leave the audited digest set (flags & 8 excluded),
  -- so XOR them back out of wh_stream_digests. Reads the pre-statement bucket snapshot (data-
  -- modifying CTEs can't see each other's table effects) — safe: buckets pre-exist and only this
  -- statement touches them. A missing bucket is a no-op; the full-sweep verification heals drift.
  digest_subtract AS (
    UPDATE __SCHEMA__.wh_stream_digests d
    SET digest_lo = d.digest_lo # s.digest_lo,
        digest_hi = d.digest_hi # s.digest_hi,
        event_count = d.event_count - s.event_count,
        updated_at = NOW()
    FROM (
      SELECT COALESCE(r.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
             COALESCE(r.scope ->> 't', '') AS scope_tenant,
             r.event_type,
             r.stream_id,
             bit_xor(hashtextextended(r.event_id::text, 0)) AS digest_lo,
             bit_xor(hashtextextended(r.event_id::text, 1)) AS digest_hi,
             COUNT(*)::int AS event_count
      FROM reclassified r
      LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = r.event_id
      WHERE COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
      GROUP BY 1, 2, 3, 4
    ) s
    WHERE d.origin_service_id = s.origin_service_id
      AND d.scope_tenant = s.scope_tenant
      AND d.event_type = s.event_type
      AND d.stream_id = s.stream_id
  )
  SELECT COUNT(*), COUNT(DISTINCT stream_id) INTO v_events, v_streams FROM reclassified;

  -- Migration 087: drop buckets the subtraction emptied (separate statement so it sees the
  -- digest_subtract UPDATE; scoped to the reclassified type names).
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.event_type = ANY(v_names) AND d.event_count <= 0;

  -- Migration 093 (#80-F): refold the sealed epochs the reclassification touched (the stamped
  -- rows now fall out of the fold), then bump the generation ONCE.
  IF v_lanes IS NOT NULL AND v_events > 0 THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT v_events, v_streams, v_blocked;
END;
$$ LANGUAGE plpgsql;
