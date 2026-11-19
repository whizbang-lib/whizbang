-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 492_SealReadingCountsTheOpenWindow.sql
-- Date: 2026-11-17
-- Description: The seal reading also counts each lane's open window, reads the origin generation and counts the stream
--              buckets carrying a cut's digest (#2365).
--
--              wh_integrity_seal_reading (490) said how far each lane is sealed and how long ago its frontier moved, but
--              not how much lies above the frontier waiting to be sealed, and the gauges had no reading of this
--              service's origin generation or of how many stream digest buckets still carry what a cut removed (460).
--              The function gains, per lane, the events above its frontier, counted up to p_cap (open_window_capped says
--              the count stopped there), and on every row the origin generation and the carried buckets, counted up to
--              p_cap the same way.
--
--              Cost: per lane, at most p_cap + 1 index entries: the local lane reads idx_event_store_commit_sequence from
--              the frontier up and keeps the rows with no origin; an origin's lane reads idx_event_store_origin_lane (155)
--              from the frontier up. The carried count reads at most p_cap + 1 entries of idx_stream_digests_carried_at
--              (460), whose predicate it repeats. One settings row for the generation. Read by the maintainer duty's
--              holder once per maintenance tick.
--
--              Number 492: the range assigned to this change; the integrator renumbers.
--
-- Reversal: reversible (490's definition returns; the three gauges report nothing)
-- Dependencies: 490 (wh_integrity_seal_reading), 093 (integrity_origin_generation), 155 (idx_event_store_origin_lane),
--               460 (carried columns, idx_stream_digests_carried_at)
-- Objects: wh_integrity_seal_reading

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_seal_reading');

-- The seal reading: per lane, the sealed-through sequence, the frontier's age and the events above it (bounded); on every
-- row the seal re-check's coverage, the origin generation and the carried buckets (bounded).
-- <docs>resilience/stream-integrity#integrity-instruments</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySealReadingSqlTests.cs:TheReading_IsEachLanesFrontierAndAge_AndTheRecheckCoverageAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySealReadingSqlTests.cs:WithNoLane_OneRowCarriesTheCoverage_AndNothingSealedInTheLookbackHasNoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySealReadingSqlTests.cs:TheReading_CountsEachLanesOpenWindow_ReadsTheGeneration_AndCountsCarriedBucketsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_seal_reading(
  p_lookback_seconds      BIGINT,
  p_recheck_every_seconds BIGINT,
  p_now                   TIMESTAMPTZ,
  p_cap                   INTEGER
)
RETURNS TABLE (
  origin_service_id  UUID,
  sealed_through     BIGINT,
  age_seconds        DOUBLE PRECISION,
  open_window_events BIGINT,
  open_window_capped BOOLEAN,
  recheck_coverage   DOUBLE PRECISION,
  origin_generation  BIGINT,
  carried_streams    BIGINT,
  carried_capped     BOOLEAN
)
LANGUAGE sql
STABLE
AS $$
  WITH coverage AS (
    SELECT CASE WHEN COUNT(*) = 0 THEN NULL
                ELSE (COUNT(*) FILTER (
                        WHERE s.rechecked_at >= p_now - make_interval(secs => p_recheck_every_seconds::DOUBLE PRECISION)
                      ))::DOUBLE PRECISION / COUNT(*)
           END AS ratio
    FROM __SCHEMA__.wh_digest_epoch_seals s
    WHERE s.sealed_at >= p_now - make_interval(secs => p_lookback_seconds::DOUBLE PRECISION)
  ),
  service AS (
    SELECT (SELECT g.setting_value::BIGINT FROM __SCHEMA__.wh_settings g
            WHERE g.setting_key = 'integrity_origin_generation') AS generation,
           (SELECT COUNT(*) FROM (
              SELECT 1 FROM __SCHEMA__.wh_stream_digests d
              WHERE d.carried_at IS NOT NULL AND d.carried_lo IS NOT NULL
              LIMIT GREATEST(p_cap, 1)::BIGINT + 1) x) AS carried
  ),
  lanes AS (
    SELECT f.origin_service_id AS lane, (f.closed_through_epoch + 1) * f.epoch_width AS through, f.updated_at
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
  )
  SELECT l.lane,
         l.through,
         GREATEST(0, EXTRACT(EPOCH FROM (p_now - l.updated_at)))::DOUBLE PRECISION,
         LEAST(w.n, GREATEST(p_cap, 1)::BIGINT),
         w.n > GREATEST(p_cap, 1),
         c.ratio,
         v.generation,
         LEAST(v.carried, GREATEST(p_cap, 1)::BIGINT),
         v.carried > GREATEST(p_cap, 1)
  FROM coverage c
  CROSS JOIN service v
  LEFT JOIN lanes l ON TRUE
  LEFT JOIN LATERAL (
    SELECT COUNT(*) AS n FROM (
      SELECT 1 FROM __SCHEMA__.wh_event_store es
      WHERE l.lane = __EMPTY_UUID__::uuid AND es.origin_service_id IS NULL AND es.commit_sequence >= l.through
      UNION ALL
      SELECT 1 FROM __SCHEMA__.wh_event_store es
      WHERE l.lane <> __EMPTY_UUID__::uuid AND es.origin_service_id = l.lane AND es.origin_commit_sequence >= l.through
      LIMIT GREATEST(p_cap, 1)::BIGINT + 1) y
  ) w ON l.lane IS NOT NULL
  ORDER BY 1;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_seal_reading(BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'492 (#2365): the seal gauges'' reading. One row per lane of wh_digest_epoch_frontiers: the sequence below which every epoch is sealed, the seconds since the frontier last advanced, and the lane''s events at or above that sequence counted up to p_cap (open_window_capped when the count stopped there; the local lane by commit_sequence among events with no origin, an origin''s lane by origin_commit_sequence). On every row: the share of the epochs sealed within p_lookback_seconds re-checked within p_recheck_every_seconds (NULL when none), this service''s origin generation, and the stream digest buckets carrying a cut''s digest counted up to p_cap (carried_capped). With no lane, one row with a NULL lane carries the service-wide values. Read by the maintainer duty''s holder once per maintenance tick.';
