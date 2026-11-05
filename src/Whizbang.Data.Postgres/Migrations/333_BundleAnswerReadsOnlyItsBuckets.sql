-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 333_BundleAnswerReadsOnlyItsBuckets.sql
-- Date: 2026-11-05
-- Description: Recording a redelivery bundle's answer costs in proportion to the bundle, not the ledger (#2093).
--
--              wh_integrity_record_bundle_answer (321) clears the wait on every ledger row the bundle covered: the
--              bucket's own row and its type lane (stream_id = the empty id). It matched both with one join condition,
--              "l.stream_id = b.stream_id OR l.stream_id = <empty>". An OR across two values of the key's last column
--              cannot be an index condition, so under a generic plan the planner hash-joined the working rows against a
--              sequential scan of the whole wh_integrity_ledger, on every bundle: about 40 ms at 300,000 ledger rows,
--              growing with the ledger, and hundreds of bundles a minute while a backfill is answered.
--
--              Three changes, the function otherwise copied whole from 321:
--              1. The covered keys are expanded first (each bucket once with its stream, once with the empty id), so
--                 the update joins on all four primary key columns by equality and reads one primary key entry per
--                 key (wh_integrity_ledger_pkey).
--              2. A row already answered, with no answer pending, is left alone. answered_at is read only as set or
--                 not set, so writing a newer instant over it changes nothing but the row's version, and the type lane
--                 row is shared by every bundle of its type: rewriting it on every bundle made concurrent answers for
--                 one type queue on that row's lock until each committed.
--
--              3. The rows either update may write are locked first, in primary key order. A bundle spanning several
--                 types or streams visited its type lanes in its plan's order, so two such bundles answered at once
--                 could each hold a row the other wanted and deadlock (observed under 12 concurrent 50-event bundles).
--
--              Number 333: the next free number.
--
-- Dependencies: 320 (wh_integrity_ledger answer columns), 321 (wh_integrity_record_bundle_answer, copied whole with
--               the changes above)
-- Objects: wh_integrity_record_bundle_answer
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

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

  -- 333 (#2093): the ledger rows either statement below may write are locked first, in primary key order, so two
  -- bundles that cover the same type lanes or buckets queue on them in one order instead of deadlocking (the joins
  -- below visit them in whatever order their plans choose). Rows neither statement will write are not locked.
  PERFORM COUNT(*) FROM (
    SELECT 1
    FROM __SCHEMA__.wh_integrity_ledger l
    JOIN (SELECT DISTINCT r.tenant_scope, r.event_type, k.sid
          FROM _wh_bundle_answer_work r
          CROSS JOIN LATERAL (VALUES (r.stream_id), (__EMPTY_UUID__::uuid)) AS k(sid)
          WHERE r.stream_id IS NOT NULL) b
      ON l.origin_service_id = p_origin_service_id
     AND l.tenant_scope = b.tenant_scope AND l.event_type = b.event_type AND l.stream_id = b.sid
    WHERE l.answered_at IS NULL OR l.answer_pending_until IS NOT NULL
       OR (l.stream_id <> __EMPTY_UUID__::uuid AND l.unhealable_at IS NULL)
    ORDER BY l.tenant_scope, l.event_type, l.stream_id
    FOR UPDATE OF l
  ) locked;

  -- The answer landed: every bucket the bundle covered stops waiting, and its type lane too.
  -- 333 (#2093): the covered keys are expanded first (each bucket with its stream and with the empty id), so the
  -- update matches all four primary key columns by equality instead of an OR on the last one, which no index can
  -- serve and which read the whole ledger on every bundle. A row already answered with no answer pending is not
  -- rewritten: answered_at is read only as set or not set, and the type lane row is shared by every bundle of its type.
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET answer_pending_until = NULL,
      answered_at          = p_now,
      last_touched         = p_now
  FROM (SELECT DISTINCT r.tenant_scope, r.event_type, k.sid
        FROM _wh_bundle_answer_work r
        CROSS JOIN LATERAL (VALUES (r.stream_id), (__EMPTY_UUID__::uuid)) AS k(sid)
        WHERE r.stream_id IS NOT NULL) b
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = b.tenant_scope AND l.event_type = b.event_type AND l.stream_id = b.sid
    AND (l.answered_at IS NULL OR l.answer_pending_until IS NOT NULL);

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
'333 (#2093): the covered ledger rows are found by primary key (no ledger scan), and a row already answered with no answer pending is not rewritten. 321: its working table is made once per connection and emptied at commit. 320 (#2029, #2033): records a committed redelivery bundle''s answer when its children commit. Per bucket: the events present in the event store, absent (the bundle stores them), refused as truncated (252) and adoptable; the covered ledger rows and their type lane stop waiting (answered); and the buckets of a bundle that changed nothing (all present or refused, none adoptable) and every refused bucket are marked unhealable, returned with the reason so each is reported once.';
