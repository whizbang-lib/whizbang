-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 403_TheLedgerCountsWhatItOpensAndCloses.sql (#2256): the nine ledger functions 399 turned into wrappers
-- are restored to their last words before 399 (090, 094, 320, 331, 370 and 384), and the counting forms and the backlog
-- read 399 added are dropped. An older release calls only the restored names, which answer exactly as they did before
-- 399. 399 stored no data, so the reverse loses none.

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_try_begin_report: 320's definition, whole.
-- ---------------------------------------------------------------------------------------------
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

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_report_divergences_with_deficits: 320's definition, whole.
-- ---------------------------------------------------------------------------------------------
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

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_try_begin_repair: 320's definition, whole.
-- ---------------------------------------------------------------------------------------------
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

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_try_begin_repair_batch: 094's definition, whole.
-- ---------------------------------------------------------------------------------------------
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheReverse_RestoresThePreviousDefinitions_AndDropsTheCountingFormsAsync</tests>
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
LANGUAGE plpgsql
AS $$
DECLARE
  v_n       INTEGER := COALESCE(array_length(p_stream_ids, 1), 0);
  v_results BOOLEAN[] := '{}';
  v_granted INTEGER := 0;
  v_one     BOOLEAN;
  v_i       INTEGER;
BEGIN
  FOR v_i IN 1 .. v_n LOOP
    IF v_granted >= p_max_grants THEN
      -- Budget spent: do not even ASK: the single function records an attempt when it grants,
      -- and a grant the caller must discard burns backoff budget for nothing.
      v_results := v_results || FALSE;
      CONTINUE;
    END IF;
    v_one := __SCHEMA__.wh_integrity_try_begin_repair(
      p_origin_service_id, p_tenant_scopes[v_i], p_event_types[v_i], p_stream_ids[v_i],
      p_now, p_base_backoff_secs, p_max_attempts);
    IF v_one THEN
      v_granted := v_granted + 1;
    END IF;
    v_results := v_results || v_one;
  END LOOP;
  RETURN v_results;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_repair_batch IS
'One round trip for a chunk''s repair decisions, capped at p_max_grants IN ORDER: past the cap keys are not consulted at all, so no attempt budget is burned on grants the caller cannot use.';

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_mark_healed: 090's definition, whole.
-- ---------------------------------------------------------------------------------------------
-- The bucket folded identical: forget it. A later divergence is a brand-new incident.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheReverse_RestoresThePreviousDefinitions_AndDropsTheCountingFormsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_healed(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id
    AND tenant_scope = COALESCE(p_tenant_scope, '')
    AND event_type = p_event_type
    AND stream_id = p_stream_id;
END;
$$;

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_mark_healed_batch: 094's definition, whole.
-- ---------------------------------------------------------------------------------------------
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerBacklogSqlTests.cs:TheReverse_RestoresThePreviousDefinitions_AndDropsTheCountingFormsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[]
) RETURNS SETOF DOUBLE PRECISION
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
      AND l.tenant_scope      = k.tenant_scope
      AND l.event_type        = k.event_type
      AND l.stream_id         = k.stream_id
    RETURNING l.first_seen_at
  )
  -- first_seen_at is the reporting caller's clock (p_now), NOW() is the database's. A host running
  -- slightly ahead of the database would otherwise report a negative age for a bucket healed moments
  -- after it was first seen, and a repair-latency histogram cannot hold a negative duration. Clamped
  -- at zero: the skew is below what the metric resolves.
  SELECT GREATEST(EXTRACT(EPOCH FROM (NOW() - h.first_seen_at)), 0)::DOUBLE PRECISION
  FROM healed h;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch IS
'One round trip to forget a chunk''s healed buckets, returning each healed bucket''s age in seconds (first sighting -> heal): read from the rows the delete destroys.';

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_mark_types_healed: 384's definition, whole.
-- ---------------------------------------------------------------------------------------------
-- Forgets every ledger row of the given (tenant, type) pairs of one origin, the type lane and every per-stream row,
-- returning each forgotten row's age in seconds (first sighting to this heal), clamped at zero for clock skew (094).
-- An absent tenant keys as the ledger keys it, the empty scope.
-- <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeHealSqlTests.cs:MarkTypesHealed_ForgetsTheTypeLaneAndEveryStreamRow_OfTheProvenTypesOnly_WithTheirAgesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeHealSqlTests.cs:MarkTypesHealed_NothingTracked_IsAnEmptyAnswerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_types_healed(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[]
) RETURNS SETOF DOUBLE PRECISION
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
    RETURNING l.first_seen_at
  )
  SELECT GREATEST(EXTRACT(EPOCH FROM (NOW() - h.first_seen_at)), 0)::DOUBLE PRECISION
  FROM healed h;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_types_healed(UUID, TEXT[], TEXT[]) IS
'384 (phantom heal, follow-up to #2207): forgets every ledger row (the type lane and every per-stream row) of the given (tenant, type) pairs of one origin, which a comparison proved complete over the whole history, returning each forgotten row''s age in seconds.';

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_forget_unconsumed: 331's definition, whole.
-- ---------------------------------------------------------------------------------------------
-- Deletes the ledger rows of event types the service does not keep: not in p_consumed_event_types (the
-- assembly-qualified wire form) and not in a namespace of p_absorbed_namespaces (lowercase). The namespace of a row's
-- type is the part of its name before the last '.', the form the receive boundary derives, so a nested type
-- ("Ns.Outer+Inner") belongs to "ns". A null consumed set forgets nothing: only an explicit set may empty the ledger.
-- Returns the rows deleted. The ledger is bounded (090 reclaims past a ceiling), so the scan is small and runs once
-- per audit cycle.
-- <docs>resilience/stream-integrity#the-integrity-surface-is-what-the-service-consumes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_RemovesEveryRowOfAnUnconsumedType_AndKeepsTheConsumedOnesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_AnEmptyConsumedSet_RemovesEveryRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_KeepsTheRowsOfATypeInAnAbsorbedNamespaceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_ANullConsumedSet_ForgetsNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed(
  p_consumed_event_types TEXT[],
  p_absorbed_namespaces  TEXT[]
) RETURNS INTEGER
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_deleted INTEGER;
BEGIN
  IF p_consumed_event_types IS NULL THEN
    RETURN 0;
  END IF;
  DELETE FROM __SCHEMA__.wh_integrity_ledger l
  WHERE l.event_type <> ALL (p_consumed_event_types)
    AND lower(COALESCE(substring(split_part(l.event_type, ', ', 1) FROM '^(.*)[.][^.]*$'), ''))
        <> ALL (COALESCE(p_absorbed_namespaces, ARRAY[]::TEXT[]));
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  RETURN v_deleted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed(TEXT[], TEXT[]) IS
'331 (#2058): deletes the integrity ledger rows of event types the service neither consumes nor absorbs. Such a type is never stored here, so its buckets are deficits whose redelivery the receive boundary discards: they can never heal. Called by the integrity audit each cycle with the consumed set; a null set forgets nothing. Returns the rows deleted.';

-- ---------------------------------------------------------------------------------------------
-- wh_integrity_forget_origin_types: 370's definition, whole.
-- ---------------------------------------------------------------------------------------------
-- Deletes every ledger row of the given (origin, type) pairs, in every tenant and stream. Pairs are positional across
-- the two arrays. A null or empty list forgets nothing. Returns the rows deleted.
-- <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_RemovesEveryRowOfAJudgedPair_AndKeepsTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_AnEmptyPairList_RemovesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_origin_types(
  p_origin_ids  UUID[],
  p_event_types TEXT[]
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_deleted INTEGER;
BEGIN
  IF p_origin_ids IS NULL OR p_event_types IS NULL OR cardinality(p_origin_ids) = 0 THEN
    RETURN 0;
  END IF;
  DELETE FROM __SCHEMA__.wh_integrity_ledger l
  USING unnest(p_origin_ids, p_event_types) AS p(origin_service_id, event_type)
  WHERE l.origin_service_id = p.origin_service_id
    AND l.event_type = p.event_type;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  RETURN v_deleted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_origin_types(UUID[], TEXT[]) IS
'370: deletes every integrity ledger row of the given (origin, event type) pairs, which the integrity audit judged to be recorded under an origin that does not own the type: such a row is never compared again, so it can neither heal nor clear. A null or empty list forgets nothing. Returns the rows deleted.';

-- ---------------------------------------------------------------------------------------------
-- The counting forms and the backlog read 399 added.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_report_divergences_counted');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_report_outcome');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_repair_batch_counted');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_repair_outcome');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_heal_counted');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_heal_batch_counted');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_heal_types_counted');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_forget_unconsumed_counted');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_forget_origin_types_counted');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_backlog');
