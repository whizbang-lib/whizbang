-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 332_DigestWindowsReadOnlyTheirWindow.sql
-- Date: 2026-11-05
-- Description: A windowed digest reads the rows of its window, not the whole lane (#2080).
--
--              compute_type_digests_epoch_window took the lane's sequence from a CASE expression (commit_sequence for
--              the local lane, origin_commit_sequence for an origin's lane) and filtered the window afterwards. No index
--              can serve a range on a CASE, so every call read the whole lane and the cost grew with the store rather
--              than the window. The lane is now two branches of a UNION ALL, one per lane, each with the window as a
--              direct range on its own column: the local lane on commit_sequence (idx_event_store_commit_sequence, 046),
--              an origin's lane on origin_commit_sequence (idx_event_store_origin_lane, 155). Exactly one branch
--              is live per call; the other is pruned by its constant predicate on p_origin_lane. Everything else is
--              216's definition verbatim. No new index: both columns are already indexed.
--
--              Number 332: 330 is the highest on develop and 331 is taken by this change set's ledger prune.
--
-- Dependencies: 046 (idx_event_store_commit_sequence), 155 (the origin lane index), 216 (the definition copied)
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- <docs>resilience/stream-integrity#windowed-manifests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestLaneReadTests.cs:TypeDigestWindow_ReadsTheWindow_NotTheLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch_window(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_event_types    TEXT[],        -- NULL = all types
  p_since          BIGINT,        -- inclusive
  p_until          BIGINT,        -- exclusive, pre-clamped at the settled max
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_lane     UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier BIGINT := -1;
  v_width    BIGINT;
  v_e_lo     BIGINT;
  v_e_hi     BIGINT;
  v_types    TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_e_lo := 0;
    v_e_hi := -1;   -- no sealed epochs: the whole window folds live
  ELSE
    -- Sealed epochs fully inside [p_since, p_until): first with start >= since, last whose
    -- exclusive end fits, both clamped to the frontier. An empty run (e_lo > e_hi) = all live.
    v_e_lo := (p_since + v_width - 1) / v_width;
    v_e_hi := LEAST(p_until / v_width - 1, v_frontier);
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id BETWEEN v_e_lo AND v_e_hi
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  lane AS (
    -- 332 (#2080): one branch per lane, each with the window as a direct range on its own indexed column, so the read
    -- is the window's rows (idx_event_store_commit_sequence for the local lane, the 155 lane index for an origin's)
    -- instead of the whole lane filtered through a CASE no index can serve. Exactly one branch is live per call.
    SELECT es.*, es.commit_sequence AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NULL AND es.origin_service_id IS NULL
      AND es.commit_sequence >= p_since AND es.commit_sequence < p_until
    UNION ALL
    SELECT es.*, es.origin_commit_sequence AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NOT NULL AND es.origin_service_id = p_origin_lane
      AND es.origin_commit_sequence >= p_since AND es.origin_commit_sequence < p_until
  ),
  live_fold AS (
    -- The window minus the sealed run: the low fringe [since, e_lo*width) and the tail
    -- [(e_hi+1)*width, until), or the whole window when no epoch fits (e_lo > e_hi).
    SELECT COALESCE(l.scope ->> 't', '') AS b_tenant, l.event_type AS b_type,
           bit_xor(hashtextextended(l.event_id::text, 0)) AS lo,
           bit_xor(hashtextextended(l.event_id::text, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM lane l
    WHERE l.lane_seq IS NOT NULL
      AND l.lane_seq >= p_since AND l.lane_seq < p_until
      AND (v_e_lo > v_e_hi
           OR l.lane_seq < v_e_lo * v_width
           OR l.lane_seq >= (v_e_hi + 1) * v_width)
      AND (v_types IS NULL OR l.event_type = ANY(v_types))
      AND COALESCE(l.flags, 0) & 8 = 0
      AND COALESCE(l.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      AND split_part(l.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)  -- 216 (#1492)
      AND l.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM live_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_type_digests_epoch_window(UUID, TEXT[], BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'Windowed type-level fold [p_since, p_until): fully-contained sealed epochs answer from their seal, fringes fold live. Caller pre-clamps p_until at integrity_settled_max. 214: the live fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 332 (#2080): the window is a direct range on the lane''s indexed sequence column, one branch per lane, so a call reads its window, not the lane.';
