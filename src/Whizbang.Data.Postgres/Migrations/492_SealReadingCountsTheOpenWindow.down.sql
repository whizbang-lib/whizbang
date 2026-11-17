-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 492_SealReadingCountsTheOpenWindow.sql (#2365): 490's definition returns, without the open window, the
-- origin generation or the carried buckets.

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
