-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 392_TheLedgerCountsWhatItOpensAndCloses.sql
-- Date: 2026-11-11
-- Description: The stream-integrity ledger reports how many buckets each call opened and closed, and the open repair
--              backlog is read as one grouped count (#2256).
--
--              The ledger deletes a row when its bucket heals, so it never holds the starting total, and every row
--              still present is open: a "percent healed" query reads 0% everywhere, and the only signal of progress
--              was the open count falling between two samples taken by hand. Counters of buckets opened and closed,
--              with a gauge of the backlog by state, give completion (closed / opened) and an ETA
--              (open / rate(closed)) in any metrics backend.
--
--              The accounting: a bucket is OPENED when its row is recorded, or REOPENED when a bucket marked unhealable
--              becomes repairable again (its local digest changed, or its deficit grew; 320). It is CLOSED once: when
--              its row is removed, or when it is marked unhealable (that mark is counted by the caller of the marking
--              functions, which already counts it as repairs_unhealable). Removing a row already marked unhealable
--              does not close it again. So the rows not marked unhealable change by exactly opened - closed.
--
--              Each function that records or removes ledger rows gets a counting form under a new name, holding the
--              logic, and the existing name becomes a wrapper over it that answers exactly what it answered before.
--              The current release calls the counting forms; an older release still running during a rolling deploy,
--              or one rolled back onto this schema, calls the existing names and reads the shapes it always read.
--
--              1. wh_integrity_report_outcome (new): 320's report decision as an outcome: 0 not reported, 1 reported,
--                 2 reported and the row recorded (opened), 3 reported and the unhealable mark lifted (reopened).
--                 wh_integrity_try_begin_report: outcome > 0.
--              2. wh_integrity_report_divergences_counted (new): 320's report batch with each key's deficit, answering
--                 (granted, opened, reopened): the per-key flags, the rows recorded, and the marks lifted by a local
--                 change or a grown deficit. wh_integrity_report_divergences_with_deficits: its granted flags.
--              3. wh_integrity_repair_outcome (new): 320's repair grant as an outcome: 0 refused, 1 granted, 2 granted
--                 and the row recorded. wh_integrity_try_begin_repair: outcome > 0.
--              4. wh_integrity_repair_batch_counted (new): 094's repair batch, answering (granted, opened).
--                 wh_integrity_try_begin_repair_batch: its granted flags.
--              5. wh_integrity_heal_counted (new): 090's single-key heal, answering the rows removed that were still
--                 open (not marked unhealable). wh_integrity_mark_healed: the same, answering nothing.
--              6. wh_integrity_heal_batch_counted, wh_integrity_heal_types_counted (new): 094's batch heal and 384's
--                 whole-type heal, answering each removed row's age with whether it was still open. The batch heal
--                 also keys an unscoped (null) tenant as the empty scope, as every report and the single-key heal do:
--                 094 compared it raw, so the bucket of an unscoped stream never healed through the batch that every
--                 comparison and re-check heals with.
--                 wh_integrity_mark_healed_batch, wh_integrity_mark_types_healed: the ages.
--              7. wh_integrity_forget_unconsumed_counted, wh_integrity_forget_origin_types_counted (new): 331's and
--                 370's prunes, answering (forgotten, closed): the rows removed and those of them still open.
--                 wh_integrity_forget_unconsumed, wh_integrity_forget_origin_types: the rows removed.
--              8. wh_integrity_ledger_backlog (new): the ledger counted once per origin and state, every known origin in
--                 every state (zero when empty, so a healed origin falls to zero instead of going stale), with the
--                 depth of wh_integrity_deliveries. The states are exclusive, decided in order: unhealable, exhausted
--                 (exhausted_at, or the attempts reached the budget), waiting (an answer pending), answered, open.
--                 One pass over the ledger (090 caps it) and a count of the delivery queue, read by one instance per
--                 service (the maintainer duty's holder) once per maintenance tick. No index: the read is a full
--                 aggregate, as wh_integrity_ledger_gauges (373) is.
--
--              Number 392: 387 to 389 are taken by changes in flight.
--
-- Reversal: reversible (no data is stored; the existing names are restored to their last words, 090, 094, 320, 331, 370
--           and 384, and the counting forms dropped)
-- Dependencies: 090 (wh_integrity_ledger, wh_integrity_mark_healed), 094 (the batch functions), 249 (unhealable_at),
--               302 (wh_integrity_deliveries), 320 (exhausted_at, answer_pending_until, answered_at, deficit_events,
--               wh_integrity_try_begin_report, wh_integrity_try_begin_repair, wh_integrity_report_divergences_with_deficits),
--               331 (wh_integrity_forget_unconsumed), 339 (wh_integrity_known_origins),
--               370 (wh_integrity_forget_origin_types), 384 (wh_integrity_mark_types_healed)
-- Objects: wh_integrity_report_outcome, wh_integrity_try_begin_report, wh_integrity_report_divergences_counted,
--          wh_integrity_report_divergences_with_deficits, wh_integrity_repair_outcome, wh_integrity_try_begin_repair,
--          wh_integrity_repair_batch_counted, wh_integrity_try_begin_repair_batch, wh_integrity_heal_counted,
--          wh_integrity_mark_healed, wh_integrity_heal_batch_counted, wh_integrity_mark_healed_batch,
--          wh_integrity_heal_types_counted, wh_integrity_mark_types_healed, wh_integrity_forget_unconsumed_counted,
--          wh_integrity_forget_unconsumed, wh_integrity_forget_origin_types_counted, wh_integrity_forget_origin_types,
--          wh_integrity_ledger_backlog
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The report decision.
-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_report_outcome');

-- 320's report decision, answering what it did: 0 not reported, 1 reported, 2 reported and recorded (opened), 3 reported
-- and the unhealable mark lifted by a changed local digest (reopened).
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <docs>resilience/stream-integrity#what-resets-the-repair-ladder</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheSingleKeyPaths_CountTheRowsTheyOpenAndCloseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:AnUnhealableBucketWhoseLocalSideChanges_IsReopenedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Report_TheLocalDigestMoved_ResetsTheLadder_AndLiftsTheMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginReport_ASignatureChange_LiftsTheMarkAndRepairResumesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_report_outcome(
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
) RETURNS SMALLINT
LANGUAGE plpgsql
AS $$
DECLARE
  v_scope         TEXT := COALESCE(p_tenant_scope, '');
  v_row           __SCHEMA__.wh_integrity_ledger%ROWTYPE;
  v_changed       BOOLEAN;
  v_local_changed BOOLEAN;
  v_report        BOOLEAN;
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
    -- A racing replica may have inserted first; only the winner reports, and only the winner opened the bucket.
    IF FOUND THEN
      RETURN 2;
    END IF;
    RETURN 0;
  END IF;

  v_local_changed := v_row.local_lo IS DISTINCT FROM p_local_lo
                  OR v_row.local_hi IS DISTINCT FROM p_local_hi;
  v_changed := v_local_changed
            OR v_row.origin_lo IS DISTINCT FROM p_origin_lo
            OR v_row.origin_hi IS DISTINCT FROM p_origin_hi;

  -- 249: a bucket repair cannot change was reported once, when it was marked; the cooldown does not repeat it.
  v_report := v_row.last_reported_at IS NULL
           OR v_changed
           OR (v_row.unhealable_at IS NULL
               AND (p_now - v_row.last_reported_at) >= make_interval(secs => p_cooldown_seconds));

  UPDATE __SCHEMA__.wh_integrity_ledger
  SET origin_lo            = p_origin_lo,
      origin_hi            = p_origin_hi,
      local_lo             = p_local_lo,
      local_hi             = p_local_hi,
      -- 320 (#2030): only a change on this side is a fresh incident for repair. The origin's digest moves with every
      -- event it appends, and that alone tells nothing about what a redelivery can do here.
      repair_attempts      = CASE WHEN v_local_changed THEN 0 ELSE v_row.repair_attempts END,
      last_repair_at       = CASE WHEN v_local_changed THEN NULL ELSE v_row.last_repair_at END,
      unhealable_at        = CASE WHEN v_local_changed THEN NULL ELSE v_row.unhealable_at END,
      exhausted_at         = CASE WHEN v_local_changed THEN NULL ELSE v_row.exhausted_at END,
      answer_pending_until = CASE WHEN v_local_changed THEN NULL ELSE v_row.answer_pending_until END,
      -- 320 (#2029): a comparison re-sighted the bucket, so the answer it was waiting on has been judged.
      answered_at          = NULL,
      last_reported_at     = CASE WHEN v_report THEN p_now ELSE v_row.last_reported_at END,
      -- 251: the kind described the event sets the old signature digested; the caller that knows it states it.
      is_deficit           = CASE WHEN v_changed THEN NULL ELSE v_row.is_deficit END,
      last_touched         = p_now
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id;

  -- 392 (#2256): a local change always reports (v_changed), so a lifted mark is always a reported outcome.
  IF v_local_changed AND v_row.unhealable_at IS NOT NULL THEN
    RETURN 3;
  END IF;
  RETURN CASE WHEN v_report THEN 1 ELSE 0 END;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_report_outcome(UUID, TEXT, TEXT, UUID, BIGINT, BIGINT, BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'392 (#2256): whether a divergence should be reported now (first sighting, changed signature, or cooldown elapsed while repair can still change it), as an outcome: 0 not reported, 1 reported, 2 reported and the row recorded, 3 reported and the unhealable mark lifted. Row-locked so concurrent replicas cannot both report the same bucket. A changed LOCAL digest resets the repair ladder (attempts, the unhealable and exhausted marks, a pending answer); a change on the origin''s side alone does not (320). Any sighting clears the answered state, and a changed signature clears the recorded kind (251).';

-- The previous release's report decision, for a release that still calls it: the outcome as the flag it answered.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
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
BEGIN
  RETURN __SCHEMA__.wh_integrity_report_outcome(
    p_origin_service_id, p_tenant_scope, p_event_type, p_stream_id,
    p_origin_lo, p_origin_hi, p_local_lo, p_local_hi, p_now, p_cooldown_seconds) > 0;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_report(UUID, TEXT, TEXT, UUID, BIGINT, BIGINT, BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'True when a divergence should be reported now: wh_integrity_report_outcome (392) as a flag, for a release that calls this name.';

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_report_divergences_counted');

-- 320's report batch with each key's deficit size, answering also the rows it recorded and the unhealable marks it
-- lifted (a changed local digest in the outcome function, or a grown deficit here).
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <docs>resilience/stream-integrity#what-resets-the-repair-ladder</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:AHealedBucket_IsClosedOnce_AndLowersTheOpenGaugeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:AnUnhealableBucketWhoseLocalSideChanges_IsReopenedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:AnUnhealableBucketWhoseDeficitGrows_IsReopenedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Report_TheDeficitGrew_ResetsTheLadder_AndLiftsTheMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_TakesOnlyDeficits_AndLeavesMismatchesAndSurplusesForReportingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_report_divergences_counted(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_origin_los        BIGINT[],
  p_origin_his        BIGINT[],
  p_local_los         BIGINT[],
  p_local_his         BIGINT[],
  p_is_deficits       BOOLEAN[],
  p_deficit_events    BIGINT[],
  p_now               TIMESTAMPTZ,
  p_cooldown_seconds  INTEGER
) RETURNS TABLE (granted BOOLEAN[], opened INTEGER, reopened INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  v_n        INTEGER := COALESCE(array_length(p_stream_ids, 1), 0);
  v_results  BOOLEAN[] := '{}';
  v_opened   INTEGER := 0;
  v_reopened INTEGER := 0;
  v_grown    INTEGER;
  v_outcome  SMALLINT;
  v_i        INTEGER;
BEGIN
  FOR v_i IN 1 .. v_n LOOP
    v_outcome := __SCHEMA__.wh_integrity_report_outcome(
      p_origin_service_id, p_tenant_scopes[v_i], p_event_types[v_i], p_stream_ids[v_i],
      p_origin_los[v_i], p_origin_his[v_i], p_local_los[v_i], p_local_his[v_i],
      p_now, p_cooldown_seconds);
    v_results := v_results || (v_outcome > 0);
    v_opened := v_opened + CASE WHEN v_outcome = 2 THEN 1 ELSE 0 END;
    v_reopened := v_reopened + CASE WHEN v_outcome = 3 THEN 1 ELSE 0 END;
  END LOOP;

  WITH updated AS (
    UPDATE __SCHEMA__.wh_integrity_ledger l
    SET is_deficit           = k.is_deficit,
        deficit_events       = COALESCE(k.deficit_events, l.deficit_events),
        repair_attempts      = CASE WHEN k.grew THEN 0 ELSE l.repair_attempts END,
        last_repair_at       = CASE WHEN k.grew THEN NULL ELSE l.last_repair_at END,
        unhealable_at        = CASE WHEN k.grew THEN NULL ELSE l.unhealable_at END,
        exhausted_at         = CASE WHEN k.grew THEN NULL ELSE l.exhausted_at END,
        answer_pending_until = CASE WHEN k.grew THEN NULL ELSE l.answer_pending_until END
    FROM (
      SELECT u.tenant_scope, u.event_type, u.stream_id, u.is_deficit, u.deficit_events,
             COALESCE(u.deficit_events > c.deficit_events, false) AS grew,
             c.unhealable_at IS NOT NULL AS was_unhealable
      FROM (
        SELECT COALESCE(x.scope, '') AS tenant_scope, x.event_type, x.stream_id, x.is_deficit, x.deficit_events
        FROM UNNEST(p_tenant_scopes, p_event_types, p_stream_ids, p_is_deficits, p_deficit_events)
          AS x(scope, event_type, stream_id, is_deficit, deficit_events)
      ) u
      JOIN __SCHEMA__.wh_integrity_ledger c
        ON c.origin_service_id = p_origin_service_id AND c.tenant_scope = u.tenant_scope
       AND c.event_type = u.event_type AND c.stream_id = u.stream_id
    ) k
    WHERE l.origin_service_id = p_origin_service_id
      AND l.tenant_scope = k.tenant_scope
      AND l.event_type = k.event_type
      AND l.stream_id = k.stream_id
      AND (l.is_deficit IS DISTINCT FROM k.is_deficit
           OR l.deficit_events IS DISTINCT FROM COALESCE(k.deficit_events, l.deficit_events)
           OR k.grew)
    RETURNING k.grew AND k.was_unhealable AS lifted
  )
  SELECT COUNT(*) FILTER (WHERE u.lifted)::INTEGER INTO v_grown FROM updated u;

  RETURN QUERY SELECT v_results, v_opened, v_reopened + v_grown;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_report_divergences_counted(UUID, TEXT[], TEXT[], UUID[], BIGINT[], BIGINT[], BIGINT[], BIGINT[], BOOLEAN[], BIGINT[], TIMESTAMPTZ, INTEGER) IS
'392 (#2256): one round trip for a manifest chunk''s report decisions (granted: element i answers key i), recording each key''s divergence kind (251) and deficit size (320), with the rows it recorded (opened) and the unhealable marks it lifted (reopened). A deficit larger than the one recorded resets the repair ladder (attempts, the unhealable and exhausted marks, a pending answer); an unknown size (NULL) keeps what is recorded.';

-- The previous release's report batch, for a release that still calls it: the counting form's flags.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_report_divergences_with_deficits(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_origin_los        BIGINT[],
  p_origin_his        BIGINT[],
  p_local_los         BIGINT[],
  p_local_his         BIGINT[],
  p_is_deficits       BOOLEAN[],
  p_deficit_events    BIGINT[],
  p_now               TIMESTAMPTZ,
  p_cooldown_seconds  INTEGER
) RETURNS BOOLEAN[]
LANGUAGE sql
AS $$
  SELECT c.granted FROM __SCHEMA__.wh_integrity_report_divergences_counted(
    p_origin_service_id, p_tenant_scopes, p_event_types, p_stream_ids, p_origin_los, p_origin_his,
    p_local_los, p_local_his, p_is_deficits, p_deficit_events, p_now, p_cooldown_seconds) c;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_report_divergences_with_deficits(UUID, TEXT[], TEXT[], UUID[], BIGINT[], BIGINT[], BIGINT[], BIGINT[], BOOLEAN[], BIGINT[], TIMESTAMPTZ, INTEGER) IS
'One round trip for a manifest chunk''s report decisions: wh_integrity_report_divergences_counted (392) without its counts, for a release that calls this name.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The repair grant.
-- ────────────────────────────────────────────────────────────────────────────────────────────────
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_repair_outcome');

-- 320's repair grant, answering what it did: 0 refused, 1 granted, 2 granted and the row recorded (opened).
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheSingleKeyPaths_CountTheRowsTheyOpenAndCloseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_PastTheAttemptBudget_IsRefusedForGoodAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_WhileItsOwnAnswerIsPending_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_OnTheTypeLane_StandsDown_WhilePerStreamRowsOfTheTypeAwaitTheirAnswerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginRepair_AnUnhealableBucket_IsRefusedPastItsTerminalCadenceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_repair_outcome(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID,
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER
) RETURNS SMALLINT
LANGUAGE plpgsql
AS $$
DECLARE
  v_scope     TEXT := COALESCE(p_tenant_scope, '');
  v_row       __SCHEMA__.wh_integrity_ledger%ROWTYPE;
  v_doublings INTEGER;
BEGIN
  -- 320 (#2034): a type-lane ask (bulk, checkpoint) re-ships every stream of the type; while per-stream requests of the
  -- type are waiting for their answers, those answers are already on the way.
  IF p_stream_id = __EMPTY_UUID__::uuid AND EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_integrity_ledger s
       WHERE s.origin_service_id = p_origin_service_id AND s.tenant_scope = v_scope
         AND s.event_type = p_event_type AND s.stream_id <> __EMPTY_UUID__::uuid
         AND s.answer_pending_until > p_now) THEN
    RETURN 0;
  END IF;

  SELECT * INTO v_row FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id
  FOR UPDATE;

  IF NOT FOUND THEN
    INSERT INTO __SCHEMA__.wh_integrity_ledger (
      origin_service_id, tenant_scope, event_type, stream_id,
      origin_lo, origin_hi, local_lo, local_hi, repair_attempts, last_repair_at, last_touched, exhausted_at)
    VALUES (p_origin_service_id, v_scope, p_event_type, p_stream_id,
            -9223372036854775808, -9223372036854775808, -9223372036854775808, -9223372036854775808,
            1, p_now, p_now, CASE WHEN 1 >= p_max_attempts THEN p_now END)
    ON CONFLICT (origin_service_id, tenant_scope, event_type, stream_id) DO NOTHING;
    RETURN CASE WHEN FOUND THEN 2 ELSE 0 END;
  END IF;

  -- 249: a redelivery already showed repair cannot change this bucket; asking again ships the same events again.
  -- 320: nor while the answer to the last request is on the way, nor once the budget is spent.
  IF v_row.unhealable_at IS NOT NULL
     OR v_row.exhausted_at IS NOT NULL
     OR v_row.repair_attempts >= p_max_attempts
     OR v_row.answer_pending_until > p_now THEN
    RETURN 0;
  END IF;

  IF v_row.last_repair_at IS NOT NULL THEN
    -- Capped at 6 doublings so the wait cannot overflow, matching the in-memory ladder.
    v_doublings := LEAST(GREATEST(v_row.repair_attempts - 1, 0), 6);
    IF (p_now - v_row.last_repair_at) < make_interval(secs => p_base_backoff_secs * POWER(2, v_doublings)) THEN
      RETURN 0;
    END IF;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_ledger
  SET repair_attempts = v_row.repair_attempts + 1,
      last_repair_at  = p_now,
      exhausted_at    = CASE WHEN v_row.repair_attempts + 1 >= p_max_attempts THEN p_now ELSE NULL END,
      answered_at     = NULL,
      last_touched    = p_now
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id;

  RETURN 1;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_repair_outcome(UUID, TEXT, TEXT, UUID, TIMESTAMPTZ, INTEGER, INTEGER) IS
'392 (#2256): whether a repair should be requested now, as an outcome: 0 refused, 1 granted, 2 granted and the row recorded. On an exponential ladder (6 doublings at most) that ends at p_max_attempts: the grant that spends the last attempt marks the bucket exhausted and none follows until a reset (320). Never while the answer to the last request is pending, never for a bucket marked unhealable (249), and never on the type lane while per-stream rows of the type await their answers.';

-- The previous release's repair grant, for a release that still calls it: the outcome as the flag it answered.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_repair(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID,
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN __SCHEMA__.wh_integrity_repair_outcome(
    p_origin_service_id, p_tenant_scope, p_event_type, p_stream_id, p_now, p_base_backoff_secs, p_max_attempts) > 0;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_repair(UUID, TEXT, TEXT, UUID, TIMESTAMPTZ, INTEGER, INTEGER) IS
'True when a repair should be requested now: wh_integrity_repair_outcome (392) as a flag, for a release that calls this name.';

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_repair_batch_counted');

-- 094's repair batch over the outcome above, answering also the rows it recorded.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheRepairBatch_CountsTheRowsItOpensAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerSqlTests.cs:TryBeginRepairBatch_PastTheCap_IsRefusedForGoodAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_repair_batch_counted(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER,
  p_max_grants        INTEGER
) RETURNS TABLE (granted BOOLEAN[], opened INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  v_n       INTEGER := COALESCE(array_length(p_stream_ids, 1), 0);
  v_results BOOLEAN[] := '{}';
  v_granted INTEGER := 0;
  v_opened  INTEGER := 0;
  v_outcome SMALLINT;
  v_i       INTEGER;
BEGIN
  FOR v_i IN 1 .. v_n LOOP
    IF v_granted >= p_max_grants THEN
      -- Budget spent: do not even ASK. The outcome function records an attempt when it grants,
      -- and a grant the caller must discard burns backoff budget for nothing.
      v_results := v_results || FALSE;
      CONTINUE;
    END IF;
    v_outcome := __SCHEMA__.wh_integrity_repair_outcome(
      p_origin_service_id, p_tenant_scopes[v_i], p_event_types[v_i], p_stream_ids[v_i],
      p_now, p_base_backoff_secs, p_max_attempts);
    IF v_outcome > 0 THEN
      v_granted := v_granted + 1;
    END IF;
    IF v_outcome = 2 THEN
      v_opened := v_opened + 1;
    END IF;
    v_results := v_results || (v_outcome > 0);
  END LOOP;
  RETURN QUERY SELECT v_results, v_opened;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_repair_batch_counted(UUID, TEXT[], TEXT[], UUID[], TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
'392 (#2256): one round trip for a chunk''s repair decisions, capped at p_max_grants IN ORDER (past the cap keys are not consulted at all, so no attempt budget is burned on grants the caller cannot use), with the rows it recorded.';

-- The previous release's repair batch, for a release that still calls it: the counting form's flags.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_repair_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER,
  p_max_grants        INTEGER
) RETURNS BOOLEAN[]
LANGUAGE sql
AS $$
  SELECT c.granted FROM __SCHEMA__.wh_integrity_repair_batch_counted(
    p_origin_service_id, p_tenant_scopes, p_event_types, p_stream_ids, p_now, p_base_backoff_secs, p_max_attempts,
    p_max_grants) c;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_repair_batch(UUID, TEXT[], TEXT[], UUID[], TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
'One round trip for a chunk''s repair decisions: wh_integrity_repair_batch_counted (392) without its count, for a release that calls this name.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The heals.
-- ────────────────────────────────────────────────────────────────────────────────────────────────
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_heal_counted');

-- 090's single-key heal, answering the rows it removed that were still open (not marked unhealable).
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheSingleKeyPaths_CountTheRowsTheyOpenAndCloseAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_heal_counted(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_closed INTEGER;
BEGIN
  WITH healed AS (
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.origin_service_id = p_origin_service_id
      AND l.tenant_scope = COALESCE(p_tenant_scope, '')
      AND l.event_type = p_event_type
      AND l.stream_id = p_stream_id
    RETURNING l.unhealable_at
  )
  SELECT (COUNT(*) FILTER (WHERE h.unhealable_at IS NULL))::INTEGER INTO v_closed FROM healed h;
  RETURN v_closed;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_heal_counted(UUID, TEXT, TEXT, UUID) IS
'392 (#2256): forgets one bucket that folded identical, answering 1 when the row removed was still open (not marked unhealable) and 0 otherwise.';

-- The previous release's single-key heal, for a release that still calls it.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_healed(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  PERFORM __SCHEMA__.wh_integrity_heal_counted(p_origin_service_id, p_tenant_scope, p_event_type, p_stream_id);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_healed(UUID, TEXT, TEXT, UUID) IS
'Forgets one bucket that folded identical: wh_integrity_heal_counted (392) without its count, for a release that calls this name.';

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_heal_batch_counted');

-- 094's batch heal: each removed row's age in seconds (first sighting to heal, clamped at zero for clock skew), with
-- whether the row was still open (not marked unhealable, which closed it already). An unscoped (null) tenant key now
-- matches the empty scope the ledger stores, as the single-key heal always did.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:AHealedBucket_IsClosedOnce_AndLowersTheOpenGaugeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheBatchHeal_OfAnUnscopedBucket_RemovesItsRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBatchSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_heal_batch_counted(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[]
) RETURNS TABLE (age_seconds DOUBLE PRECISION, was_open BOOLEAN)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  WITH keys AS (
    SELECT unnest(p_tenant_scopes) AS tenant_scope,
           unnest(p_event_types)   AS event_type,
           unnest(p_stream_ids)    AS stream_id
  ),
  healed AS (
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    USING keys k
    WHERE l.origin_service_id = p_origin_service_id
      -- 392: an unscoped key (null) is the ledger's empty scope, as every report and the single-key heal key it; 094
      -- compared it raw, so an unscoped bucket never healed through this batch.
      AND l.tenant_scope      = COALESCE(k.tenant_scope, '')
      AND l.event_type        = k.event_type
      AND l.stream_id         = k.stream_id
    RETURNING l.first_seen_at, l.unhealable_at
  )
  SELECT GREATEST(EXTRACT(EPOCH FROM (NOW() - h.first_seen_at)), 0)::DOUBLE PRECISION, h.unhealable_at IS NULL
  FROM healed h;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_heal_batch_counted(UUID, TEXT[], TEXT[], UUID[]) IS
'392 (#2256): one round trip to forget a chunk''s healed buckets, returning each healed bucket''s age in seconds (first sighting -> heal), read from the rows the delete destroys, and whether it was still open.';

-- The previous release's batch heal, for a release that still calls it: the ages.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[]
) RETURNS SETOF DOUBLE PRECISION
LANGUAGE sql
AS $$
  SELECT c.age_seconds FROM __SCHEMA__.wh_integrity_heal_batch_counted(
    p_origin_service_id, p_tenant_scopes, p_event_types, p_stream_ids) c;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch(UUID, TEXT[], TEXT[], UUID[]) IS
'One round trip to forget a chunk''s healed buckets, returning each healed bucket''s age in seconds: wh_integrity_heal_batch_counted (392), for a release that calls this name.';

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_heal_types_counted');

-- 384's whole-type heal: every ledger row of the given (tenant, type) pairs of one origin, with each row's age and
-- whether it was still open.
-- <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:AnUnhealableBucket_MovesToStateUnhealable_AndIsNotClosedAgainWhenItsRowGoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeHealSqlTests.cs:MarkTypesHealed_ForgetsTheTypeLaneAndEveryStreamRow_OfTheProvenTypesOnly_WithTheirAgesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeHealSqlTests.cs:MarkTypesHealed_NothingTracked_IsAnEmptyAnswerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_heal_types_counted(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[]
) RETURNS TABLE (age_seconds DOUBLE PRECISION, was_open BOOLEAN)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  WITH types AS (
    SELECT DISTINCT COALESCE(t.tenant, '') AS tenant_scope, t.etype AS event_type
    FROM unnest(p_tenant_scopes, p_event_types) AS t(tenant, etype)
  ),
  healed AS (
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    USING types t
    WHERE l.origin_service_id = p_origin_service_id
      AND l.tenant_scope      = t.tenant_scope
      AND l.event_type        = t.event_type
    RETURNING l.first_seen_at, l.unhealable_at
  )
  SELECT GREATEST(EXTRACT(EPOCH FROM (NOW() - h.first_seen_at)), 0)::DOUBLE PRECISION, h.unhealable_at IS NULL
  FROM healed h;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_heal_types_counted(UUID, TEXT[], TEXT[]) IS
'392 (#2256): forgets every ledger row (the type lane and every per-stream row) of the given (tenant, type) pairs of one origin, which a comparison proved complete over the whole history (384), returning each forgotten row''s age in seconds and whether it was still open.';

-- The previous release's whole-type heal, for a release that still calls it: the ages.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_types_healed(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[]
) RETURNS SETOF DOUBLE PRECISION
LANGUAGE sql
AS $$
  SELECT c.age_seconds FROM __SCHEMA__.wh_integrity_heal_types_counted(p_origin_service_id, p_tenant_scopes, p_event_types) c;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_types_healed(UUID, TEXT[], TEXT[]) IS
'Forgets every ledger row of the given (tenant, type) pairs of one origin, returning each row''s age in seconds: wh_integrity_heal_types_counted (392), for a release that calls this name.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The prunes.
-- ────────────────────────────────────────────────────────────────────────────────────────────────
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_forget_unconsumed_counted');

-- 331's prune of the rows of event types the service neither consumes nor absorbs: the rows removed, and those of them
-- still open (not marked unhealable). A null consumed set forgets nothing.
-- <docs>resilience/stream-integrity#the-integrity-surface-is-what-the-service-consumes</docs>
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheForgetPaths_CountRowsThatWereStillOpen_AsForgottenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_RemovesEveryRowOfAnUnconsumedType_AndKeepsTheConsumedOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed_counted(
  p_consumed_event_types TEXT[],
  p_absorbed_namespaces  TEXT[]
) RETURNS TABLE (forgotten INTEGER, closed INTEGER)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  IF p_consumed_event_types IS NULL THEN
    RETURN QUERY SELECT 0, 0;
    RETURN;
  END IF;
  RETURN QUERY
  WITH gone AS (
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.event_type <> ALL (p_consumed_event_types)
      AND lower(COALESCE(substring(split_part(l.event_type, ', ', 1) FROM '^(.*)[.][^.]*$'), ''))
          <> ALL (COALESCE(p_absorbed_namespaces, ARRAY[]::TEXT[]))
    RETURNING l.unhealable_at
  )
  SELECT COUNT(*)::INTEGER, (COUNT(*) FILTER (WHERE g.unhealable_at IS NULL))::INTEGER FROM gone g;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed_counted(TEXT[], TEXT[]) IS
'392 (#2256): deletes the integrity ledger rows of event types the service neither consumes nor absorbs (331), which can never heal. A null set forgets nothing. Returns the rows deleted (forgotten) and those of them not marked unhealable (closed).';

-- The previous release's prune, for a release that still calls it, and for the operator: the rows removed.
-- <docs>resilience/stream-integrity#the-integrity-surface-is-what-the-service-consumes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_ANullConsumedSet_ForgetsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:ThePreviousReleasesFunctions_AnswerInTheirOldShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed(
  p_consumed_event_types TEXT[],
  p_absorbed_namespaces  TEXT[]
) RETURNS INTEGER
LANGUAGE sql
AS $$
  SELECT c.forgotten FROM __SCHEMA__.wh_integrity_forget_unconsumed_counted(p_consumed_event_types, p_absorbed_namespaces) c;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed(TEXT[], TEXT[]) IS
'331 (#2058): deletes the integrity ledger rows of event types the service neither consumes nor absorbs; a null set forgets nothing. Returns the rows deleted: wh_integrity_forget_unconsumed_counted (392) without its closed count.';

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_forget_origin_types_counted');

-- 370's prune of every ledger row of the given (origin, type) pairs: the rows removed, and those of them still open.
-- Pairs are positional across the two arrays. A null or empty list forgets nothing.
-- <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheForgetPaths_CountRowsThatWereStillOpen_AsForgottenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_RemovesEveryRowOfAJudgedPair_AndKeepsTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_origin_types_counted(
  p_origin_ids  UUID[],
  p_event_types TEXT[]
) RETURNS TABLE (forgotten INTEGER, closed INTEGER)
LANGUAGE plpgsql
AS $$
BEGIN
  IF p_origin_ids IS NULL OR p_event_types IS NULL OR cardinality(p_origin_ids) = 0 THEN
    RETURN QUERY SELECT 0, 0;
    RETURN;
  END IF;
  RETURN QUERY
  WITH gone AS (
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    USING unnest(p_origin_ids, p_event_types) AS p(origin_service_id, event_type)
    WHERE l.origin_service_id = p.origin_service_id
      AND l.event_type = p.event_type
    RETURNING l.unhealable_at
  )
  SELECT COUNT(*)::INTEGER, (COUNT(*) FILTER (WHERE g.unhealable_at IS NULL))::INTEGER FROM gone g;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_origin_types_counted(UUID[], TEXT[]) IS
'392 (#2256): deletes every integrity ledger row of the given (origin, event type) pairs, whose origin does not own the type (370). Returns the rows deleted (forgotten) and those of them not marked unhealable (closed).';

-- The previous release's prune, for a release that still calls it: the rows removed.
-- <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_AnEmptyPairList_RemovesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_origin_types(
  p_origin_ids  UUID[],
  p_event_types TEXT[]
) RETURNS INTEGER
LANGUAGE sql
AS $$
  SELECT c.forgotten FROM __SCHEMA__.wh_integrity_forget_origin_types_counted(p_origin_ids, p_event_types) c;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_origin_types(UUID[], TEXT[]) IS
'370: deletes every integrity ledger row of the given (origin, event type) pairs. Returns the rows deleted: wh_integrity_forget_origin_types_counted (392) without its closed count.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The backlog.
-- ────────────────────────────────────────────────────────────────────────────────────────────────
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_backlog');

-- The open repair backlog: every origin (known by its checkpoints, or holding ledger rows) in every state, with its
-- buckets in that state, and the integrity delivery queue's depth on every row. With no origin at all, one row with a
-- null origin carries the queue depth.
-- <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:Backlog_CountsEachOriginsBucketsOncePerState_NamesTheOrigin_AndReadsTheQueueDepthAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:Backlog_OfAnEmptyLedger_IsReported_WithNoBucketsAndAnEmptyQueueAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_backlog(p_max_attempts INTEGER)
RETURNS TABLE (
  origin_service_id   UUID,
  origin_service_name TEXT,
  state               TEXT,
  buckets             BIGINT,
  outbound_deliveries BIGINT
)
LANGUAGE sql
STABLE
AS $$
  WITH counted AS (
    SELECT x.origin_service_id AS origin,
           CASE
             WHEN x.unhealable_at IS NOT NULL THEN 'unhealable'
             WHEN x.exhausted_at IS NOT NULL OR x.repair_attempts >= p_max_attempts THEN 'exhausted'
             WHEN x.answer_pending_until > NOW() THEN 'waiting'
             WHEN x.answered_at IS NOT NULL THEN 'answered'
             ELSE 'open'
           END AS bucket_state,
           COUNT(*)::BIGINT AS n
    FROM __SCHEMA__.wh_integrity_ledger x
    GROUP BY 1, 2
  ),
  origins AS (
    SELECT k.origin_service_id AS origin FROM __SCHEMA__.wh_integrity_known_origins k
    UNION
    SELECT c.origin FROM counted c
  ),
  queue AS (
    SELECT COUNT(*)::BIGINT AS depth FROM __SCHEMA__.wh_integrity_deliveries
  )
  SELECT o.origin, k.origin_service_name, s.bucket_state, COALESCE(c.n, 0), q.depth
  FROM queue q
  LEFT JOIN (origins o
             CROSS JOIN (VALUES ('unhealable'), ('exhausted'), ('waiting'), ('answered'), ('open')) AS s(bucket_state)
             LEFT JOIN __SCHEMA__.wh_integrity_known_origins k ON k.origin_service_id = o.origin
             LEFT JOIN counted c ON c.origin = o.origin AND c.bucket_state = s.bucket_state)
    ON TRUE
  ORDER BY 1, 3;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_ledger_backlog(INTEGER) IS
'392 (#2256): the open repair backlog for the ledger_open_buckets and outbound_deliveries gauges. Each origin known by its checkpoints or holding ledger rows, in each state (unhealable, exhausted, waiting, answered, open; exclusive, decided in that order), with its buckets in that state, zero when none; and the depth of wh_integrity_deliveries on every row (a single row with a null origin when there is no origin). One pass over the ledger, read by the maintainer duty''s holder once per maintenance tick.';
