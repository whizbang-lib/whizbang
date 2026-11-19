-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 320_RepairWaitsForItsAnswer.sql
-- Date: 2026-11-03
-- Description: Stream-integrity repair waits for the answer to its request and stops at its attempt budget
--              (#2029, #2030, #2033, #2034, #2035).
--
--              The paced drain re-asked for a bucket while the answer to its last request was still on the way (the
--              backoff ran from the claim, and nothing recorded a request in flight), a received answer healed nothing
--              until the next audit, and past MaxRepairAttemptsPerBucket the ladder settled into a terminal cadence
--              (base x 2^6) that asked again every few hours forever. An origin's new events reset the ladder and
--              lifted the unhealable mark on every audit. Each request re-ships every event of the bucket's window.
--
--              1. Ledger state:
--                 * answer_pending_until: a request for the bucket left (set by wh_integrity_mark_answer_pending
--                   after a send); neither grant path asks again before it passes. A type-lane mark (the empty
--                   stream, used by bulk and checkpoint requests) also covers the per-stream rows of that type whose
--                   stamped window lies inside the request's window.
--                 * answered_at: a bundle for the bucket was committed (wh_integrity_record_bundle_answer). The drain
--                   does not claim the bucket again until a comparison re-sights it (wh_integrity_try_begin_report
--                   clears it) or a heal deletes the row.
--                 * exhausted_at: the bucket spent its attempt budget. Neither grant path asks again until a reset.
--                 * deficit_events: the last reported deficit in events, so a grown deficit can be told from an
--                   origin that merely moved on.
--              2. wh_integrity_try_begin_report: the ladder (attempts, last attempt, the unhealable and exhausted
--                 marks, the pending answer) resets only when the LOCAL digest moved. A change on the origin's side
--                 alone keeps them. Any sighting clears answered_at: the comparison has spoken.
--              3. wh_integrity_report_divergences_with_deficits: the 251 report batch plus each key's deficit size;
--                 a deficit that grew past the recorded one resets the ladder as a local change does. A new name, as
--                 251 did, so the earlier batch keeps its signature for versions that still call it.
--              4. wh_integrity_claim_repair_drain and wh_integrity_try_begin_repair: never a bucket with its answer
--                 pending, answered (drain), or exhausted; no terminal cadence; the claim or grant that spends the
--                 last attempt stamps exhausted_at. A type-lane grant stands down while per-stream rows of its type
--                 await their answer (the per-stream requests already re-ship those events).
--              5. wh_integrity_mark_answer_pending and wh_integrity_record_bundle_answer (new). The second runs when a
--                 redelivery bundle's children commit, by primary key over the bundle's events: it answers per bucket
--                 what the store held at commit, records the answer on the ledger, and marks the buckets of a bundle
--                 that changed nothing as unhealable at once. "Present" is judged at commit on purpose: by the time
--                 the maintainer's deferred reconcile runs, the bundle's own children are stored and read as present.
--              6. idx_integrity_ledger_repair_drain_open replaces idx_integrity_ledger_repair_drain (251): the
--                 claimable rows without the answered and exhausted ones, so the claim and the backlog probe never
--                 walk rows that are not queued repairs.
--              7. wh_integrity_ledger_gauges (new): 090's wh_integrity_ledger_summary plus the buckets awaiting an
--                 answer, the answered buckets, and the integrity deliveries waiting for the maintainer with the age
--                 of the oldest (#2035). A new name because the result columns change (see the definition).
--
--              Number 320: 316 to 319 are taken by changes in flight.
--
-- Dependencies: 090 (wh_integrity_ledger, wh_integrity_ledger_summary, copied as wh_integrity_ledger_gauges), 094 (wh_integrity_try_begin_report_batch),
--               097 (window_from/window_until), 249 (unhealable_at, wh_integrity_try_begin_repair), 251 (is_deficit,
--               wh_integrity_try_begin_report, wh_integrity_claim_repair_drain, each copied with the deltas above),
--               252 (the refused-truncated inbox state), 302 (wh_integrity_deliveries)
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS answer_pending_until TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS answered_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS exhausted_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS deficit_events BIGINT;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.answer_pending_until IS
'320 (#2029): a repair request for this bucket left and its answer is on the way until then; no grant path asks again before it passes. Cleared when a bundle for the bucket commits, or by a change in the local digest.';
COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.answered_at IS
'320 (#2029): a redelivery bundle for this bucket committed; the paced drain waits for the next comparison (which clears it) or a heal (which deletes the row).';
COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.exhausted_at IS
'320 (#2029): the bucket spent its repair attempt budget and is no longer requested. Lifted by a change in the local digest or a deficit that grew (#2030).';
COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.deficit_events IS
'320 (#2030): the deficit, in events, the last comparison reported; a larger one later is a new incident. NULL when unknown.';

-- The claimable rows only, in the claim's order (replaces 251's index, whose rows included answered and exhausted ones).
DROP INDEX IF EXISTS __SCHEMA__.idx_integrity_ledger_repair_drain;
CREATE INDEX IF NOT EXISTS idx_integrity_ledger_repair_drain_open
  ON __SCHEMA__.wh_integrity_ledger (last_repair_at ASC NULLS FIRST, first_seen_at ASC)
  WHERE is_deficit AND stream_id <> __EMPTY_UUID__::uuid AND unhealable_at IS NULL
    AND answered_at IS NULL AND exhausted_at IS NULL;

COMMENT ON INDEX __SCHEMA__.idx_integrity_ledger_repair_drain_open IS
'320 (#2029): the rows the paced drain may claim, in claim order: deficits, per stream, not unhealable, not answered and not exhausted. The backlog probe counts the same rows.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_try_begin_report');

-- Re-created from 251; the change is what resets the ladder (the local digest only) and that a sighting clears answered_at.
-- <docs>resilience/stream-integrity#what-resets-the-repair-ladder</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Report_TheLocalDigestMoved_ResetsTheLadder_AndLiftsTheMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Report_OnlyTheOriginsDigestMoved_KeepsTheAttempts_AndTheMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ClearsThePending_AndTheBucketWaitsForTheNextComparisonAsync</tests>
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
    -- A racing replica may have inserted first; only the winner reports.
    IF FOUND THEN
      RETURN TRUE;
    END IF;
    RETURN FALSE;
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

  RETURN v_report;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_report(UUID, TEXT, TEXT, UUID, BIGINT, BIGINT, BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'True when a divergence should be reported now (first sighting, changed signature, or cooldown elapsed while repair can still change it). Row-locked so concurrent replicas cannot both report the same bucket. A changed LOCAL digest resets the repair ladder (attempts, the unhealable and exhausted marks, a pending answer); a change on the origin''s side alone does not (320). Any sighting clears the answered state, and a changed signature clears the recorded kind (251).';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_report_divergences_with_deficits');

-- The 251 report batch plus each key's deficit size: a grown deficit resets the ladder as a local change does.
-- <docs>resilience/stream-integrity#what-resets-the-repair-ladder</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Report_TheDeficitGrew_ResetsTheLadder_AndLiftsTheMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Report_OnlyTheOriginsDigestMoved_KeepsTheAttempts_AndTheMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_TakesOnlyDeficits_AndLeavesMismatchesAndSurplusesForReportingAsync</tests>
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
LANGUAGE plpgsql
AS $$
DECLARE
  v_results BOOLEAN[];
BEGIN
  v_results := __SCHEMA__.wh_integrity_try_begin_report_batch(
    p_origin_service_id, p_tenant_scopes, p_event_types, p_stream_ids,
    p_origin_los, p_origin_his, p_local_los, p_local_his, p_now, p_cooldown_seconds);

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
           COALESCE(u.deficit_events > c.deficit_events, false) AS grew
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
         OR k.grew);

  RETURN v_results;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_report_divergences_with_deficits(UUID, TEXT[], TEXT[], UUID[], BIGINT[], BIGINT[], BIGINT[], BIGINT[], BOOLEAN[], BIGINT[], TIMESTAMPTZ, INTEGER) IS
'320 (#2030): one round trip for a manifest chunk''s report decisions (element i answers key i), recording each key''s divergence kind (251) and deficit size. A deficit larger than the one recorded resets the repair ladder (attempts, the unhealable and exhausted marks, a pending answer); an unknown size (NULL) keeps what is recorded.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_claim_repair_drain');

-- Re-created from 251; the eligibility adds the answer and exhaustion states and drops the terminal cadence.
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Claim_ABucketWithItsAnswerPending_IsNotClaimed_UntilTheAnswerTimeoutPassesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Claim_PastTheAttemptBudget_StopsForGood_AndCountsAsExhaustedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ClearsThePending_AndTheBucketWaitsForTheNextComparisonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_PastCap_RetriesAtTerminalCadenceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs:Claim_ALedgerFullOfSurpluses_ReadsOnlyTheDeficitsThroughThePartialIndexAsync</tests>
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
      AND l.answered_at IS NULL    -- 320: answered; the next comparison decides
      AND l.exhausted_at IS NULL   -- 320: the budget is spent
      AND l.origin_service_id = ANY(p_origin_ids)
      AND l.repair_attempts < p_max_attempts
      AND (l.answer_pending_until IS NULL OR l.answer_pending_until <= p_now)
      AND (l.last_repair_at IS NULL
           OR (p_now - l.last_repair_at) >= make_interval(
                secs => p_base_backoff_secs * POWER(2, LEAST(GREATEST(l.repair_attempts - 1, 0), 6))))
    ORDER BY l.last_repair_at ASC NULLS FIRST, l.first_seen_at ASC
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET repair_attempts = l.repair_attempts + 1,
      last_repair_at  = p_now,
      exhausted_at    = CASE WHEN l.repair_attempts + 1 >= p_max_attempts THEN p_now ELSE NULL END,
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
'Atomically claims up to p_limit repair-eligible DEFICIT ledger rows for the paced drain, least-recently-attempted first, SKIP LOCKED against concurrent drainers, attempt stamped on claim. Never a bucket marked unhealable (249), answered or exhausted, or with an answer pending (320); the backoff doubles up to 6 times and the claim that spends the last attempt marks the bucket exhausted. There is no terminal cadence.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_try_begin_repair');

-- Re-created from 249; the grant honors a pending answer and the budget, and the type lane stands down for its streams.
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_PastTheAttemptBudget_IsRefusedForGoodAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_WhileItsOwnAnswerIsPending_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_OnTheTypeLane_StandsDown_WhilePerStreamRowsOfTheTypeAwaitTheirAnswerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerSqlTests.cs:TryBeginRepairBatch_PastTheCap_IsRefusedForGoodAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginRepair_AnUnhealableBucket_IsRefusedPastItsTerminalCadenceAsync</tests>
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
    RETURN FALSE;
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
    RETURN FOUND;
  END IF;

  -- 249: a redelivery already showed repair cannot change this bucket; asking again ships the same events again.
  -- 320: nor while the answer to the last request is on the way, nor once the budget is spent.
  IF v_row.unhealable_at IS NOT NULL
     OR v_row.exhausted_at IS NOT NULL
     OR v_row.repair_attempts >= p_max_attempts
     OR v_row.answer_pending_until > p_now THEN
    RETURN FALSE;
  END IF;

  IF v_row.last_repair_at IS NOT NULL THEN
    -- Capped at 6 doublings so the wait cannot overflow, matching the in-memory ladder.
    v_doublings := LEAST(GREATEST(v_row.repair_attempts - 1, 0), 6);
    IF (p_now - v_row.last_repair_at) < make_interval(secs => p_base_backoff_secs * POWER(2, v_doublings)) THEN
      RETURN FALSE;
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

  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_repair(UUID, TEXT, TEXT, UUID, TIMESTAMPTZ, INTEGER, INTEGER) IS
'True when a repair should be requested now, on an exponential ladder (6 doublings at most) that ends at p_max_attempts: the grant that spends the last attempt marks the bucket exhausted and none follows until a reset (320). Never while the answer to the last request is pending, never for a bucket marked unhealable (249), and never on the type lane while per-stream rows of the type await their answers.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_mark_answer_pending');

-- Records that requests for these buckets left. A type-lane key (the empty stream) is the bulk or checkpoint request
-- for the whole type: its own row, and the per-stream rows of the type whose stamped window lies inside
-- [p_window_from, p_window_until] (every per-stream row of the type when either bound is NULL).
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Claim_ABucketWithItsAnswerPending_IsNotClaimed_UntilTheAnswerTimeoutPassesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:MarkAnswerPending_OnTheTypeLane_CoversThePerStreamRowsInsideItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_WhileItsOwnAnswerIsPending_IsRefusedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_answer_pending(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_window_from       BIGINT,
  p_window_until      BIGINT,
  p_now               TIMESTAMPTZ,
  p_timeout_secs      INTEGER
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_marked INTEGER;
BEGIN
  WITH keys AS (
    SELECT DISTINCT COALESCE(k.tenant, '') AS tenant, k.etype, k.sid
    FROM unnest(p_tenant_scopes, p_event_types, p_stream_ids) AS k(tenant, etype, sid)
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET answer_pending_until = p_now + make_interval(secs => p_timeout_secs),
      answered_at          = NULL,
      last_touched         = p_now
  WHERE l.origin_service_id = p_origin_service_id
    AND EXISTS (
      SELECT 1 FROM keys k
      WHERE l.tenant_scope = k.tenant AND l.event_type = k.etype
        AND (l.stream_id = k.sid
             OR (k.sid = __EMPTY_UUID__::uuid
                 AND (p_window_from IS NULL OR p_window_until IS NULL
                      OR (l.window_from >= p_window_from AND l.window_until <= p_window_until)))));
  GET DIAGNOSTICS v_marked = ROW_COUNT;
  RETURN v_marked;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_answer_pending(UUID, TEXT[], TEXT[], UUID[], BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'320 (#2029, #2034): records that repair requests for the keyed buckets left, so neither grant path asks again for p_timeout_secs or until a bundle answers. A type-lane key also covers the per-stream rows of its type inside the request''s window. Returns the rows marked.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_record_bundle_answer');

-- What a committed redelivery bundle answered, judged when its children commit. Per (stream, tenant, type) bucket:
-- the bundle's events the event store already held, those it did not (the bundle stores them), those the inbox
-- refused to restore because the stream was truncated here (252), and the present events the bundle could give an
-- origin or an origin sequence (the reconcile adopts them). A bucket keyed by a child the store holds takes the
-- stored event's key; one keyed by a child it does not takes the inbox row's, normalized as every digest fold keys
-- it. A child known to neither is counted in the row with NULL keys. marked says why this call marked the bucket
-- unhealable: refused_truncated (always), unchanged (every child was present or refused and none adoptable).
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ClearsThePending_AndTheBucketWaitsForTheNextComparisonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ABundleWhoseEventsWereAllStoredAlready_MarksTheBucketAtCommitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ChildrenTheBundleJustBrought_AreAbsentAtCommit_ThoughAReconcileAfterTheyAreStoredFindsThemPresentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_AStoredEventWithoutItsOrigin_IsLeftForTheReconcile_NotMarkedAtCommitAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_record_bundle_answer(
  p_origin_service_id UUID,
  p_event_ids         UUID[],
  p_commit_sequences  BIGINT[],
  p_now               TIMESTAMPTZ
) RETURNS TABLE(stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER, absent_count INTEGER,
                refused_truncated_count INTEGER, adoptable_count INTEGER, marked TEXT) AS $$
#variable_conflict use_column
DECLARE
  v_local_service_id UUID;
  v_adoptable        BOOLEAN;
  v_children         INTEGER;
  v_present          INTEGER;
  v_refused          INTEGER;
  v_adoptable_count  INTEGER;
  v_unchanged        BOOLEAN;
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_integrity_record_bundle_answer: % event ids but % commit sequences',
      cardinality(p_event_ids), cardinality(p_commit_sequences);
  END IF;

  -- The emit chain's normalization (087), as the reconcile applies it: the local service and the empty id adopt nothing.
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;
  v_adoptable := p_origin_service_id IS NOT NULL
    AND p_origin_service_id <> __EMPTY_UUID__::uuid
    AND p_origin_service_id IS DISTINCT FROM v_local_service_id;

  CREATE TEMP TABLE IF NOT EXISTS _wh_bundle_answer_rows (
    event_id UUID PRIMARY KEY,
    stream_id UUID,
    tenant_scope TEXT,
    event_type TEXT,
    present BOOLEAN NOT NULL,
    refused BOOLEAN NOT NULL,
    adoptable BOOLEAN NOT NULL
  ) ON COMMIT DROP;
  TRUNCATE _wh_bundle_answer_rows;

  INSERT INTO _wh_bundle_answer_rows
  SELECT
    m.eid,
    COALESCE(es.stream_id, i.stream_id),
    CASE WHEN es.event_id IS NOT NULL THEN COALESCE(es.scope ->> 't', '')
         WHEN i.message_id IS NOT NULL THEN COALESCE(i.scope ->> 't', '') END,
    CASE WHEN es.event_id IS NOT NULL THEN es.event_type
         WHEN i.message_id IS NOT NULL THEN __SCHEMA__.normalize_event_type(i.message_type) END,
    es.event_id IS NOT NULL,
    es.event_id IS NULL AND COALESCE(ist.error = __REPAIR_REFUSED_TRUNCATED__, false),
    es.event_id IS NOT NULL AND v_adoptable AND (
      es.origin_service_id IS NULL
      OR (es.origin_service_id = p_origin_service_id AND es.origin_commit_sequence IS NULL AND COALESCE(m.seq, 0) > 0))
  FROM (SELECT DISTINCT ON (u.eid) u.eid, u.seq FROM unnest(p_event_ids, p_commit_sequences) AS u(eid, seq) ORDER BY u.eid) m
  LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
  LEFT JOIN __SCHEMA__.wh_inbox i ON i.message_id = m.eid AND es.event_id IS NULL
  LEFT JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id;

  SELECT COUNT(*), COUNT(*) FILTER (WHERE r.present), COUNT(*) FILTER (WHERE r.refused), COUNT(*) FILTER (WHERE r.adoptable)
  INTO v_children, v_present, v_refused, v_adoptable_count
  FROM _wh_bundle_answer_rows r;
  v_unchanged := v_children > 0 AND v_present + v_refused >= v_children AND v_adoptable_count = 0;

  -- The answer landed: every bucket the bundle covered stops waiting, and its type lane too.
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET answer_pending_until = NULL,
      answered_at          = p_now,
      last_touched         = p_now
  FROM (SELECT DISTINCT r.tenant_scope, r.event_type, r.stream_id FROM _wh_bundle_answer_rows r WHERE r.stream_id IS NOT NULL) b
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = b.tenant_scope AND l.event_type = b.event_type
    AND (l.stream_id = b.stream_id OR l.stream_id = __EMPTY_UUID__::uuid);

  RETURN QUERY
  WITH buckets AS (
    SELECT r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype,
           (COUNT(*) FILTER (WHERE r.present))::int AS present,
           (COUNT(*) FILTER (WHERE NOT r.present AND NOT r.refused))::int AS absent,
           (COUNT(*) FILTER (WHERE r.refused))::int AS refused,
           (COUNT(*) FILTER (WHERE r.adoptable))::int AS adoptable
    FROM _wh_bundle_answer_rows r
    GROUP BY r.stream_id, r.tenant_scope, r.event_type
  ),
  to_mark AS (
    SELECT b.sid, b.tenant, b.etype,
           CASE WHEN b.refused > 0 THEN 'refused_truncated' ELSE 'unchanged' END AS reason
    FROM buckets b
    WHERE b.sid IS NOT NULL AND (b.refused > 0 OR (v_unchanged AND b.present > 0))
  ),
  marked_rows AS (
    UPDATE __SCHEMA__.wh_integrity_ledger l
    SET unhealable_at = p_now,
        last_touched  = p_now
    FROM to_mark t
    WHERE l.origin_service_id = p_origin_service_id
      AND l.tenant_scope = t.tenant AND l.event_type = t.etype AND l.stream_id = t.sid
      AND l.unhealable_at IS NULL
    RETURNING l.tenant_scope AS tenant, l.event_type AS etype, l.stream_id AS sid, t.reason
  )
  SELECT b.sid, b.tenant, b.etype, b.present, b.absent, b.refused, b.adoptable, m.reason
  FROM buckets b
  LEFT JOIN marked_rows m ON m.sid = b.sid AND m.tenant = b.tenant AND m.etype = b.etype
  ORDER BY b.sid, b.tenant, b.etype;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_record_bundle_answer(UUID, UUID[], BIGINT[], TIMESTAMPTZ) IS
'320 (#2029, #2033): records a committed redelivery bundle''s answer when its children commit. Per bucket: the events present in the event store, absent (the bundle stores them), refused as truncated (252) and adoptable; the covered ledger rows and their type lane stop waiting (answered); and the buckets of a bundle that changed nothing (all present or refused, none adoptable) and every refused bucket are marked unhealable, returned with the reason so each is reported once.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_gauges');

-- 090's wh_integrity_ledger_summary with the answer states and the integrity delivery queue (#2035). A new name, not a
-- redefinition: the summary's result columns change, and when the ledger replays 090 over a database that already ran
-- this file, 090's CREATE OR REPLACE cannot change a function's result type back and the replay fails. 090's summary is
-- left as it is for versions that still call it.
-- <docs>resilience/stream-integrity#integrity-gauges</docs>
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
  oldest_delivery_secs     DOUBLE PRECISION
)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  SELECT
    l.unhealed, l.exhausted, l.oldest, l.pending, l.answered, d.pending, d.oldest
  FROM (
    SELECT
      COUNT(*)::BIGINT AS unhealed,
      COUNT(*) FILTER (WHERE x.repair_attempts >= p_max_attempts OR x.exhausted_at IS NOT NULL)::BIGINT AS exhausted,
      COALESCE(EXTRACT(EPOCH FROM (NOW() - MIN(x.first_seen_at))), 0)::DOUBLE PRECISION AS oldest,
      COUNT(*) FILTER (WHERE x.answer_pending_until > NOW())::BIGINT AS pending,
      COUNT(*) FILTER (WHERE x.answered_at IS NOT NULL)::BIGINT AS answered
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
'Ledger as a gauge: unhealed buckets, how many have exhausted their repair budget, the age of the oldest, the buckets awaiting the answer to a request and those answered and waiting for the next comparison (320); and the integrity deliveries waiting for the maintainer with the age of the oldest (#2035).';
