-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 392_AnsweredRowsAreRecheckedFirst.sql (#2259): wh_integrity_claim_ledger_recheck as 375 defined it, with
-- no answered lane, and the answered lane's partial index dropped. 392 stored no data of its own: the rechecked_at values
-- its answered lane stamped are ordinary re-check stamps, which the restored claim reads as it always did.

DROP INDEX IF EXISTS __SCHEMA__.idx_integrity_ledger_answered_unrechecked;

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_claim_ledger_recheck: 375's definition, whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_claim_ledger_recheck');

-- Re-created from 348; a row with no stamped window is claimed too, and returned with NULL bounds.
-- <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
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
BEGIN
  RETURN QUERY
  WITH stale AS (
    SELECT l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id
    FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.origin_service_id = ANY(p_origin_ids)
      AND l.stream_id <> __EMPTY_UUID__::uuid
      AND COALESCE(l.last_reported_at, l.first_seen_at) < p_reported_before
      AND (l.rechecked_at IS NULL OR l.rechecked_at < p_reported_before)
    ORDER BY COALESCE(l.last_reported_at, l.first_seen_at) ASC, l.first_seen_at ASC
    LIMIT p_limit
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
'375 (#2208): claims up to p_limit ledger rows for the idle re-check, oldest report first: rows of p_origin_ids last reported before p_reported_before and not re-checked since, never the type lane. A row with no stamped window comes back with NULL bounds and is re-checked over the whole history. Stamps rechecked_at = p_now and nothing else (no attempt, no repair). SKIP LOCKED against a concurrent claimer.';
