-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 348_IdleLedgerRecheck.sql
-- Date: 2026-11-07
-- Description: An idle service re-checks stale ledger rows itself instead of waiting for the daily audit (#2115).
--
--              A ledger row clears only when its bucket is compared again, and the routine audit compares a bucket at
--              most once a day, so rows written under an older compare rule (an upgrade that changed the rule)
--              lingered for days, and with automatic repair on, the paced drain turned them into repair requests.
--              The idle re-check claims the stalest rows here, oldest report first, and asks each row's owner for a
--              stream-level answer over the row's window, which the comparison clears or reports again.
--
--              1. wh_integrity_ledger.rechecked_at: when the idle re-check last claimed the row. A row is claimed
--                 again only once that, like its last report, is older than the re-check threshold, so a re-check
--                 that finds the bucket still divergent does not ask again every tick. last_touched cannot serve:
--                 every report, drain claim and pending mark moves it.
--              2. wh_integrity_claim_ledger_recheck: the claim. Rows last reported before the threshold, not claimed
--                 since it, stamped with their compared window, of an origin that can be asked, never the synthetic
--                 type lane; oldest report first, bounded, SKIP LOCKED against a concurrent claimer. It stamps
--                 rechecked_at and nothing else: no attempt is spent and no repair is asked.
--
--              No index: the ledger is bounded by the repair drain's backlog cap and the audit's report caps, and the
--              claim runs on an idle service at most once a tick.
--
--              Number 348: 345 is the highest on develop, 346 is taken by a change in flight, and 347 is this
--              change set's windowed digest read.
--
-- Dependencies: 090 (wh_integrity_ledger), 097 (window_from, window_until)
-- Objects: wh_integrity_ledger (rechecked_at), wh_integrity_claim_ledger_recheck
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS rechecked_at TIMESTAMPTZ;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.rechecked_at IS
'348 (#2115): when the idle ledger re-check last claimed this row and asked its owner to answer its bucket again. Claimed again only once this is older than the re-check threshold. NULL = never re-checked.';

-- <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerRecheckSqlTests.cs:Claim_TakesTheStalestRowsFirst_BoundedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerRecheckSqlTests.cs:Claim_ARowItClaimed_IsNotClaimedAgainUntilTheThresholdPassesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerRecheckSqlTests.cs:Claim_SkipsRowsItCannotAsk_AndSpendsNothingAsync</tests>
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
      AND l.window_from IS NOT NULL AND l.window_until IS NOT NULL
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
'348 (#2115): claims up to p_limit ledger rows for the idle re-check, oldest report first: rows of p_origin_ids last reported before p_reported_before and not re-checked since, with a stamped window, never the type lane. Stamps rechecked_at = p_now and nothing else (no attempt, no repair). SKIP LOCKED against a concurrent claimer.';
