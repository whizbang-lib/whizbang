-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 251_RepairDrainClaimsOnlyDeficits.sql
-- Date: 2026-10-27
-- Description: The paced repair drain claims only the ledger rows a redelivery can repair (#1842).
--
--              A manifest comparison writes a ledger row for every divergence it reports: a deficit (this
--              store holds fewer events than the origin), an identity mismatch (equal counts, different
--              folds) and a local surplus (more events than the origin). Only a deficit is repairable: a
--              redelivery of a mismatch re-ships what is already here, dedup drops it and the fold never
--              moves, and asking the origin for more cannot remove a surplus. The burst path has always
--              repaired deficits only, but the drain's claim (099) selected rows by origin, stream and
--              backoff alone, so it re-requested mismatches and surpluses on every backoff step, and those
--              rows counted toward the drain's backlog bound (MaxRepairDrainBacklog) and could defer real
--              deficits.
--
--              Why an explicit column rather than a predicate on the row: origin_lo/hi and local_lo/hi are
--              the two halves of each side's fold digest, which are hashes. Nothing in the row says which
--              side holds more events, so no predicate over it is exact. The comparison knows (it compares
--              the event counts), so it states the kind when it reports, through
--              wh_integrity_report_divergences.
--
--              is_deficit is NULL when the kind is unknown, and an unknown kind is never claimed:
--                * rows written before this migration (no backfill is possible, for the reason above). The
--                  next comparison that re-sights the bucket states its kind; until then the bucket is
--                  reported but not redelivered.
--                * rows the single-key report creates, or whose signature it moves. It is the batch's
--                  fallback and the only report path a version older than this one has, and it carries no
--                  kind. An unchanged signature keeps the kind it had: the kind is a function of the two
--                  event sets, and the signature is their digest.
--
--              The report batch (094) keeps its signature for versions that still call it, and its single
--              keeps its signature too: changing a function's arity leaves the earlier definition sites
--              adding a second overload whenever the ledger replays them. The kind is written by a new,
--              separately named batch that runs the 094 batch and records each key's kind in the same call.
--
--              The claim's eligibility (099: origin learned, per-stream lane, backoff ladder with the
--              terminal cadence; 249: never a bucket marked unhealable) is unchanged apart from the kind.
--              The new partial index holds the claimable rows only (deficits, per-stream, not marked
--              unhealable), in the claim's order, so the claim reads them and never the surpluses,
--              mismatches and unhealable buckets beside them, and the backlog probe counts the same rows.
--
-- Dependencies: 090 (wh_integrity_ledger), 094 (wh_integrity_try_begin_report_batch), 097 (window_from/window_until),
--               249 (unhealable_at; wh_integrity_try_begin_report and wh_integrity_claim_repair_drain, each copied
--               apart from the kind)
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS is_deficit BOOLEAN;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.is_deficit IS
'TRUE when the divergence is a deficit (this store holds fewer events than the origin), the only kind a redelivery can repair and the only kind the paced repair drain claims. FALSE for an identity mismatch or a local surplus, kept for reporting. NULL when the kind is unknown (a row from before the kind was recorded, or a single-key report that moved the signature); never claimed.';

-- The drain's claim order over the rows it may claim, and nothing else. The backlog probe counts the same
-- predicate, so both read only deficits however many mismatches and surpluses the ledger holds.
CREATE INDEX IF NOT EXISTS idx_integrity_ledger_repair_drain
  ON __SCHEMA__.wh_integrity_ledger (last_repair_at ASC NULLS FIRST, first_seen_at ASC)
  WHERE is_deficit AND stream_id <> __EMPTY_UUID__::uuid AND unhealable_at IS NULL;

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_try_begin_report');

-- Re-created from 249 verbatim; the only change is that a moved signature clears the kind, because the
-- kind it recorded described the event sets the old signature digested.
-- <docs>resilience/stream-integrity#drain-claims-deficits-only</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_ARowReportedWithoutItsKind_IsNotClaimed_UntilAReportStatesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerSqlTests.cs:TryBeginReportBatch_GrantsAgainOnceTheCooldownHasElapsedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginReport_AnUnhealableBucketUnchanged_IsNotReReportedOnTheCooldownAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginReport_ASignatureChange_LiftsTheMarkAndRepairResumesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_report(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID,
  p_origin_lo         BIGINT,
  p_origin_hi         BIGINT,
  p_local_lo          BIGINT,
  p_local_hi          BIGINT,
  p_now               TIMESTAMPTZ,
  p_cooldown_seconds  INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_scope   TEXT := COALESCE(p_tenant_scope, '');
  v_row     __SCHEMA__.wh_integrity_ledger%ROWTYPE;
  v_changed BOOLEAN;
  v_report  BOOLEAN;
BEGIN
  -- Lock the row for the duration so concurrent replicas serialize on this bucket and exactly one
  -- of them is told to report. This is the part in-memory state could never provide.
  SELECT * INTO v_row FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id
  FOR UPDATE;

  IF NOT FOUND THEN
    INSERT INTO __SCHEMA__.wh_integrity_ledger (
      origin_service_id, tenant_scope, event_type, stream_id,
      origin_lo, origin_hi, local_lo, local_hi, last_reported_at, repair_attempts, last_touched,
      first_seen_at)
    VALUES (p_origin_service_id, v_scope, p_event_type, p_stream_id,
            p_origin_lo, p_origin_hi, p_local_lo, p_local_hi, p_now, 0, p_now,
            p_now)
    ON CONFLICT (origin_service_id, tenant_scope, event_type, stream_id) DO NOTHING;
    -- A racing replica may have inserted first; only the winner reports.
    IF FOUND THEN
      RETURN TRUE;
    END IF;
    RETURN FALSE;
  END IF;

  v_changed := v_row.origin_lo IS DISTINCT FROM p_origin_lo
            OR v_row.origin_hi IS DISTINCT FROM p_origin_hi
            OR v_row.local_lo  IS DISTINCT FROM p_local_lo
            OR v_row.local_hi  IS DISTINCT FROM p_local_hi;

  -- 249: a bucket repair cannot change was reported once, when it was marked; the cooldown does not repeat it.
  v_report := v_row.last_reported_at IS NULL
           OR v_changed
           OR (v_row.unhealable_at IS NULL
               AND (p_now - v_row.last_reported_at) >= make_interval(secs => p_cooldown_seconds));

  UPDATE __SCHEMA__.wh_integrity_ledger
  SET origin_lo        = p_origin_lo,
      origin_hi        = p_origin_hi,
      local_lo         = p_local_lo,
      local_hi         = p_local_hi,
      -- A moved signature is a fresh incident: the repair budget starts over.
      repair_attempts  = CASE WHEN v_changed THEN 0 ELSE v_row.repair_attempts END,
      last_repair_at   = CASE WHEN v_changed THEN NULL ELSE v_row.last_repair_at END,
      -- 249: and whatever a redelivery showed about the old signature no longer holds.
      unhealable_at    = CASE WHEN v_changed THEN NULL ELSE v_row.unhealable_at END,
      last_reported_at = CASE WHEN v_report THEN p_now ELSE v_row.last_reported_at END,
      -- ...and its kind is no longer known; the caller that knows it states it (wh_integrity_report_divergences).
      is_deficit       = CASE WHEN v_changed THEN NULL ELSE v_row.is_deficit END,
      last_touched     = p_now
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id;

  RETURN v_report;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_report(UUID, TEXT, TEXT, UUID, BIGINT, BIGINT, BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'True when a divergence should be reported now (first sighting, changed signature, or cooldown elapsed while repair can still change it). Row-locked so concurrent replicas cannot both report the same bucket. A changed signature resets the repair budget, lifts the 249 unhealable mark and clears the recorded divergence kind (251).';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_report_divergences');

-- The report batch with each key's divergence kind: the 094 batch's decisions, unchanged, then the kinds
-- in one set-based write. A row whose recorded kind already matches is not written again, so a repeat
-- sighting costs what it cost before.
-- <docs>resilience/stream-integrity#drain-claims-deficits-only</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_TakesOnlyDeficits_AndLeavesMismatchesAndSurplusesForReportingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_ADeficitThatTurnsIntoASurplus_LeavesTheDrain_AndReturnsWhenItIsADeficitAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBatchSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_report_divergences(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_origin_los        BIGINT[],
  p_origin_his        BIGINT[],
  p_local_los         BIGINT[],
  p_local_his         BIGINT[],
  p_is_deficits       BOOLEAN[],
  p_now               TIMESTAMPTZ,
  p_cooldown_seconds  INTEGER
) RETURNS BOOLEAN[]
LANGUAGE plpgsql
AS $$
DECLARE
  v_results BOOLEAN[];
BEGIN
  v_results := __SCHEMA__.wh_integrity_try_begin_report_batch(
    p_origin_service_id, p_tenant_scopes, p_event_types, p_stream_ids,
    p_origin_los, p_origin_his, p_local_los, p_local_his, p_now, p_cooldown_seconds);

  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET is_deficit = k.is_deficit
  FROM (
    SELECT COALESCE(u.scope, '') AS tenant_scope, u.event_type, u.stream_id, u.is_deficit
    FROM UNNEST(p_tenant_scopes, p_event_types, p_stream_ids, p_is_deficits) AS u(scope, event_type, stream_id, is_deficit)
  ) k
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = k.tenant_scope
    AND l.event_type = k.event_type
    AND l.stream_id = k.stream_id
    AND l.is_deficit IS DISTINCT FROM k.is_deficit;

  RETURN v_results;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_report_divergences(UUID, TEXT[], TEXT[], UUID[], BIGINT[], BIGINT[], BIGINT[], BIGINT[], BOOLEAN[], TIMESTAMPTZ, INTEGER) IS
'One round trip for a manifest chunk''s report decisions (element i answers key i, via wh_integrity_try_begin_report_batch) that also records each key''s divergence kind, which decides whether the paced repair drain may claim the row.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_claim_repair_drain');

-- Re-created from 249 verbatim; the eligibility adds the kind, so only deficits are claimed, and the
-- predicate is the partial index's, so the claim walks that index in its own order.
-- <docs>resilience/stream-integrity#drain-claims-deficits-only</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_TakesOnlyDeficits_AndLeavesMismatchesAndSurplusesForReportingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_RowsRecordedBeforeKindsWere_AreNotClaimedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_ALedgerFullOfSurpluses_ReadsOnlyTheDeficitsThroughThePartialIndexAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_PastCap_RetriesAtTerminalCadenceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:ClaimRepairDrain_SkipsAnUnhealableBucketAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_NeverReturnsTheSyntheticBulkLane_AndOrdersLeastRecentlyAttemptedFirstAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_claim_repair_drain(
  p_origin_ids        UUID[],
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER,
  p_limit             INTEGER
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
  WITH eligible AS (
    SELECT l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id
    FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.is_deficit
      AND l.stream_id <> __EMPTY_UUID__::uuid
      AND l.unhealable_at IS NULL  -- 249
      AND l.origin_service_id = ANY(p_origin_ids)
      AND (l.last_repair_at IS NULL
           OR (p_now - l.last_repair_at) >= make_interval(
                secs => p_base_backoff_secs * POWER(2,
                  CASE WHEN l.repair_attempts >= p_max_attempts THEN 6
                       ELSE LEAST(GREATEST(l.repair_attempts - 1, 0), 6) END)))
    ORDER BY l.last_repair_at ASC NULLS FIRST, l.first_seen_at ASC
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET repair_attempts = l.repair_attempts + 1,
      last_repair_at  = p_now,
      last_touched    = p_now
  FROM eligible e
  WHERE l.origin_service_id = e.origin_service_id
    AND l.tenant_scope = e.tenant_scope
    AND l.event_type = e.event_type
    AND l.stream_id = e.stream_id
  RETURNING l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id, l.window_from, l.window_until;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_claim_repair_drain(UUID[], TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
'Atomically claims up to p_limit repair-eligible DEFICIT ledger rows for the paced drain (identity mismatches, local surpluses and rows of unknown kind are never claimed) with the same eligibility ladder as wh_integrity_try_begin_repair (backoff capped at 6 doublings, terminal cadence at the attempt cap, never a bucket marked unhealable), least-recently-attempted first, SKIP LOCKED against concurrent drainers, attempt stamped on claim.';
