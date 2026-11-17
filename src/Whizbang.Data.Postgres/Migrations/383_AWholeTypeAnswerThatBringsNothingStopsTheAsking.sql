-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 383_AWholeTypeAnswerThatBringsNothingStopsTheAsking.sql
-- Date: 2026-11-09
-- Description: A whole-type repair request whose answer brings nothing new is not asked again (#2211).
--
--              A bulk backfill or a confirmed checkpoint gap asks the origin for every event of one (tenant, type) in a
--              window, and is recorded on the type's type lane: the ledger row with the empty stream id. When the
--              answer's bundles committed, every per-stream bucket of a bundle that changed nothing was marked as a
--              bucket repair cannot change, but the type lane was only recorded as answered. The answered state does not
--              stop a grant (wh_integrity_try_begin_repair clears it), so the same whole-type request was granted again
--              on the repair ladder and the origin re-shipped events this service already held, on every rung.
--
--              1. wh_integrity_mark_answer_pending: re-created from 320; the type lane's own row keeps the window its
--                 request asked for (both bounds given), in the ledger's units, so its answer can be judged against it.
--              2. wh_integrity_record_bundle_answer: re-created from 369; a bundle that changed nothing (every child
--                 already stored or refused, none adoptable) and says the window it answered also marks the type lane
--                 of each (tenant, type) it carried whose whole-type request was awaiting its answer, when that window
--                 covered the one the request asked for (_wh_integrity_answer_covers, 357: a type lane with no window
--                 needs a whole-history answer). An answer to a request for named streams marks no type lane: the type
--                 lane is not awaiting it. The marked type lane is returned with zero counts, so the receiver reports
--                 it once as it reports a marked bucket. Only the bundle that clears the wait can mark, so an answer
--                 whose first bundle brought something new leaves the type lane to the next request's answer.
--
--              A marked type lane is refused by both whole-type grant paths until its bucket changes: a comparison that
--              sees this service's side of the type change lifts the mark (wh_integrity_try_begin_report and
--              wh_integrity_report_divergences_with_deficits, as for any marked bucket), and a heal deletes the row. A
--              stream of the type that is genuinely missing is still compared and repaired on its own by the
--              stream-level drill-down, which this does not touch.
--
--              Number 383: 376 to 382 are taken on develop or by changes in flight.
--
-- Dependencies: 097 (window_from, window_until), 249 (unhealable_at), 320 (wh_integrity_mark_answer_pending, copied with
--               the delta above), 357 (_wh_integrity_answer_covers), 369 (wh_integrity_record_bundle_answer, copied with
--               the delta above)
-- Objects: wh_integrity_mark_answer_pending, wh_integrity_record_bundle_answer
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_integrity_mark_answer_pending: last word 320, copied whole with the 383 delta (the type lane keeps its window).
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_mark_answer_pending');

-- Records that requests for these buckets left. A type-lane key (the empty stream) is the bulk or checkpoint request
-- for the whole type: its own row, and the per-stream rows of the type whose stamped window lies inside
-- [p_window_from, p_window_until] (every per-stream row of the type when either bound is NULL). 383 (#2211): the type
-- lane's own row keeps the request's window when both bounds are given, so its answer is judged against what was asked.
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:Claim_ABucketWithItsAnswerPending_IsNotClaimed_UntilTheAnswerTimeoutPassesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:MarkAnswerPending_OnTheTypeLane_CoversThePerStreamRowsInsideItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairAnswerSqlTests.cs:TryBeginRepair_WhileItsOwnAnswerIsPending_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:MarkAnswerPending_OnTheTypeLane_StampsTheRequestsWindowOnItAsync</tests>
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
      last_touched         = p_now,
      -- 383 (#2211): the type lane's own row keeps the window its request asked for (the ledger's units: the request's
      -- exclusive floor plus one, its inclusive ceiling plus one), which its answer is judged against.
      window_from          = CASE WHEN l.stream_id = __EMPTY_UUID__::uuid AND p_window_from IS NOT NULL AND p_window_until IS NOT NULL
                                  THEN p_window_from ELSE l.window_from END,
      window_until         = CASE WHEN l.stream_id = __EMPTY_UUID__::uuid AND p_window_from IS NOT NULL AND p_window_until IS NOT NULL
                                  THEN p_window_until ELSE l.window_until END
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
'383 (#2211): the type lane''s own row keeps the request''s window when both bounds are given. 320 (#2029, #2034): records that repair requests for the keyed buckets left, so neither grant path asks again for p_timeout_secs or until a bundle answers. A type-lane key also covers the per-stream rows of its type inside the request''s window. Returns the rows marked.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_integrity_record_bundle_answer: last word 369, copied whole with the 383 delta (the type lane is marked too).
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_record_bundle_answer');

-- What a committed redelivery bundle answered, judged when its children commit. Per (stream, tenant, type) bucket:
-- the bundle's events the event store already held, those it did not (the bundle stores them), those the inbox
-- refused to restore because the stream was truncated here (252), and the present events the bundle could give an
-- origin or an origin sequence (the reconcile adopts them). A bucket keyed by a child the store holds takes the
-- stored event's key; one keyed by a child it does not takes the inbox row's, normalized as every digest fold keys
-- it. A child known to neither is counted in the row with NULL keys. marked says why this call marked the bucket
-- unhealable: refused_truncated (always), unchanged (every child was present or refused and none adoptable). 383
-- (#2211): a bundle that changed nothing and says the window it answered also marks the type lane (the empty stream) of
-- each (tenant, type) it carried whose whole-type request was awaiting its answer, when that window covered the one the
-- request asked for, and returns that row with zero counts, so a whole-type request whose answer brings nothing new is
-- not granted again.
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
-- <docs>resilience/stream-integrity#a-whole-type-answer-that-brings-nothing-stops-the-asking</docs>
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:RecordBundleAnswer_AnAnswerThatStoredNothing_CoveringTheTypeLanesRequest_MarksTheTypeLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:RecordBundleAnswer_AnAnswerThatStoredSomething_LeavesTheTypeLaneRepairableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:RecordBundleAnswer_AnAnswerNarrowerThanTheTypeLanesRequest_LeavesItRepairableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:RecordBundleAnswer_ATypeLaneWithNoRequestedWindow_IsMarkedOnlyByAWholeHistoryAnswerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:RecordBundleAnswer_AnotherTypesLane_IsNotMarkedByThisTypesAnswerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:RecordBundleAnswer_ATypeLaneNotAwaitingAnAnswer_IsNotMarkedByAnotherRequestsAnswerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.cs:RecordBundleAnswer_AnAnswerThatDoesNotSayItsWindow_LeavesTheTypeLaneRepairableAsync</tests>
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
  -- 383 (#2211): the type lanes of the bundle's (tenant, type)s whose whole-type request was awaiting its answer.
  v_lane_tenants     TEXT[];
  v_lane_types       TEXT[];
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

  -- 383 (#2211): which of the bundle's type lanes had a whole-type request (a bulk backfill or a checkpoint gap) awaiting
  -- its answer, read before the answer below clears the wait. Only those can be marked: a bundle answering a request for
  -- named streams says nothing about the rest of the type. Read from the rows the statement above locked.
  SELECT array_agg(l.tenant_scope), array_agg(l.event_type)
  INTO v_lane_tenants, v_lane_types
  FROM unnest(v_key_tenants, v_key_types, v_key_streams) AS b(tenant_scope, event_type, sid)
  JOIN __SCHEMA__.wh_integrity_ledger l
    ON l.origin_service_id = p_origin_service_id
   AND l.tenant_scope = b.tenant_scope AND l.event_type = b.event_type AND l.stream_id = b.sid
  WHERE b.sid = __EMPTY_UUID__::uuid AND l.answer_pending_until IS NOT NULL AND l.unhealable_at IS NULL;

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
  -- changed nothing. 383 (#2211): and, for a bundle that changed nothing and says the window it answered, the type
  -- lane of each (tenant, type) it carried whose whole-type request was awaiting this answer; the marking below holds it
  -- to the window that request asked for.
  SELECT array_agg(t.tenant_scope), array_agg(t.event_type), array_agg(t.stream_id), array_agg(t.reason)
  INTO v_mark_tenants, v_mark_types, v_mark_streams, v_mark_reasons
  FROM (
    SELECT g.tenant_scope, g.event_type, g.stream_id,
           CASE WHEN g.refused > 0 THEN 'refused_truncated' ELSE 'unchanged' END AS reason
    FROM (SELECT r.stream_id, r.tenant_scope, r.event_type,
                 COUNT(*) FILTER (WHERE r.present) AS present, COUNT(*) FILTER (WHERE r.refused) AS refused
          FROM unnest(v_w_streams, v_w_tenants, v_w_types, v_w_present, v_w_refused)
                 AS r(stream_id, tenant_scope, event_type, present, refused)
          WHERE r.stream_id IS NOT NULL
          GROUP BY r.stream_id, r.tenant_scope, r.event_type) g
    WHERE g.refused > 0 OR (v_unchanged AND g.present > 0)
    UNION ALL
    SELECT DISTINCT r.tenant_scope, r.event_type, __EMPTY_UUID__::uuid, 'unchanged'
    FROM unnest(v_w_streams, v_w_tenants, v_w_types, v_w_present) AS r(stream_id, tenant_scope, event_type, present)
    WHERE v_unchanged AND p_answer_window_known AND r.present AND r.stream_id IS NOT NULL
      AND r.stream_id <> __EMPTY_UUID__::uuid
      AND EXISTS (SELECT 1 FROM unnest(v_lane_tenants, v_lane_types) AS p(tenant_scope, event_type)
                  WHERE p.tenant_scope = r.tenant_scope AND p.event_type = r.event_type)
  ) t;

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
  SELECT o.sid, o.tenant, o.etype, o.present, o.absent, o.refused, o.adoptable, o.reason
  FROM (
    SELECT b.sid, b.tenant, b.etype, b.present, b.absent, b.refused, b.adoptable, m.reason
    FROM buckets b
    LEFT JOIN marked_rows m ON m.sid = b.sid AND m.tenant = b.tenant AND m.etype = b.etype
    UNION ALL
    -- 383 (#2211): a type lane this call marked is returned with zero counts, so it is reported once like any marked
    -- bucket; the bundle's events are already counted on their own buckets.
    SELECT m.sid, m.tenant, m.etype, 0, 0, 0, 0, m.reason
    FROM marked_rows m
    WHERE m.sid = __EMPTY_UUID__::uuid
  ) o
  ORDER BY o.sid, o.tenant, o.etype;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_record_bundle_answer(UUID, UUID[], BIGINT[], TIMESTAMPTZ, BOOLEAN, BIGINT, BIGINT) IS
'383 (#2211): a bundle that changed nothing and says its answered window also marks the type lane of each (tenant, type) it carried whose whole-type request was awaiting the answer, when that window covered the one the request asked for (stamped by wh_integrity_mark_answer_pending), and returns it with zero counts, so a whole-type request whose answer brings nothing new is not granted again. 369: the bundle''s children are gathered into arrays by one statement, with no temporary table, so a call on a freshly reset pooled connection writes nothing to the system catalog. 357 (#2143): a bucket of a bundle that changed nothing is marked unhealable only when the answer covered its deficit: the answered window (p_answer_from exclusive, p_answer_to inclusive) includes the row''s compared window, or the bundle carried the row''s named missing event; p_answer_window_known false (an origin or caller that does not say) marks as before. Other buckets are answered and stay repairable. 333 (#2093): the covered ledger rows are found by primary key (no ledger scan), and a row already answered with no answer pending is not rewritten. 320 (#2029, #2033): records a committed redelivery bundle''s answer when its children commit. Per bucket: the events present in the event store, absent (the bundle stores them), refused as truncated (252) and adoptable; the covered ledger rows and their type lane stop waiting (answered); and every refused bucket is marked unhealable, returned with the reason so each is reported once.';
