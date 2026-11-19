-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 490_IntegritySealReading.sql
-- Date: 2026-11-17
-- Description: The seal gauges read how far each lane is sealed, how long ago its seal last moved, and how much of the
--              recent sealed history the re-check covered (#2365).
--
--              The epoch frontier (092) says per lane which epochs are sealed and when the frontier last advanced, and the
--              seal record (450) says when each epoch was sealed and last re-checked. Nothing read them for a gauge, so a
--              lane whose sealing had stopped, or a re-check that had fallen behind its interval, was visible only by
--              querying the tables. wh_integrity_seal_reading returns one row per lane: the sequence below which every
--              epoch is sealed and the seconds since the frontier last advanced, with the re-check's coverage (the
--              share of the epochs sealed within the lookback that were re-checked within the interval) on every row.
--              With no lane, one row with a null lane carries the coverage.
--
--              Cost: one pass over wh_digest_epoch_frontiers (one row per lane) and one over wh_digest_epoch_seals rows
--              sealed within the lookback (one row per epoch, never per event); read by the maintainer duty's holder
--              once per maintenance tick.
--
--              Number 490: the range assigned to this change; the integrator renumbers.
--
-- Reversal: reversible (the function goes; the gauges report nothing)
-- Dependencies: 092 (wh_digest_epoch_frontiers), 450 (wh_digest_epoch_seals)
-- Objects: wh_integrity_seal_reading

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_seal_reading');

-- The seal reading: per lane, the sealed-through sequence and the frontier's age; the seal re-check's coverage on every row.
-- <docs>resilience/stream-integrity#integrity-instruments</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySealReadingSqlTests.cs:TheReading_IsEachLanesFrontierAndAge_AndTheRecheckCoverageAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySealReadingSqlTests.cs:WithNoLane_OneRowCarriesTheCoverage_AndNothingSealedInTheLookbackHasNoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_seal_reading(
  p_lookback_seconds      BIGINT,
  p_recheck_every_seconds BIGINT,
  p_now                   TIMESTAMPTZ
)
RETURNS TABLE (
  origin_service_id UUID,
  sealed_through    BIGINT,
  age_seconds       DOUBLE PRECISION,
  recheck_coverage  DOUBLE PRECISION
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
  )
  SELECT f.origin_service_id,
         (f.closed_through_epoch + 1) * f.epoch_width,
         GREATEST(0, EXTRACT(EPOCH FROM (p_now - f.updated_at)))::DOUBLE PRECISION,
         c.ratio
  FROM coverage c
  LEFT JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON TRUE
  ORDER BY 1;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_seal_reading(BIGINT, BIGINT, TIMESTAMPTZ) IS
'490 (#2365): the seal gauges'' reading. One row per lane of wh_digest_epoch_frontiers: the sequence below which every epoch is sealed ((closed_through_epoch + 1) * epoch_width; 0 while nothing is sealed) and the seconds since the frontier last advanced; and on every row the share of the epochs sealed within p_lookback_seconds that were re-checked within p_recheck_every_seconds (NULL when none was sealed within the lookback). With no lane, one row with a NULL lane carries the share. Read by the maintainer duty''s holder once per maintenance tick.';
