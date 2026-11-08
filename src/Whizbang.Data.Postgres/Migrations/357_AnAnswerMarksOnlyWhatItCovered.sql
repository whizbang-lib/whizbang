-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 357_AnAnswerMarksOnlyWhatItCovered.sql
-- Date: 2026-11-07
-- Description: A redelivery answer marks a bucket unhealable only when it covered the bucket's deficit (#2143).
--
--              Audit-driven repairs and bulk backfills ask the origin for the manifest's compared window. An answer for a
--              recent window carries only the stream's recent events; when the consumer already holds those, the answer
--              stores nothing, and the bundle answer (and the reconcile after it) marked every bucket the bundle touched
--              as one repair cannot change. That included buckets whose missing events are older than the window, most
--              of them whole-history deficits (window_from NULL) that had never had a repair request of their own, so
--              repair stopped on real gaps.
--
--              The origin now echoes the window it answered on every bundle, and the receiver passes it here:
--              1. _wh_integrity_answer_covers (new): whether an answer covered a ledger row's deficit. The answered
--                 window (exclusive floor, inclusive ceiling, null = unbounded) must include the row's compared window
--                 (stamped [window_from, window_until), 097); a whole-history row (window_from NULL) needs a
--                 whole-history answer. Or the answer carried the row's named missing event (338, #2103). An answer
--                 whose window is not known (an older origin or caller) covers everything, as before.
--              2. wh_integrity_record_bundle_answer: copied whole from 333, plus three trailing parameters with
--                 defaults (the answered window), so a four-argument call from an older release still resolves and marks
--                 as before. An unchanged bucket the answer did not cover is answered and not marked.
--              3. wh_integrity_mark_unhealable_batch: copied whole from 249, plus the answered window and the
--                 bundle's events as trailing defaulted parameters, for the reconcile's mark.
--              4. wh_integrity_rearm_narrow_answers (new), run once below: re-arms the per-stream whole-history rows an
--                 earlier release marked unhealable with no repair attempt of their own (another request's answer
--                 marked them). Which answer marked a row is not recorded, so a row with an attempt of its own, or a
--                 windowed row, is left as it is; a wrongly marked one of those heals on its next local change.
--
--              The correction is a migration step, not a maintenance step: maintenance is idle-gated, and a service
--              whose repair stalled this way may never be idle, which is when the rows matter. It runs once per
--              database (a wh_settings marker, README rule 6), and it is bounded by the ledger, which is capped
--              (090): one pass over the ledger, touching only the rows it re-arms. A re-armed row has its answer
--              cleared too, so the paced drain claims it and asks for its whole history, and a whole-history answer
--              that still stores nothing marks it again, correctly.
--
--              Cost on the hot statement: the coverage check is evaluated only on ledger rows the mark already found
--              by primary key (333), so the plan is unchanged (IntegrityBundleAnswerPlanShapeTests).
--
--              Number 357: 351 to 356 are taken by changes in flight.
--
-- Dependencies: 090 (wh_integrity_ledger), 097 (window_from, window_until), 249 (unhealable_at,
--               wh_integrity_mark_unhealable_batch, copied with the delta above), 320 (answered_at), 333
--               (wh_integrity_record_bundle_answer, copied with the delta above), 338 (missing_event_id)
-- Objects: _wh_integrity_answer_covers, wh_integrity_record_bundle_answer, wh_integrity_mark_unhealable_batch,
--          wh_integrity_rearm_narrow_answers; wh_settings row integrity_rearm_narrow_answers_version
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Whether a redelivery answer covered a ledger row's deficit. The answer's window is in the request's units: the
-- events after p_answer_from up to and including p_answer_to, NULL = unbounded. The row's compared window is stamped
-- as [window_from, window_until) (097); as an answer it is the floor window_from - 1 (none when window_from is 0) and
-- the ceiling window_until - 1. A row without a stamped window is whole-history. An answer whose window is not known
-- covers every row, as before 357.
SELECT __SCHEMA__.drop_all_overloads('_wh_integrity_answer_covers');
-- <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AWindowedBucket_IsMarkedOnlyByAnAnswerThatCoversItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:MarkUnhealable_WithAWindow_MarksOnlyTheRowsTheAnswerCoveredAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_integrity_answer_covers(
  p_answer_window_known BOOLEAN,
  p_answer_from         BIGINT,
  p_answer_to           BIGINT,
  p_window_from         BIGINT,
  p_window_until        BIGINT,
  p_missing_event_id    UUID,
  p_event_ids           UUID[]
) RETURNS BOOLEAN
LANGUAGE sql IMMUTABLE
AS $$
  SELECT NOT COALESCE(p_answer_window_known, false)
      OR (p_missing_event_id IS NOT NULL AND p_missing_event_id = ANY(COALESCE(p_event_ids, ARRAY[]::uuid[])))
      OR ((p_answer_from IS NULL
           OR (p_window_from IS NOT NULL AND p_window_until IS NOT NULL AND p_window_from > 0
               AND p_answer_from <= p_window_from - 1))
          AND (p_answer_to IS NULL
           OR (p_window_from IS NOT NULL AND p_window_until IS NOT NULL AND p_answer_to >= p_window_until - 1)));
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_integrity_answer_covers(BOOLEAN, BIGINT, BIGINT, BIGINT, BIGINT, UUID, UUID[]) IS
'357 (#2143): whether a redelivery answer covered a ledger row''s deficit: its window includes the row''s compared window (a row without one is whole-history and needs a whole-history answer), or it carried the row''s named missing event. An answer whose window is not known covers every row.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_integrity_record_bundle_answer: last word 333, copied whole with the 357 delta (the answered window).
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:RecordBundleAnswer_ASecondCallOnTheConnection_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:RecordBundleAnswer_TwiceInOneTransaction_AnswersEachBundleForItsOwnEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_UnderAGenericPlan_FindsTheCoveredLedgerRowsByPrimaryKey_NotALedgerScanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_LocksTheRowsItWrites_InPrimaryKeyOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_ARowAlreadyAnswered_IsNotRewrittenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityBundleAnswerPlanShapeTests.cs:RecordBundleAnswer_ARowWithItsAnswerPending_IsStillAnsweredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AWindowedAnswer_LeavesAWholeHistoryBucketRepairable_ButAnsweredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AWholeHistoryAnswerThatStoredNothing_MarksAWholeHistoryBucketAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AWindowedBucket_IsMarkedOnlyByAnAnswerThatCoversItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:RecordBundleAnswer_AnAnswerCarryingTheNamedMissingEvent_MarksHoweverNarrowItsWindowAsync</tests>
-- <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
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

  -- 321: made once per connection and emptied at commit, not created and dropped per call (see the header).
  CREATE TEMP TABLE IF NOT EXISTS _wh_bundle_answer_work (
    event_id UUID NOT NULL,
    stream_id UUID,
    tenant_scope TEXT,
    event_type TEXT,
    present BOOLEAN NOT NULL,
    refused BOOLEAN NOT NULL,
    adoptable BOOLEAN NOT NULL
  ) ON COMMIT DELETE ROWS;
  DELETE FROM _wh_bundle_answer_work;

  INSERT INTO _wh_bundle_answer_work
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
  FROM _wh_bundle_answer_work r;
  v_unchanged := v_children > 0 AND v_present + v_refused >= v_children AND v_adoptable_count = 0;

  -- 333 (#2093): the covered keys, each bucket once with its stream and once with the empty id (its type lane), are
  -- collected into arrays first. The working table has no statistics (it is never analyzed, which would write the
  -- catalog on every call), so a join against it is planned for a large table and reads the ledger by a sequential
  -- scan; an unnest of parameters is planned as a few rows, so each key is one primary key probe.
  SELECT array_agg(k.tenant_scope), array_agg(k.event_type), array_agg(k.sid)
  INTO v_key_tenants, v_key_types, v_key_streams
  FROM (SELECT DISTINCT r.tenant_scope, r.event_type, ks.sid
        FROM _wh_bundle_answer_work r
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
  -- 333 (#2093): the update matches all four primary key columns by equality instead of an OR on the last one, which
  -- no index can serve and which read the whole ledger on every bundle. A row already answered with no answer pending
  -- is not rewritten: answered_at is read only as set or not set, and the type lane row is shared by every bundle of
  -- its type.
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
        FROM _wh_bundle_answer_work r
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
    FROM _wh_bundle_answer_work r
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
'357 (#2143): a bucket of a bundle that changed nothing is marked unhealable only when the answer covered its deficit: the answered window (p_answer_from exclusive, p_answer_to inclusive) includes the row''s compared window, or the bundle carried the row''s named missing event; p_answer_window_known false (an origin or caller that does not say) marks as before. Other buckets are answered and stay repairable. 333 (#2093): the covered ledger rows are found by primary key (no ledger scan), and a row already answered with no answer pending is not rewritten. 321: its working table is made once per connection and emptied at commit. 320 (#2029, #2033): records a committed redelivery bundle''s answer when its children commit. Per bucket: the events present in the event store, absent (the bundle stores them), refused as truncated (252) and adoptable; the covered ledger rows and their type lane stop waiting (answered); and every refused bucket is marked unhealable, returned with the reason so each is reported once.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_integrity_mark_unhealable_batch: last word 249, copied whole with the 357 delta (the answered window and the
-- bundle's events, trailing and defaulted, so a five-argument call from an older release marks as before).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_mark_unhealable_batch');
-- <docs>resilience/stream-integrity#unhealable-buckets</docs>
-- <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:MarkUnhealable_ATrackedBucket_IsReturnedOnce_AndAKeyWithoutARowIsIgnoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:MarkUnhealable_AnEmptyTenant_MatchesTheRowTheLedgerKeyedWithoutOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:MarkUnhealable_WithAWindow_MarksOnlyTheRowsTheAnswerCoveredAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_unhealable_batch(
  p_origin_service_id   UUID,
  p_tenant_scopes       TEXT[],
  p_event_types         TEXT[],
  p_stream_ids          UUID[],
  p_now                 TIMESTAMPTZ,
  p_answer_window_known BOOLEAN DEFAULT false,
  p_answer_from         BIGINT DEFAULT NULL,
  p_answer_to           BIGINT DEFAULT NULL,
  p_event_ids           UUID[] DEFAULT NULL
) RETURNS TABLE(tenant_scope TEXT, event_type TEXT, stream_id UUID) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  WITH keys AS (
    SELECT DISTINCT COALESCE(k.tenant, '') AS tenant, k.etype, k.sid
    FROM unnest(p_tenant_scopes, p_event_types, p_stream_ids) AS k(tenant, etype, sid)
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET unhealable_at = p_now,
      last_touched  = p_now
  FROM keys k
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = k.tenant
    AND l.event_type = k.etype
    AND l.stream_id = k.sid
    AND l.unhealable_at IS NULL
    -- 357 (#2143): only a row whose deficit the answer covered.
    AND __SCHEMA__._wh_integrity_answer_covers(p_answer_window_known, p_answer_from, p_answer_to,
                                               l.window_from, l.window_until, l.missing_event_id, p_event_ids)
  RETURNING l.tenant_scope, l.event_type, l.stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_unhealable_batch(UUID, TEXT[], TEXT[], UUID[], TIMESTAMPTZ, BOOLEAN, BIGINT, BIGINT, UUID[]) IS
'357 (#2143): with the answered window known, marks only the rows whose deficit the answer covered (_wh_integrity_answer_covers). 249 (#1790): marks the tracked ledger rows of the keyed buckets as ones repair cannot change and returns the rows this call marked (a row already marked, or a key with no row, is not returned), so each is reported once.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The one-time correction: per-stream whole-history rows marked unhealable with no repair attempt of their own were
-- marked by another request's answer, which before 357 never had to cover them. Each is re-armed (the mark and the
-- answer cleared) so the paced drain asks for its whole history. Runs once per database: the wh_settings marker is
-- claimed first, and a call that finds it already set changes nothing and returns 0. Returns the rows re-armed.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_rearm_narrow_answers');
-- <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.cs:Correction_ReArmsWholeHistoryRowsMarkedWithoutAnAttemptOfTheirOwn_OnceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_rearm_narrow_answers()
RETURNS INTEGER AS $$
DECLARE
  v_rearmed INTEGER;
BEGIN
  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
  VALUES ('integrity_rearm_narrow_answers_version', '1', 'integer',
          'Gates the one-time re-arm of whole-history ledger rows an answer narrower than their window marked unhealable (#2143, migration 357).',
          NOW(), 'migration:357_AnAnswerMarksOnlyWhatItCovered')
  ON CONFLICT (setting_key) DO NOTHING;
  IF NOT FOUND THEN
    RETURN 0;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET unhealable_at = NULL,
      answered_at   = NULL,
      last_touched  = NOW()
  WHERE l.stream_id <> __EMPTY_UUID__::uuid
    AND l.unhealable_at IS NOT NULL
    AND l.window_from IS NULL
    AND l.repair_attempts = 0;
  GET DIAGNOSTICS v_rearmed = ROW_COUNT;
  RETURN v_rearmed;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_rearm_narrow_answers() IS
'357 (#2143): once per database (the integrity_rearm_narrow_answers_version setting), re-arms the per-stream whole-history ledger rows marked unhealable with no repair attempt of their own, which an answer narrower than their window marked before 357. Returns the rows re-armed; 0 once the marker is set.';

SELECT __SCHEMA__.wh_integrity_rearm_narrow_answers();
