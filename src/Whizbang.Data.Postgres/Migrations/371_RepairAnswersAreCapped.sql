-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 371_RepairAnswersAreCapped.sql
-- Date: 2026-11-09
-- Description: The paced repair drain stops claiming while its cap's worth of ledger buckets await an answer (#2184).
--
--              A repair answer can land hours after its request (the answer timeout is three hours by default), so a
--              lower request rate does not bound the load late answers put on this service's store: answers to
--              requests sent over hours can arrive together. The drain now counts the buckets whose answer is still on
--              its way (answer_pending_until in the future, the same rows the repair_answer_pending_buckets gauge
--              counts) and claims no new row while the count is at Whizbang:StreamIntegrity:MaxOutstandingRepairAnswers.
--
--              1. wh_integrity_count_answer_pending: the buckets awaiting an answer at the drain's instant, read no
--                 further than the drain's cap.
--
--              Index: idx_integrity_ledger_answer_pending (answer_pending_until) WHERE answer_pending_until IS NOT NULL
--              answers the count from the index alone. It is built concurrently after the migrations by the framework
--              index maintenance step (ConcurrentIndexBuild.FrameworkIndexes), not here, so no write to the ledger
--              waits on the build; until it is built the bounded count reads the ledger, which 090 caps.
--
--              Number 371: 358 is the highest on develop and 360 to 370 are taken by changes in flight.
--
-- Dependencies: 090 (wh_integrity_ledger), 320 (answer_pending_until)
-- Objects: wh_integrity_count_answer_pending

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_count_answer_pending');

-- The ledger buckets whose answer is on its way at p_now, counted up to p_limit: the drain only needs to know whether
-- its cap is reached, so the read stops there.
-- <docs>resilience/stream-integrity#outstanding-repair-answers-are-capped</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerPendingCountSqlTests.cs:Count_TheBucketsAwaitingAnAnswer_AtTheDrainsInstantAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerPendingCountSqlTests.cs:Count_ReadsNoFurtherThanTheCapAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerPendingCountSqlTests.cs:Count_OnceTheIndexIsBuilt_IsAnsweredFromItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_count_answer_pending(
  p_now   TIMESTAMPTZ,
  p_limit INTEGER
) RETURNS INTEGER
LANGUAGE sql
STABLE
AS $$
  SELECT count(*)::INTEGER
  FROM (
    SELECT 1
    FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.answer_pending_until > p_now
    LIMIT p_limit
  ) s;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_count_answer_pending(TIMESTAMPTZ, INTEGER) IS
'371 (#2184): the integrity ledger buckets whose repair answer is on its way at p_now, counted up to p_limit. The paced repair drain claims no new row while the count is at its cap (MaxOutstandingRepairAnswers).';
