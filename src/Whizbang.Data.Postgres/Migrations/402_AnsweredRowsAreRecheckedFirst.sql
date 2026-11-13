-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 402_AnsweredRowsAreRecheckedFirst.sql
-- Date: 2026-11-11
-- Description: The idle ledger re-check claims rows whose repair was answered, and not re-checked since, before any
--              other row (#2259).
--
--              A repair answer stores the redelivered events and stamps the row answered (answered_at), but the bucket
--              clears only when it is compared again. The re-check (348, 375) walked rows oldest report first, and only
--              rows reported and re-checked before its threshold, so an answered row waited behind every unrelated
--              stale row, or for its threshold to pass, while its data had already healed. The open-buckets gauge then
--              stayed high for hours after the repair landed.
--
--              wh_integrity_claim_ledger_recheck: re-created from 375 with an answered lane taken first. A per-stream row
--              of an origin that can be asked, answered and not re-checked since the answer (rechecked_at IS NULL OR
--              rechecked_at < answered_at), is claimed whatever its report's age, oldest answer first; the rest of the
--              bound is filled from the stale rows exactly as 375 claims them. A claimed answered row is stamped
--              rechecked_at = GREATEST(p_now, answered_at), so an answer stamped by a clock ahead of this one cannot
--              keep the row in the lane. Once re-checked after its answer, a row waits like any other: the comparison
--              removes it, or reports it again (which clears answered_at), or the threshold passes.
--
--              idx_integrity_ledger_answered_unrechecked (new): a partial index over exactly the answered lane, so the
--              lane, read on every tick, reaches its rows without walking the origin's whole ledger. A row enters it
--              when an answer stamps it and leaves when the re-check claims it.
--
--              Number 402: 401 is the highest on develop, and the integrity bundle's changes take 402 to 407 in order.
--
-- Reversal: reversible (the index is dropped and the claim is restored from 375; rechecked_at values the answered lane
--           stamped are ordinary re-check stamps the restored claim reads as it always did)
-- Dependencies: 090 (wh_integrity_ledger), 320 (answered_at), 348 (rechecked_at), 375 (wh_integrity_claim_ledger_recheck,
--               copied with the delta above)
-- Objects: wh_integrity_claim_ledger_recheck, idx_integrity_ledger_answered_unrechecked
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

CREATE INDEX IF NOT EXISTS idx_integrity_ledger_answered_unrechecked
  ON __SCHEMA__.wh_integrity_ledger (origin_service_id, answered_at)
  WHERE answered_at IS NOT NULL
    AND (rechecked_at IS NULL OR rechecked_at < answered_at)
    AND stream_id <> __EMPTY_UUID__::uuid;

COMMENT ON INDEX __SCHEMA__.idx_integrity_ledger_answered_unrechecked IS
'402 (#2259): the idle re-check''s answered lane: per-stream ledger rows answered and not re-checked since the answer.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_claim_ledger_recheck');

-- Re-created from 375; answered rows not re-checked since their answer are claimed first.
-- <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnsweredRecheckSqlTests.cs:Claim_ARecentlyReportedRowWhoseRepairWasAnswered_IsClaimedBeforeOlderStaleRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnsweredRecheckSqlTests.cs:Claim_ARowReCheckedBeforeItsAnswer_IsClaimedAgainOnTheNextPassAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnsweredRecheckSqlTests.cs:Claim_AnAnsweredRowReCheckedSinceItsAnswer_WaitsLikeAnyOtherRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnsweredRecheckSqlTests.cs:Claim_AnsweredRows_ComeOldestAnswerFirst_AndOnlyForTheOriginsAskedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerRecheckSqlTests.cs:Claim_TakesTheStalestRowsFirst_BoundedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerRecheckSqlTests.cs:Claim_ARowItClaimed_IsNotClaimedAgainUntilTheThresholdPassesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerRecheckSqlTests.cs:Claim_SkipsRowsItCannotAsk_AndSpendsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerRecheckSqlTests.cs:Claim_ARowWithNoWindow_IsClaimed_AndComesBackWithoutOneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_claim_ledger_recheck(
  p_origin_ids      UUID[],
  p_reported_before TIMESTAMPTZ,
  p_now             TIMESTAMPTZ,
  p_limit           INTEGER
) RETURNS TABLE (
  origin_service_id UUID,
  tenant_scope      TEXT,
  event_type        TEXT,
  stream_id         UUID,
  window_from       BIGINT,
  window_until      BIGINT
)
LANGUAGE plpgsql
AS $$
DECLARE
  v_answered INTEGER;
BEGIN
  -- 402 (#2259): the answered lane first. Its predicate is idx_integrity_ledger_answered_unrechecked's.
  RETURN QUERY
  WITH answered AS (
    SELECT l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id
    FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.origin_service_id = ANY(p_origin_ids)
      AND l.stream_id <> __EMPTY_UUID__::uuid
      AND l.answered_at IS NOT NULL
      AND (l.rechecked_at IS NULL OR l.rechecked_at < l.answered_at)
    ORDER BY l.answered_at ASC, l.first_seen_at ASC
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET rechecked_at = GREATEST(p_now, l.answered_at)
  FROM answered s
  WHERE l.origin_service_id = s.origin_service_id
    AND l.tenant_scope = s.tenant_scope
    AND l.event_type = s.event_type
    AND l.stream_id = s.stream_id
  RETURNING l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id, l.window_from, l.window_until;

  GET DIAGNOSTICS v_answered = ROW_COUNT;
  IF v_answered >= p_limit THEN
    RETURN;
  END IF;

  -- The rest of the bound: the stale rows, as 375 claims them. A row the lane above just claimed is stamped past the
  -- threshold, so it is not claimed twice.
  RETURN QUERY
  WITH stale AS (
    SELECT l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id
    FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.origin_service_id = ANY(p_origin_ids)
      AND l.stream_id <> __EMPTY_UUID__::uuid
      AND COALESCE(l.last_reported_at, l.first_seen_at) < p_reported_before
      AND (l.rechecked_at IS NULL OR l.rechecked_at < p_reported_before)
    ORDER BY COALESCE(l.last_reported_at, l.first_seen_at) ASC, l.first_seen_at ASC
    LIMIT p_limit - v_answered
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET rechecked_at = p_now
  FROM stale s
  WHERE l.origin_service_id = s.origin_service_id
    AND l.tenant_scope = s.tenant_scope
    AND l.event_type = s.event_type
    AND l.stream_id = s.stream_id
  RETURNING l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id, l.window_from, l.window_until;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_claim_ledger_recheck(UUID[], TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
'402 (#2259): claims up to p_limit ledger rows for the idle re-check. First the rows of p_origin_ids answered and not re-checked since the answer, oldest answer first, whatever their report''s age; then rows last reported before p_reported_before and not re-checked since, oldest report first. Never the type lane. A row with no stamped window comes back with NULL bounds and is re-checked over the whole history. Stamps rechecked_at (p_now, or the answer''s time when that is later) and nothing else (no attempt, no repair). SKIP LOCKED against a concurrent claimer.';
