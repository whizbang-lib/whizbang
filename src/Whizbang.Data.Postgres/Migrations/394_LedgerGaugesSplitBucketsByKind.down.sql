-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 394_LedgerGaugesSplitBucketsByKind.sql (#2260): wh_integrity_ledger_gauges as its last words before 394
-- defined it (373), without the four kind columns. 394 stored no data, so the reverse loses none.

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_ledger_gauges: 373's definition, whole.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_gauges');

-- 320's gauges plus the deficit events left to heal and the unhealable buckets (#2203), in the same pass.
-- <docs>resilience/stream-integrity#integrity-gauges</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBucketKindGaugesSqlTests.cs:TheReverse_RestoresTheGaugesWithoutTheKindsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerHealRemainingGaugesSqlTests.cs:Summary_SumsTheDeficitOfRepairableBuckets_AndCountsTheUnhealableOnesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerHealRemainingGaugesSqlTests.cs:Summary_ABucketWhoseDeficitIsUnknown_AddsNothing_AndAnEmptyLedgerReadsZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Summary_CountsBucketsAwaitingAnAnswer_AnsweredBuckets_AndTheIntegrityDeliveriesWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Summary_WithNoIntegrityDeliveries_ReportsNoneWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Claim_PastTheAttemptBudget_StopsForGood_AndCountsAsExhaustedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_gauges(p_max_attempts INTEGER)
RETURNS TABLE (
  unhealed_buckets         BIGINT,
  repair_exhausted         BIGINT,
  oldest_unhealed_secs     DOUBLE PRECISION,
  answer_pending_buckets   BIGINT,
  answered_buckets         BIGINT,
  deliveries_pending       BIGINT,
  oldest_delivery_secs     DOUBLE PRECISION,
  unhealed_deficit_events  BIGINT,
  unhealable_buckets       BIGINT
)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  SELECT
    l.unhealed, l.exhausted, l.oldest, l.pending, l.answered, d.pending, d.oldest, l.deficit_events, l.unhealable
  FROM (
    SELECT
      COUNT(*)::BIGINT AS unhealed,
      COUNT(*) FILTER (WHERE x.repair_attempts >= p_max_attempts OR x.exhausted_at IS NOT NULL)::BIGINT AS exhausted,
      COALESCE(EXTRACT(EPOCH FROM (NOW() - MIN(x.first_seen_at))), 0)::DOUBLE PRECISION AS oldest,
      COUNT(*) FILTER (WHERE x.answer_pending_until > NOW())::BIGINT AS pending,
      COUNT(*) FILTER (WHERE x.answered_at IS NOT NULL)::BIGINT AS answered,
      COALESCE(SUM(x.deficit_events) FILTER (
        WHERE x.is_deficit AND x.unhealable_at IS NULL AND x.exhausted_at IS NULL), 0)::BIGINT AS deficit_events,
      COUNT(*) FILTER (WHERE x.unhealable_at IS NOT NULL)::BIGINT AS unhealable
    FROM __SCHEMA__.wh_integrity_ledger x
  ) l
  CROSS JOIN (
    SELECT
      COUNT(*)::BIGINT AS pending,
      GREATEST(COALESCE(EXTRACT(EPOCH FROM (NOW() - MIN(q.queued_at))), 0), 0)::DOUBLE PRECISION AS oldest
    FROM __SCHEMA__.wh_integrity_deliveries q
  ) d;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_ledger_gauges(INTEGER) IS
'Ledger as a gauge: unhealed buckets, how many have exhausted their repair budget, the age of the oldest, the buckets awaiting the answer to a request and those answered and waiting for the next comparison (320); the integrity deliveries waiting for the maintainer with the age of the oldest (#2035); and the deficit events left to heal in buckets repair can still act on, and the unhealable buckets (373, #2203).';
