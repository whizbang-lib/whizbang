-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 369_RepairAnswersAreAbsorbedCheaply.sql
-- Date: 2026-11-08
-- Description: A receiver absorbs redelivery (repair) answers without the per-call costs that made a burst of answers
--              load the shared database server more than the work itself.
--
--              Answers to repair requests can arrive in bulk long after the requests left (repair waits hours for an
--              answer), so the receive side, not the request rate, is where repair spends the database. Three costs
--              were paid per answer bundle that bore no relation to its size:
--
--              1. wh_integrity_record_bundle_answer gathered the bundle's children in a temporary working table. 321
--                 kept that table per connection, but a pooled connection is reset (DISCARD ALL) each time the pool
--                 hands it out, which drops every temporary table, so in practice the table was created again on
--                 almost every call: system catalog rows written and deleted (about 70 per call), a cache invalidation
--                 for every other session of the database, a fresh plan for every statement that read it, and, because
--                 the table's identity changed each time, a new statement-statistics entry for each of those statements
--                 on every call, which keeps an extension that tracks statements evicting entries server-wide. The
--                 children are now gathered into arrays by one statement; the logic and results are unchanged.
--              2. notify_instance_owners_with_payload judged each owner's liveness separately: _notify_debounced asked
--                 wh_instance_is_live per target, a SQL function that cannot be inlined (its body has a subquery), so
--                 each call planned the liveness rule afresh and read pg_locks, whose cost grows with the server's
--                 connection limit rather than with the work. Liveness is now read once per call
--                 (_wh_notify_debounced_for takes it as an argument); _notify_debounced keeps its signature and reads
--                 it for a direct caller.
--              3. commit_handler_result rang every stream of the inbox children it stored, after store_inbox_messages
--                 had already rung exactly the streams that needed it (114: a stream with no pending work; a duplicate
--                 is not new work). The handler commit predates 114 and so doubled the ring for a stored child and
--                 rang for duplicates and busy streams too; a repair answer is mostly duplicates. The commit now leaves
--                 the inbox ring to the store, as every other inbox path does. Its outbox and perspective rings are
--                 unchanged.
--
--              Number 369: 359 to 368 are taken on develop or by changes in flight.
--
-- Dependencies: 029 (commit_handler_result, copied with the delta above), 114 (store_inbox_messages rings only what
--               needs it), 317 (notify_instance_owners_with_payload and _notify_debounced, copied with the delta above;
--               wh_live_instance_ids, wh_instance_is_live), 357 (wh_integrity_record_bundle_answer, copied with the
--               delta above; _wh_integrity_answer_covers)
-- Objects: wh_integrity_record_bundle_answer, _wh_notify_debounced_for, _notify_debounced,
--          notify_instance_owners_with_payload, commit_handler_result
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_integrity_record_bundle_answer: last word 357, copied whole with the 369 delta (arrays, no temporary table).
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
-- <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ClearsThePending_AndTheBucketWaitsForTheNextComparisonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ABundleWhoseEventsWereAllStoredAlready_MarksTheBucketAtCommitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_ChildrenTheBundleJustBrought_AreAbsentAtCommit_ThoughAReconcileAfterTheyAreStoredFindsThemPresentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:RecordBundleAnswer_AStoredEventWithoutItsOrigin_IsLeftForTheReconcile_NotMarkedAtCommitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:RecordBundleAnswer_ASecondCallOnTheConnection_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:RecordBundleAnswer_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:RecordBundleAnswer_TwiceInOneTransaction_AnswersEachBundleForItsOwnEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_UnderAGenericPlan_FindsTheCoveredLedgerRowsByPrimaryKey_NotALedgerScanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_LocksTheRowsItWrites_InPrimaryKeyOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_ARowAlreadyAnswered_IsNotRewrittenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_ARowWithItsAnswerPending_IsStillAnsweredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AWindowedAnswer_LeavesAWholeHistoryBucketRepairable_ButAnsweredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AWholeHistoryAnswerThatStoredNothing_MarksAWholeHistoryBucketAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AWindowedBucket_IsMarkedOnlyByAnAnswerThatCoversItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AnAnswerCarryingTheNamedMissingEvent_MarksHoweverNarrowItsWindowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_record_bundle_answer(
  p_origin_service_id UUID,
  p_event_ids         UUID[],
  p_commit_sequences  BIGINT[],
  p_now               TIMESTAMPTZ,
  p_answer_window_known BOOLEAN DEFAULT false,
  p_answer_from       BIGINT DEFAULT NULL,
  p_answer_to         BIGINT DEFAULT NULL
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
  -- 369: the bundle's children, one element per distinct event, in parallel arrays (was a temporary table).
  v_w_streams        UUID[];
  v_w_tenants        TEXT[];
  v_w_types          TEXT[];
  v_w_present        BOOLEAN[];
  v_w_refused        BOOLEAN[];
  v_w_adoptable      BOOLEAN[];
  v_key_tenants      TEXT[];
  v_key_types        TEXT[];
  v_key_streams      UUID[];
  v_mark_tenants     TEXT[];
  v_mark_types       TEXT[];
  v_mark_streams     UUID[];
  v_mark_reasons     TEXT[];
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

  -- 369: the children are gathered into arrays, with the bundle's totals, by one statement. The temporary working
  -- table this replaced (320, kept per connection by 321) was dropped by every session reset, and a pooled connection
  -- is reset each time it is handed out, so in practice it was created again on almost every call: catalog rows written
  -- and deleted, an invalidation for every other session of the database, a fresh plan for each statement that read it,
  -- and, because the table's identity changed, new statement-statistics entries on every call.
  SELECT array_agg(w.sid), array_agg(w.tenant), array_agg(w.etype),
         array_agg(w.present), array_agg(w.refused), array_agg(w.adoptable),
         COUNT(*), COUNT(*) FILTER (WHERE w.present), COUNT(*) FILTER (WHERE w.refused), COUNT(*) FILTER (WHERE w.adoptable)
  INTO v_w_streams, v_w_tenants, v_w_types, v_w_present, v_w_refused, v_w_adoptable,
       v_children, v_present, v_refused, v_adoptable_count
  FROM (
    SELECT
      COALESCE(es.stream_id, i.stream_id) AS sid,
      CASE WHEN es.event_id IS NOT NULL THEN COALESCE(es.scope ->> 't', '')
           WHEN i.message_id IS NOT NULL THEN COALESCE(i.scope ->> 't', '') END AS tenant,
      CASE WHEN es.event_id IS NOT NULL THEN es.event_type::text
           WHEN i.message_id IS NOT NULL THEN __SCHEMA__.normalize_event_type(i.message_type) END AS etype,
      es.event_id IS NOT NULL AS present,
      es.event_id IS NULL AND COALESCE(ist.error = __REPAIR_REFUSED_TRUNCATED__, false) AS refused,
      es.event_id IS NOT NULL AND v_adoptable AND (
        es.origin_service_id IS NULL
        OR (es.origin_service_id = p_origin_service_id AND es.origin_commit_sequence IS NULL AND COALESCE(m.seq, 0) > 0))
        AS adoptable
    FROM (SELECT DISTINCT ON (u.eid) u.eid, u.seq FROM unnest(p_event_ids, p_commit_sequences) AS u(eid, seq) ORDER BY u.eid) m
    LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
    LEFT JOIN __SCHEMA__.wh_inbox i ON i.message_id = m.eid AND es.event_id IS NULL
    LEFT JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
  ) w;
  v_unchanged := v_children > 0 AND v_present + v_refused >= v_children AND v_adoptable_count = 0;

  -- 333 (#2093): the covered keys, each bucket once with its stream and once with the empty id (its type lane), are
  -- collected into arrays first, so each key is one primary key probe of the ledger.
  SELECT array_agg(k.tenant_scope), array_agg(k.event_type), array_agg(k.sid)
  INTO v_key_tenants, v_key_types, v_key_streams
  FROM (SELECT DISTINCT r.tenant_scope, r.event_type, ks.sid
        FROM unnest(v_w_streams, v_w_tenants, v_w_types) AS r(stream_id, tenant_scope, event_type)
        CROSS JOIN LATERAL (VALUES (r.stream_id), (__EMPTY_UUID__::uuid)) AS ks(sid)
        WHERE r.stream_id IS NOT NULL) k;

  -- 333 (#2093): the ledger rows either statement below may write are locked first, in primary key order, so two
  -- bundles that cover the same type lanes or buckets queue on them in one order instead of deadlocking (the joins
  -- below visit them in whatever order their plans choose). Rows neither statement will write are not locked.
  PERFORM COUNT(*) FROM (
    SELECT 1
    FROM unnest(v_key_tenants, v_key_types, v_key_streams) AS b(tenant_scope, event_type, sid)
    JOIN __SCHEMA__.wh_integrity_ledger l
      ON l.origin_service_id = p_origin_service_id
     AND l.tenant_scope = b.tenant_scope AND l.event_type = b.event_type AND l.stream_id = b.sid
    WHERE l.answered_at IS NULL OR l.answer_pending_until IS NOT NULL
       OR (l.stream_id <> __EMPTY_UUID__::uuid AND l.unhealable_at IS NULL)
    ORDER BY l.tenant_scope, l.event_type, l.stream_id
    FOR UPDATE OF l
  ) locked;

  -- The answer landed: every bucket the bundle covered stops waiting, and its type lane too.
  -- 333 (#2093): matched on all four primary key columns by equality; a row already answered with no answer pending
  -- is not rewritten (answered_at is read only as set or not set, and the type lane row is shared by every bundle of
  -- its type).
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET answer_pending_until = NULL,
      answered_at          = p_now,
      last_touched         = p_now
  FROM unnest(v_key_tenants, v_key_types, v_key_streams) AS b(tenant_scope, event_type, sid)
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = b.tenant_scope AND l.event_type = b.event_type AND l.stream_id = b.sid
    AND (l.answered_at IS NULL OR l.answer_pending_until IS NOT NULL);

  -- The buckets to mark, collected the same way (333, #2093): a refused bucket, and every bucket of a bundle that
  -- changed nothing.
  SELECT array_agg(t.tenant_scope), array_agg(t.event_type), array_agg(t.stream_id),
         array_agg(CASE WHEN t.refused > 0 THEN 'refused_truncated' ELSE 'unchanged' END)
  INTO v_mark_tenants, v_mark_types, v_mark_streams, v_mark_reasons
  FROM (SELECT r.stream_id, r.tenant_scope, r.event_type,
               COUNT(*) FILTER (WHERE r.present) AS present, COUNT(*) FILTER (WHERE r.refused) AS refused
        FROM unnest(v_w_streams, v_w_tenants, v_w_types, v_w_present, v_w_refused)
               AS r(stream_id, tenant_scope, event_type, present, refused)
        WHERE r.stream_id IS NOT NULL
        GROUP BY r.stream_id, r.tenant_scope, r.event_type) t
  WHERE t.refused > 0 OR (v_unchanged AND t.present > 0);

  RETURN QUERY
  WITH buckets AS (
    SELECT r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype,
           (COUNT(*) FILTER (WHERE r.present))::int AS present,
           (COUNT(*) FILTER (WHERE NOT r.present AND NOT r.refused))::int AS absent,
           (COUNT(*) FILTER (WHERE r.refused))::int AS refused,
           (COUNT(*) FILTER (WHERE r.adoptable))::int AS adoptable
    FROM unnest(v_w_streams, v_w_tenants, v_w_types, v_w_present, v_w_refused, v_w_adoptable)
           AS r(stream_id, tenant_scope, event_type, present, refused, adoptable)
    GROUP BY r.stream_id, r.tenant_scope, r.event_type
  ),
  marked_rows AS (
    UPDATE __SCHEMA__.wh_integrity_ledger l
    SET unhealable_at = p_now,
        last_touched  = p_now
    FROM unnest(v_mark_tenants, v_mark_types, v_mark_streams, v_mark_reasons) AS t(tenant, etype, sid, reason)
    WHERE l.origin_service_id = p_origin_service_id
      AND l.tenant_scope = t.tenant AND l.event_type = t.etype AND l.stream_id = t.sid
      AND l.unhealable_at IS NULL
      -- 357 (#2143): an unchanged bucket is marked only when the answer covered its deficit. A refusal is about the
      -- refused event itself, which no further request restores, so it marks whatever the window.
      AND (t.reason = 'refused_truncated'
           OR __SCHEMA__._wh_integrity_answer_covers(p_answer_window_known, p_answer_from, p_answer_to,
                                                     l.window_from, l.window_until, l.missing_event_id, p_event_ids))
    RETURNING l.tenant_scope AS tenant, l.event_type AS etype, l.stream_id AS sid, t.reason
  )
  SELECT b.sid, b.tenant, b.etype, b.present, b.absent, b.refused, b.adoptable, m.reason
  FROM buckets b
  LEFT JOIN marked_rows m ON m.sid = b.sid AND m.tenant = b.tenant AND m.etype = b.etype
  ORDER BY b.sid, b.tenant, b.etype;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_record_bundle_answer(UUID, UUID[], BIGINT[], TIMESTAMPTZ, BOOLEAN, BIGINT, BIGINT) IS
'369: the bundle''s children are gathered into arrays by one statement, with no temporary table, so a call on a freshly reset pooled connection writes nothing to the system catalog. 357 (#2143): a bucket of a bundle that changed nothing is marked unhealable only when the answer covered its deficit: the answered window (p_answer_from exclusive, p_answer_to inclusive) includes the row''s compared window, or the bundle carried the row''s named missing event; p_answer_window_known false (an origin or caller that does not say) marks as before. Other buckets are answered and stay repairable. 333 (#2093): the covered ledger rows are found by primary key (no ledger scan), and a row already answered with no answer pending is not rewritten. 320 (#2029, #2033): records a committed redelivery bundle''s answer when its children commit. Per bucket: the events present in the event store, absent (the bundle stores them), refused as truncated (252) and adoptable; the covered ledger rows and their type lane stop waiting (answered); and every refused bucket is marked unhealable, returned with the reason so each is reported once.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- _wh_notify_debounced_for: _notify_debounced (last word 317) copied whole, liveness taken as an argument (369).
SELECT __SCHEMA__.drop_all_overloads('_wh_notify_debounced_for');

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ManyOwners_JudgesLivenessOnceNotPerOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ALiveOwnerDraining_IsSuppressed_ButADeadOneIsRungAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/NotifyKindAndPayloadSqlTests.cs:NotifyInstanceOwners_KindAndPayloadAreIndependent_PayloadReachesTheWire_KindKeysTheStateAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_notify_debounced_for(
  p_instance_id UUID,
  p_kind TEXT,
  p_payload TEXT,
  p_window INTEGER,
  p_live BOOLEAN
) RETURNS VOID AS $$
DECLARE
  v_channel TEXT := 'wh_work_i_' || p_instance_id::text;
  v_now TIMESTAMPTZ := NOW();
  -- 369: the caller judged liveness once for every target of its call; NULL counts as not live.
  v_live BOOLEAN := COALESCE(p_live, false);
  v_last_attempt TIMESTAMPTZ;
  v_last_work TIMESTAMPTZ;
  v_rapid_run INTEGER;
  v_floor_ms INTEGER;
  v_rapid_gap_ms INTEGER;
  v_churn_run INTEGER;
  v_gap_ms DOUBLE PRECISION;
  v_effective_ms INTEGER;
  v_suppress BOOLEAN := FALSE;
BEGIN
  -- Adaptive knobs (the ceiling arrives as p_window; read the floor/rapid-gap/churn once).
  SELECT COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_debounce_floor_ms'), 50),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_rapid_gap_ms'), 100),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_churn_run'), 5)
  INTO v_floor_ms, v_rapid_gap_ms, v_churn_run;

  -- Only a LIVE target may ever be suppressed: a corpse's fresh watermark must not strand work
  --: its doorbell must fire so the deterministic re-target path engages (130). Liveness arrives as p_live, judged by
  -- the one rule (317, #2027) once per caller rather than once per target (369).

  -- This target's current state, taken WITHOUT waiting (143). The row lock we take here lives
  -- until the caller's transaction ends, which is exactly why the next writer must skip it.
  SELECT last_attempt_at, last_work_at, rapid_run
    INTO v_last_attempt, v_last_work, v_rapid_run
  FROM __SCHEMA__.wh_notify_state
  WHERE instance_id = p_instance_id AND payload_kind = p_kind
  FOR UPDATE SKIP LOCKED;

  IF NOT FOUND THEN
    -- Held by another writer (it is ringing or sliding this target right now), or the row does
    -- not exist yet. Held: ring and leave the state to the holder. Absent: one creator at a time;
    -- a loser rings and returns rather than queue behind the winner's transaction.
    IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_notify_state
               WHERE instance_id = p_instance_id AND payload_kind = p_kind)
       OR NOT pg_try_advisory_xact_lock(hashtext(p_instance_id::text), hashtext(p_kind)) THEN
      PERFORM __SCHEMA__._queue_doorbell(v_channel, p_payload);
      RETURN;
    END IF;

    -- Born without a found-work watermark: only claim_work arms last_work_at (131/137).
    INSERT INTO __SCHEMA__.wh_notify_state
      (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run, effective_window_ms,
       fired_count, suppressed_count)
    VALUES (p_instance_id, p_kind, NULL, NULL, 0, 0, 0, 0)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    IF NOT FOUND THEN
      -- Committed by someone else between the probe and the insert: theirs, not ours.
      PERFORM __SCHEMA__._queue_doorbell(v_channel, p_payload);
      RETURN;
    END IF;
    -- The row is ours (uncommitted insert): state is empty, the same as a first doorbell after idle.
  END IF;

  -- VOLUME axis: advance the rapid run while doorbells arrive closer than the rapid gap; reset
  -- on the first calm gap. No prior attempt = calm (a lone doorbell after idle).
  v_gap_ms := CASE WHEN v_last_attempt IS NULL THEN NULL
                   ELSE EXTRACT(EPOCH FROM (v_now - v_last_attempt)) * 1000 END;
  IF v_gap_ms IS NOT NULL AND v_gap_ms < v_rapid_gap_ms THEN
    v_rapid_run := COALESCE(v_rapid_run, 0) + 1;
  ELSE
    v_rapid_run := 0;
  END IF;

  -- TIME axis: floor window normally; escalate to the ceiling (p_window seconds) once the run
  -- trips churn. p_window <= 0 is the global OFF switch: suppression disabled (floor ignored).
  IF p_window <= 0 THEN
    v_effective_ms := 0;
  ELSIF v_rapid_run >= v_churn_run THEN
    v_effective_ms := p_window * 1000;
  ELSE
    v_effective_ms := GREATEST(v_floor_ms, 0);
  END IF;

  -- Suppress iff: live AND the drainer is genuinely draining this kind (found-work watermark
  -- fresh within the EFFECTIVE window). A NULL last_work_at (fire-born row, claim_work never
  -- armed) can never satisfy this: "a fire never arms suppression" (131), by construction.
  IF v_live AND v_effective_ms > 0 AND v_last_work IS NOT NULL
     AND v_last_work > v_now - (v_effective_ms * INTERVAL '1 millisecond') THEN
    v_suppress := TRUE;
  END IF;

  IF v_suppress THEN
    -- Slide the found-work watermark (a suppressed store IS work the linger poll will find) and
    -- record the attempt + rate state + the suppressed count for OTel. The row is ours.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_work_at = v_now,
        last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        suppressed_count = suppressed_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_kind;
  ELSE
    PERFORM __SCHEMA__._queue_doorbell(v_channel, p_payload);
    -- Record the attempt + rate state + fired count WITHOUT arming suppression: last_work_at is
    -- left untouched (only claim_work arms it). The row is ours.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        fired_count = fired_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_kind;
    -- Opportunistic hygiene on the rare fire path: rows for long-departed instances, aged by
    -- most-recent activity (either watermark) so fire-born NULL-last_work_at rows also expire.
    -- Never waits: a row someone holds is, by definition, not long-departed.
    DELETE FROM __SCHEMA__.wh_notify_state ns
    WHERE ns.ctid IN (
      SELECT stale.ctid FROM __SCHEMA__.wh_notify_state stale
      WHERE GREATEST(COALESCE(stale.last_work_at, 'epoch'::timestamptz),
                     COALESCE(stale.last_attempt_at, 'epoch'::timestamptz)) < v_now - INTERVAL '7 days'
      FOR UPDATE SKIP LOCKED);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_notify_debounced_for(UUID, TEXT, TEXT, INTEGER, BOOLEAN) IS
'369: one doorbell with adaptive debounce (130, 131, 137, 143, 146) for a target whose liveness the caller already judged by the one rule (317): only a live target that found work within the effective window is suppressed; every other target is rung. Called by notify_instance_owners_with_payload, which reads liveness once per call, and by _notify_debounced.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- _notify_debounced: kept for a direct caller, with its signature; it judges the one target's liveness by the rule and
-- hands the rest to _wh_notify_debounced_for (369). Exactly one overload per framework function.
SELECT __SCHEMA__.drop_all_overloads('_notify_debounced');

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/NotifyDebounceEscalationSqlTests.cs:Debounce_EscalatedWindowAndFreshWatermark_SuppressesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._notify_debounced(
  p_instance_id UUID,
  p_kind TEXT,
  p_payload TEXT,
  p_window INTEGER
) RETURNS VOID AS $$
BEGIN
  PERFORM __SCHEMA__._wh_notify_debounced_for(
    p_instance_id, p_kind, p_payload, p_window, __SCHEMA__.wh_instance_is_live(p_instance_id));
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._notify_debounced(UUID, TEXT, TEXT, INTEGER) IS
'One doorbell with adaptive debounce for a single target: judges the target''s liveness by the one rule (317) and rings or suppresses through _wh_notify_debounced_for (369). A caller ringing several targets reads liveness once and calls _wh_notify_debounced_for itself.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- notify_instance_owners_with_payload: last word 317, copied with the 369 delta: the live instances are read once, and
-- every target's liveness and the deterministic rank come from that one read.
-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ManyOwners_JudgesLivenessOnceNotPerOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ALiveOwnerDraining_IsSuppressed_ButADeadOneIsRungAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/NotifyKindAndPayloadSqlTests.cs:NotifyInstanceOwners_KindAndPayloadAreIndependent_PayloadReachesTheWire_KindKeysTheStateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:NotifyOwners_TargetsOnlyInstancesTheRuleCallsLiveAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.notify_instance_owners_with_payload(
  p_kind TEXT,
  p_payload TEXT,
  p_stream_ids UUID[]
) RETURNS VOID AS $$
DECLARE
  v_unclaimed_streams UUID[];
  v_active_count INTEGER;
  v_debounce INTEGER;
  -- 369: the live instances in instance id order, read once; element n + 1 is the instance of rank n.
  v_live UUID[];
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
     WHERE setting_key = 'notify_debounce_seconds'), 7)
  INTO v_debounce;

  -- 369: one read of the liveness rule (317) for the whole call. The rule reads pg_locks, whose cost grows with the
  -- server's connection limit; judged per target, it was planned and run again for every owner rung.
  SELECT COALESCE(array_agg(l.instance_id ORDER BY l.instance_id), ARRAY[]::UUID[])
  INTO v_live
  FROM __SCHEMA__.wh_live_instance_ids() l;

  -- Step 1 (unchanged targeting): per-owner notify for streams in wh_active_streams.
  PERFORM __SCHEMA__._wh_notify_debounced_for(a.assigned_instance_id, p_kind, p_payload, v_debounce,
                                             a.assigned_instance_id = ANY(v_live))
  FROM (
    SELECT DISTINCT assigned_instance_id
    FROM __SCHEMA__.wh_active_streams
    WHERE stream_id = ANY(p_stream_ids)
      AND assigned_instance_id IS NOT NULL
  ) a;

  -- Step 2 (unchanged targeting): deterministic-target notify for unclaimed streams.
  SELECT ARRAY_AGG(s) INTO v_unclaimed_streams
  FROM unnest(p_stream_ids) AS s
  WHERE NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = s
      AND a.assigned_instance_id IS NOT NULL
  );

  IF v_unclaimed_streams IS NULL OR cardinality(v_unclaimed_streams) = 0 THEN
    RETURN;
  END IF;

  v_active_count := cardinality(v_live);

  IF v_active_count = 0 THEN
    RETURN;
  END IF;

  -- The partition number is COMPUTED, never read from a queue table (see 317 and its predecessors): the stream
  -- ledger's number first, compute_partition only for a stream the ledger has never held, so the doorbell wakes the
  -- instance the claim itself routes the stream to. A doorbell kind other than these three (schedule) and a signal's
  -- wire name reach no branch, as before. Every target here is live by construction (it is drawn from v_live).
  IF p_kind IN (__CATEGORY_OUTBOX__, __CATEGORY_INBOX__, __CATEGORY_PERSPECTIVE__) THEN
    PERFORM __SCHEMA__._wh_notify_debounced_for(targets.target_instance_id, p_kind, p_payload, v_debounce, true)
    FROM (
      SELECT DISTINCT v_live[1 + (
        COALESCE(
          (SELECT ast.partition_number
           FROM __SCHEMA__.wh_active_streams ast
           WHERE ast.stream_id = s),
          __SCHEMA__.compute_partition(s)
        ) % v_active_count)] AS target_instance_id
      FROM unnest(v_unclaimed_streams) AS s
    ) AS targets
    -- A number outside 0..count-1 (only a negative stored partition) matched no rank before and rings no one now.
    WHERE targets.target_instance_id IS NOT NULL;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_instance_owners_with_payload(TEXT, TEXT, UUID[]) IS
'Instance-routed NOTIFY emission with doorbell debounce (slice 27 + v0.685 + 130, kind split from payload in 141). 369: the live instances are read once per call, and each target''s liveness and the deterministic rank come from that read (the rule reads pg_locks, so judging it per target cost a plan and a pg_locks read per owner). Per-owner for pinned streams; rank-deterministic for unclaimed outbox/inbox/perspective streams, where the partition number is the stream ledger''s, else computed through compute_partition, never read out of a queue table. Every emission goes through _wh_notify_debounced_for(instance, p_kind, p_payload, window, live). Signals call this form with their wire name as both, and reach only the per-owner path.';


-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- commit_handler_result: last word 029, copied whole with the 369 delta (the inbox ring is left to the store).
SELECT __SCHEMA__.drop_all_overloads('commit_handler_result');

-- <docs>fundamentals/work-coordinator/handler-commit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:CommitHandlerResult_AChildOnAStreamWithNoPendingWork_RingsItsOwnerOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:CommitHandlerResult_AChildAlreadySeen_RingsNoOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:CommitHandlerResult_AChildOnAStreamWithPendingWork_RingsNoOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HappyPath_MarksInboxProcessedAndStoresOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HandlerEmitsEvent_StoresInEventStoreAndCreatesPerspectiveEventAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_result(
  p_request JSONB
) RETURNS VOID AS $$
DECLARE
  v_instance_id UUID := (p_request ->> 'instance_id')::UUID;
  v_now TIMESTAMPTZ := NOW();
  v_lease_expiry TIMESTAMPTZ := v_now + INTERVAL '300 seconds';
  v_partition_count INTEGER := COALESCE((p_request ->> 'partition_count')::INTEGER, 10000);
  -- Production: completions DELETE rows. Debug: retain with processed_at stamped.
  -- Caller (C# OutboxDrainWorker / InboxDrainWorker / completion flushers) reads from
  -- IWorkCoordinatorStrategy.DebugMode and sets this on the request.
  v_debug_mode BOOLEAN := COALESCE((p_request ->> 'debug_mode')::BOOLEAN, FALSE);
  v_inbox_completion JSONB := p_request -> 'inbox_completion';
  v_new_outbox JSONB := COALESCE(p_request -> 'new_outbox_messages', '[]'::JSONB);
  v_new_inbox JSONB := COALESCE(p_request -> 'new_inbox_messages', '[]'::JSONB);
  v_outbox_inserted INTEGER := 0;
  -- Slice 27: instance-routed NOTIFY. Collect stream IDs from the new outbox/inbox
  -- payloads so we can route NOTIFYs only to the owning instance(s) of those streams
  -- instead of broadcasting on a global wh_work channel.
  v_outbox_stream_ids UUID[];
BEGIN
  -- 1. Mark inbox completion (if present). process_inbox_completions takes a JSONB array;
  --    wrap the single completion in an array.
  IF v_inbox_completion IS NOT NULL AND v_inbox_completion::TEXT != 'null' THEN
    PERFORM __SCHEMA__.process_inbox_completions(
      jsonb_build_array(v_inbox_completion),
      v_now,
      v_debug_mode  -- production: DELETE; debug: stamp processed_at and retain
    );
  END IF;

  -- 2. Store new outbox messages emitted by the handler.
  IF jsonb_array_length(v_new_outbox) > 0 THEN
    PERFORM __SCHEMA__.store_outbox_messages(
      v_new_outbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
    v_outbox_inserted := jsonb_array_length(v_new_outbox);

    -- Collect stream_ids for slice 27's routed NOTIFY.
    SELECT array_agg(DISTINCT (elem ->> __ENVELOPE_FIELD_STREAM_ID__)::UUID)
    INTO v_outbox_stream_ids
    FROM jsonb_array_elements(v_new_outbox) AS elem
    WHERE elem ->> __ENVELOPE_FIELD_STREAM_ID__ IS NOT NULL;

    -- 2.5. Event-store auto-create chain. For events with stream_id, copy into
    -- wh_event_store and auto-create wh_perspective_events rows for matching
    -- perspectives. Without this step, handler-emitted events sit in wh_outbox
    -- but never reach perspective consumers.
    DECLARE
      v_emitted_ids UUID[];
    BEGIN
      SELECT array_agg((elem ->> __ENVELOPE_FIELD_MESSAGE_ID__)::UUID)
      INTO v_emitted_ids
      FROM jsonb_array_elements(v_new_outbox) AS elem
      WHERE (elem ->> 'IsEvent')::BOOLEAN = true
        AND elem ->> __ENVELOPE_FIELD_STREAM_ID__ IS NOT NULL;

      IF v_emitted_ids IS NOT NULL AND cardinality(v_emitted_ids) > 0 THEN
        PERFORM __SCHEMA__._emit_event_store_chain(
          v_emitted_ids, v_instance_id, v_lease_expiry, v_now, v_partition_count
        );
      END IF;
    END;
  END IF;

  -- 3. Store new inbox messages emitted by the handler (a composite's children, a redelivery bundle's among them).
  --    369: the store rings exactly the streams that need it (114: a stream with no pending work; a duplicate is no
  --    new work), as on every other inbox path, so the commit rings nothing more for them.
  IF jsonb_array_length(v_new_inbox) > 0 THEN
    PERFORM __SCHEMA__.store_inbox_messages(
      v_new_inbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
  END IF;

  -- 4. NOTIFY signal types via slice 27's instance-routed helper. Listeners on each
  --    instance subscribe to wh_work_i_<their_instance_id> only; non-owners never
  --    wake. pg_notify still deduplicates (channel, payload) within a transaction at
  --    COMMIT, so 10 000 inserts targeting the same owner collapse to one delivered
  --    notification per (owner, category) pair. Streams without a known owner in
  --    wh_active_streams emit zero NOTIFYs; polling backstop catches that work.
  IF v_outbox_inserted > 0 AND v_outbox_stream_ids IS NOT NULL THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_OUTBOX__, v_outbox_stream_ids);
    -- Event-store auto-create chain may have queued perspective events for the same
    -- streams. Route the perspective wake to the same owners.
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_outbox_stream_ids);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.commit_handler_result(JSONB) IS
'Atomic transactional bundle for inbox handler completion. Marks the inbox completion AND stores the new outbox/inbox messages the handler emitted in one transaction; if any step fails the whole bundle rolls back. Handler-emitted events with a stream_id flow through _emit_event_store_chain which copies them into wh_event_store and creates matching wh_perspective_events rows so perspective consumers see them. Rings the owners of the outbox streams (outbox and perspective). 369: new inbox messages are rung by store_inbox_messages alone, which rings only a stream with no pending work and never a duplicate; the commit ringing every inbox stream again doubled the ring and woke owners for duplicates and busy streams. Phase A of work-pump decomposition.';
