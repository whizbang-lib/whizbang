-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 340_DigestWindowsReadTheirTypes.sql
-- Date: 2026-11-06
-- Description: A type-filtered windowed stream-digest read costs in proportion to the rows it returns (#2107).
--
--              After 332 (#2080) the read was bound by its window, but it was an auto-prepared statement, and the
--              generic plan it settled into cannot see how wide the window is or which types are asked for: it
--              estimates the range as narrow, finds the window's rows through idx_event_store_commit_sequence, and
--              filters the requested types on the heap. A wide window therefore read every event in it, of any type,
--              to return a few hundred.
--
--              The read is now this function, which plans each call against its own window and types
--              (plan_cache_mode = force_custom_plan, the setting 157 established for the claim). With the real
--              window and the real types the planner reads a sparse type through an index on the type
--              (idx_event_store_type_commit_sequence, built concurrently after the migrations by #2092, or
--              idx_event_store_type_tenant from 195) and a dense one through the window's commit range, whichever
--              touches fewer pages. Before the #2092 index is valid the read answers the same through the others.
--
--              The page is also cheaper to assemble: it took one page of whole streams by joining the window to the
--              picked stream ids, which the planner ran as a nested loop over the materialized window (page size times
--              window rows). It now keeps the window's rows up to the last picked stream id, one pass and no join.
--              Same answer: the first p_max_streams distinct streams after p_resume_after, every event of each.
--
--              No new index. Number 340: 335 is the highest on develop and 336-339 are taken by an open change.
--
-- Dependencies: 046 (idx_event_store_commit_sequence), 155 (idx_event_store_origin_lane), 195 (idx_event_store_type_tenant),
--               normalize_event_type
-- Objects: wh_compute_stream_digests_window
-- Constants: the double-underscore tokens in this file (for example __DIGEST_EXEMPT_TYPES__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- <docs>resilience/stream-integrity#windowed-manifests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestTypeIndexTests.cs:TypeFilteredWindow_ReadsThroughAnIndexConditionOnTheType_UnderAGenericPlanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestTypeIndexTests.cs:TheRead_PlansEachCallAgainstItsOwnWindow_WhateverTheCallersPlanModeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestLaneReadTests.cs:ManifestStreamWindow_ReadsTheWindow_NotTheLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochWindowedDigestSqlTests.cs:StreamWindow_OnlyEventsInsideTheWindowContributeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_compute_stream_digests_window(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_event_types    TEXT[],        -- NULL = all types
  p_since          BIGINT,        -- inclusive
  p_until          BIGINT,        -- exclusive, pre-clamped at the settled max
  p_resume_after   UUID,          -- page start: only streams above this id (NULL = from the first)
  p_max_streams    INTEGER,       -- whole streams on this page
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, stream_id UUID, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_types   TEXT[];
  v_settled TIMESTAMPTZ := p_now - make_interval(secs => p_settle_seconds);
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  RETURN QUERY
  WITH win AS (
    -- One branch per lane (332), each with the window as a direct range on its own indexed column. Exactly one is live
    -- per call; under the custom plan the other is pruned and the type list is a constant the planner can index.
    SELECT es.event_id AS w_event, es.stream_id AS w_stream, es.event_type::text AS w_type,
           COALESCE(es.scope ->> 't', '') AS w_tenant
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NULL AND es.origin_service_id IS NULL
      AND es.commit_sequence >= p_since AND es.commit_sequence < p_until
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
      AND es.created_at < v_settled
      AND (p_resume_after IS NULL OR es.stream_id > p_resume_after)
    UNION ALL
    SELECT es.event_id, es.stream_id, es.event_type::text, COALESCE(es.scope ->> 't', '')
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NOT NULL AND es.origin_service_id = p_origin_lane
      AND es.origin_commit_sequence >= p_since AND es.origin_commit_sequence < p_until
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
      AND es.created_at < v_settled
      AND (p_resume_after IS NULL OR es.stream_id > p_resume_after)
  ),
  last_picked AS (
    -- The page's last stream: the p_max_streams-th distinct stream id in order. Every row up to it is on the page.
    SELECT p.w_stream
    FROM (SELECT DISTINCT w.w_stream FROM win w ORDER BY w.w_stream LIMIT p_max_streams) p
    ORDER BY p.w_stream DESC
    LIMIT 1
  )
  SELECT w.w_tenant, w.w_type, w.w_stream,
         bit_xor(hashtextextended(w.w_event::text, 0)),
         bit_xor(hashtextextended(w.w_event::text, 1)),
         COUNT(*)::int
  FROM win w
  WHERE w.w_stream <= (SELECT lp.w_stream FROM last_picked lp)
  GROUP BY 1, 2, 3
  ORDER BY 3, 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_compute_stream_digests_window(UUID, TEXT[], BIGINT, BIGINT, UUID, INTEGER, TIMESTAMPTZ, INTEGER) IS
'Windowed stream-level fold [p_since, p_until), one page of whole streams after p_resume_after (#2107). Caller pre-clamps p_until at integrity_settled_max and asks for one stream past its page bound as the sentinel. Plans each call against its own window and types (force_custom_plan), so a sparse type is read through an index on the type rather than filtered from every event in the window. Leaves out ephemeral (flags & 8) and at-most-once (flags & 32) events and audit records.';
