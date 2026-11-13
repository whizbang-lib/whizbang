-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 404_LedgerGaugesSplitBucketsByKind.sql
-- Date: 2026-11-11
-- Description: The stream-integrity ledger gauges split the unhealed buckets by kind (#2260).
--
--              unhealed_buckets counts every ledger row. A comparison writes one for every divergence it reports,
--              and only a deficit (this store holds fewer events than the origin) is one a redelivery heals: a bucket
--              whose two sides hold the same number of events with different digests (the same events under
--              different ids) and one where this store holds more events than the origin both stay, and both read as
--              missing data. A service holding many of the second kind showed a backlog that redelivery could never
--              heal, and nothing said so.
--
--              The comparison already records each row's kind (251: is_deficit) and its size (320: deficit_events,
--              the origin's events minus this store's), so the kinds are read from the row:
--                - deficit:          is_deficit;
--                - digest_mismatch:  not is_deficit, with a size of zero;
--                - surplus:          not is_deficit, with a negative size;
--                - unknown:          anything else (is_deficit NULL: a row written before kinds were recorded, or
--                                    one the single-key report created or moved; or a size never recorded). The next
--                                    comparison that sights the bucket states its kind.
--              The four are exclusive and add up to unhealed_buckets.
--
--              1. wh_integrity_ledger_gauges: re-created from 373 with four columns added at the end (deficit_buckets,
--                 surplus_buckets, digest_mismatch_buckets, unknown_kind_buckets), so a caller that names the earlier
--                 columns reads them unchanged. unhealed_deficit_events is unchanged: the deficits' events only.
--
--              No index: four more FILTER aggregates in the one pass over the ledger the function already makes (090
--              caps the ledger), so the read stays one sequential scan and one aggregate.
--
--              Number 400: 397 is the highest on develop, and the integrity bundle's changes take 398 to 403 in order.
--
-- Reversal: reversible (no data is stored; 373's definition restores the function)
-- Dependencies: 251 (is_deficit), 320 (deficit_events), 373 (wh_integrity_ledger_gauges, copied with the delta above)
-- Objects: wh_integrity_ledger_gauges

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests); the result gains columns.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_gauges');

-- 373's gauges plus the unhealed buckets of each kind (#2260), in the same pass.
-- <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBucketKindGaugesSqlTests.cs:Summary_SplitsTheUnhealedBucketsByKind_AndTheKindsAddUpToTheTotalAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBucketKindGaugesSqlTests.cs:Summary_OfAnEmptyLedger_ReadsZeroForEveryKindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerHealRemainingGaugesSqlTests.cs:Summary_SumsTheDeficitOfRepairableBuckets_AndCountsTheUnhealableOnesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Summary_CountsBucketsAwaitingAnAnswer_AnsweredBuckets_AndTheIntegrityDeliveriesWaitingAsync</tests>
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
  unhealable_buckets       BIGINT,
  deficit_buckets          BIGINT,
  surplus_buckets          BIGINT,
  digest_mismatch_buckets  BIGINT,
  unknown_kind_buckets     BIGINT
)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  SELECT
    l.unhealed, l.exhausted, l.oldest, l.pending, l.answered, d.pending, d.oldest, l.deficit_events, l.unhealable,
    l.deficits, l.surpluses, l.mismatches, l.unhealed - l.deficits - l.surpluses - l.mismatches
  FROM (
    SELECT
      COUNT(*)::BIGINT AS unhealed,
      COUNT(*) FILTER (WHERE x.repair_attempts >= p_max_attempts OR x.exhausted_at IS NOT NULL)::BIGINT AS exhausted,
      COALESCE(EXTRACT(EPOCH FROM (NOW() - MIN(x.first_seen_at))), 0)::DOUBLE PRECISION AS oldest,
      COUNT(*) FILTER (WHERE x.answer_pending_until > NOW())::BIGINT AS pending,
      COUNT(*) FILTER (WHERE x.answered_at IS NOT NULL)::BIGINT AS answered,
      COALESCE(SUM(x.deficit_events) FILTER (
        WHERE x.is_deficit AND x.unhealable_at IS NULL AND x.exhausted_at IS NULL), 0)::BIGINT AS deficit_events,
      COUNT(*) FILTER (WHERE x.unhealable_at IS NOT NULL)::BIGINT AS unhealable,
      -- 400 (#2260): the kinds; whatever none of the three names is unknown.
      COUNT(*) FILTER (WHERE x.is_deficit)::BIGINT AS deficits,
      COUNT(*) FILTER (WHERE NOT x.is_deficit AND x.deficit_events < 0)::BIGINT AS surpluses,
      COUNT(*) FILTER (WHERE NOT x.is_deficit AND x.deficit_events = 0)::BIGINT AS mismatches
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
'Ledger as a gauge: unhealed buckets, how many have exhausted their repair budget, the age of the oldest, the buckets awaiting the answer to a request and those answered and waiting for the next comparison (320); the integrity deliveries waiting for the maintainer with the age of the oldest (#2035); the deficit events left to heal in buckets repair can still act on, and the unhealable buckets (373, #2203); and the unhealed buckets by kind: deficit, surplus, digest mismatch and unknown, which add up to the total (400, #2260).';
