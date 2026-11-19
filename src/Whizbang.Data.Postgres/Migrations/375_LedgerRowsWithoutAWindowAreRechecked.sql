-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 375_LedgerRowsWithoutAWindowAreRechecked.sql
-- Date: 2026-11-09
-- Description: The idle ledger re-check also re-checks per-stream ledger rows with no window, over the whole history,
--              and the paced drain holds such a row written before this release until a comparison confirms it (#2208).
--
--              A row with no window (window_from and window_until NULL) was compared over the whole history. Many such
--              rows were written by older releases under rules since fixed, most of them while this service held
--              nothing of the stream, and the consumer now holds every event. The idle re-check (348) claimed only
--              windowed rows, so these were never compared again, and the paced drain kept claiming them and asking
--              origins for events already held.
--
--              1. wh_integrity_claim_ledger_recheck: re-created from 348 without the stamped-window condition. A row
--                 with no window is claimed like any other and comes back without one; the re-check worker then asks
--                 its origin for a whole-history answer (unwindowed, from the origin's recompute), which the comparison
--                 clears where the consumer holds everything and reports again where it does not. Still never the
--                 synthetic type lane, still bounded, oldest report first, SKIP LOCKED, and it spends nothing.
--              2. wh_integrity_hold_unconfirmed_rows (new), run once below: marks every per-stream deficit row with no
--                 window that this database holds now as answered (answered_at, 320). An answered row is not claimed
--                 by the paced drain until a comparison sights it again (wh_integrity_try_begin_report clears
--                 answered_at) or a heal deletes it, which is exactly the confirmation these rows lack. Rows reported
--                 after the upgrade were compared under the current rule and are not held. Rows already answered,
--                 marked unhealable or exhausted are left as they are: the drain does not claim them either.
--
--              The hold is a migration step, not a maintenance step, as in 357: maintenance is idle-gated, and a busy
--              service is where the needless repairs cost most. It runs once per database (a wh_settings marker,
--              README rule 6) and is bounded by the ledger (090 caps it): one pass, touching only the rows it holds.
--              A held row counts on the answered gauge (wh_integrity_ledger_gauges.answered_buckets) until it is
--              compared again, which is what it is: waiting for the next comparison.
--
--              No index: the claim reads the same bounded ledger as 348, once a tick, on an idle service.
--
--              Number 375: 360 to 374 are taken on develop or by changes in flight.
--
-- Dependencies: 090 (wh_integrity_ledger), 097 (window_from, window_until), 249 (unhealable_at), 320 (answered_at,
--               exhausted_at), 348 (rechecked_at, wh_integrity_claim_ledger_recheck, copied with the delta above)
-- Objects: wh_integrity_claim_ledger_recheck, wh_integrity_hold_unconfirmed_rows; wh_settings row
--          integrity_hold_unconfirmed_rows_version
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

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

-- Once per database: the per-stream whole-history deficit rows written before this release wait for a comparison.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_hold_unconfirmed_rows');
-- <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnconfirmedHoldSqlTests.cs:Hold_ALegacyRowWithNoWindow_WaitsForAComparisonBeforeTheDrainRepairsIt_OnceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_hold_unconfirmed_rows()
RETURNS INTEGER AS $$
DECLARE
  v_held INTEGER;
BEGIN
  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
  VALUES ('integrity_hold_unconfirmed_rows_version', '1', 'integer',
          'Gates the one-time hold of whole-history ledger rows written before the comparison rule they were reported under was fixed (#2208, migration 375).',
          NOW(), 'migration:375_LedgerRowsWithoutAWindowAreRechecked')
  ON CONFLICT (setting_key) DO NOTHING;
  IF NOT FOUND THEN
    RETURN 0;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET answered_at  = NOW(),
      last_touched = NOW()
  WHERE l.stream_id <> __EMPTY_UUID__::uuid
    AND l.is_deficit
    AND l.window_from IS NULL
    AND l.answered_at IS NULL
    AND l.unhealable_at IS NULL
    AND l.exhausted_at IS NULL;
  GET DIAGNOSTICS v_held = ROW_COUNT;
  RETURN v_held;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_hold_unconfirmed_rows() IS
'375 (#2208): once per database (the integrity_hold_unconfirmed_rows_version setting), marks the per-stream deficit ledger rows with no window as answered, so the paced drain waits for a comparison to sight them again (or a heal to delete them) before repairing them. Returns the rows held; 0 once the marker is set.';

SELECT __SCHEMA__.wh_integrity_hold_unconfirmed_rows();
