-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 530_AWholeTypeAnswerIsJudgedOnceAllOfItIsIn.sql
-- Date: 2026-11-18
-- Description: A whole-type answer marks its type lanes only once every one of its bundles has committed, in any order
--              (#2340), and only an answer to a whole-type request marks a type lane at all (#2341).
--
--              431 (#2311) lets only an answer's last bundle mark a type lane, and has a bundle that changed nothing and
--              says more follows leave the lane waiting. That relies on the last bundle committing after the others,
--              which holds when every bundle of the answer rides one broker session. It does not hold when they ride
--              several: a requester that does not accept bundles spanning streams is answered one stream per session,
--              and an answer's events of different scopes ride one session per scope. The last bundle then committed
--              first, found nothing brought before it, and marked the lane while earlier bundles still had rows to
--              store. And marking a type lane checked only that it was waiting, so an answer to a separate request for
--              named streams of the same type that changed nothing marked a waiting whole-type lane, against 383's rule
--              that such an answer says nothing about the rest of the type.
--
--              The origin now names, on each bundle, its answer (an id), its place in it (an index), on the last the
--              number of bundles, and whether the answer is to a whole-type request. wh_integrity_record_bundle_answer
--              takes them as p_answer_id, p_bundle_index, p_bundle_count and p_answers_whole_type:
--
--              1. wh_integrity_answer_progress (new): one row per whole-type answer still coming in, with the places
--                 recorded so far, the count once the last bundle has come, the union of the bundles' windows, and per
--                 (tenant, type) what each bundle found. Removed when the answer completes; an answer whose bundles
--                 stopped coming is forgotten a day after its last bundle, by the next call.
--              2. wh_integrity_record_bundle_answer: copied whole from 431 with the delta. A bundle of a named-stream
--                 answer neither answers nor marks a type lane. A bundle of a whole-type answer answers its type lanes
--                 at once when it brought something (as before) and otherwise leaves them waiting; the bundle that
--                 completes the count answers the rest and marks each one no bundle brought anything for, that the
--                 answer's last bundle carried whole, that was awaiting it, and whose requested window the union of
--                 the answer's windows covers. Per-stream buckets are judged per bundle exactly as before. A bundle
--                 from an origin that predates the fields names no answer and is judged as 431 judged it.
--
--              Number 530: 494 is the highest taken by changes in flight; numbers up to 529 are left for them.
--
-- Reversal: reversible (431's wh_integrity_record_bundle_answer is restored and the progress table dropped; an answer
--           in progress is then judged bundle by bundle as before, and its type lanes at worst wait out their answer
--           timeout)
-- Dependencies: 431 (wh_integrity_record_bundle_answer, copied with the delta above), 383 (the type lane's requested
--               window), 357 (_wh_integrity_answer_covers)
-- Objects: wh_integrity_answer_progress, idx_integrity_answer_progress_last_touched, wh_integrity_record_bundle_answer
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_integrity_answer_progress: a whole-type redelivery answer still coming in (#2340).
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_answer_progress (
  origin_service_id UUID        NOT NULL,
  answer_id         UUID        NOT NULL,
  bundle_count      INTEGER,
  bundles_in        INTEGER[]   NOT NULL,
  window_known      BOOLEAN     NOT NULL,
  answer_from       BIGINT,
  answer_to         BIGINT,
  lane_tenants      TEXT[]      NOT NULL,
  lane_types        TEXT[]      NOT NULL,
  lane_states       TEXT[]      NOT NULL,
  last_touched      TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (origin_service_id, answer_id)
);

CREATE INDEX IF NOT EXISTS idx_integrity_answer_progress_last_touched
  ON __SCHEMA__.wh_integrity_answer_progress (last_touched);

COMMENT ON TABLE __SCHEMA__.wh_integrity_answer_progress IS
'530 (#2340): one row per whole-type redelivery answer whose bundles are still coming in: the places (bundle indexes) recorded so far, the number of bundles once the last has come, whether every bundle said its window and the union of those windows (NULL unbounded), and per (tenant, type) each bundle carried what it found: answered (it brought something), unmarkable (the last bundle carried a stream of the type in part, or found none of its events present) or unchanged. wh_integrity_record_bundle_answer deletes the row when the count completes, and deletes any row a day after its last bundle.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- wh_integrity_record_bundle_answer: last word 431, copied whole with the 530 delta (judged once all of it is in).
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
-- not granted again. 410 (#2149): a stream the bundle names in p_partial_streams (it carried only part of the
-- stream's answer: the rest is in another bundle, on another page of the origin's selection, or beyond the origin's cap)
-- is answered but its bucket is never marked unchanged, and the type lane of a (tenant, type) with such a stream in the
-- bundle is not marked either, whatever window the bundle claims. 431 (#2311): a bundle that says more of its answer
-- follows (p_answer_continues) and changed nothing leaves the type lanes it carried awaiting the rest of the answer, so
-- only the answer's last bundle can mark one, and only when no bundle before it brought anything (such a bundle answers
-- the type lane, and an answered type lane is no longer awaiting, so it cannot be marked). 530 (#2340, #2341): a bundle
-- that names its answer (p_answer_id, p_bundle_index, and on the answer's last bundle p_bundle_count) is judged with
-- the rest of its answer, whatever order the bundles commit in. A bundle of an answer to a request for named streams
-- (p_answers_whole_type false) neither answers nor marks a type lane. A bundle of a whole-type answer records itself in
-- wh_integrity_answer_progress; one that brought something answers its type lanes at once, one that changed nothing
-- leaves them waiting, and the bundle that completes the count answers every type lane the answer carried and no bundle
-- answered, and marks those the answer as a whole covered (the union of its bundles' windows) and the last bundle did
-- not carry in part. A bundle that names no answer is judged as 431 judged it.
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
-- <docs>resilience/stream-integrity#a-whole-type-answer-that-brings-nothing-stops-the-asking</docs>
-- <docs>resilience/stream-integrity#a-whole-type-answer-is-judged-as-a-whole</docs>
-- <docs>resilience/stream-integrity#a-whole-type-answer-is-judged-once-all-of-it-is-in</docs>
-- <docs>resilience/stream-integrity#only-a-whole-type-answer-marks-the-type-lane</docs>
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.PartialStreams.cs:RecordBundleAnswer_AStreamTheBundleCarriedOnlyInPart_IsAnsweredButStaysRepairableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerCoverageSqlTests.PartialStreams.cs:RecordBundleAnswer_AnotherStreamCarriedInPart_DoesNotStopTheMarkOfAStreamCarriedWholeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.PartialStreams.cs:RecordBundleAnswer_ABundleThatCarriedAStreamOfTheTypeInPart_LeavesTheTypeLaneRepairableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.WholeAnswer.cs:RecordBundleAnswer_AMultiBundleAnswerWhoseFirstBundleChangedNothing_DoesNotMarkTheTypeLaneBeforeItsLastBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.WholeAnswer.cs:RecordBundleAnswer_AMultiBundleAnswerThatChangedNothingAnywhere_MarksTheTypeLaneOnItsLastBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.WholeAnswer.cs:RecordBundleAnswer_AnEarlierBundleThatStoredSomething_LeavesTheTypeLaneUnmarkedByTheLastAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_ANamedStreamAnswerThatChangedNothing_LeavesAWaitingWholeTypeLaneUnmarkedAndWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_ANamedStreamAnswerThatBroughtSomething_LeavesAWaitingWholeTypeLaneWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AMultiBundleNamedStreamAnswerThatChangedNothing_NeverMarksTheTypeLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AOneBundleWholeTypeAnswerThatChangedNothing_MarksTheTypeLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AWholeTypeAnswerThatChangedNothing_MarksTheTypeLaneOnlyOnceAllItsBundlesAreInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AWholeTypeAnswerWithABundleThatBroughtSomething_NeverMarksTheTypeLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AnAnswerWhoseBundlesCarryDifferentScopes_MarksEveryTenantsLaneOnlyOnceAllOfItIsInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AnAnswerThatBroughtSomethingForOneTenant_MarksOnlyTheOtherTenantsLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_ABundleCommittedTwice_DoesNotStandInForAMissingOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AnAnswerWithABundleNeverCommitted_LeavesTheTypeLaneWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_PagesThatEachAnswerPartOfTheWindow_AreJudgedByTheWindowTheyAnsweredTogetherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AnAnswerThatCoveredLessThanTheRequest_AnswersTheTypeLaneWithoutMarkingItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_ALastBundleThatCarriedAStreamInPart_LeavesTheTypeLaneUnmarkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AnEarlierBundleThatCarriedAStreamInPart_StillLetsTheWholeAnswerMarkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_AnAnswerLeftIncompleteForADay_IsForgottenWhenAnotherAnswerStartsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.AnswerParts.cs:RecordBundleAnswer_ConcurrentBundlesOfOneAnswer_JudgeTheAnswerOnceBothHaveCommittedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_record_bundle_answer(
  p_origin_service_id UUID,
  p_event_ids         UUID[],
  p_commit_sequences  BIGINT[],
  p_now               TIMESTAMPTZ,
  p_answer_window_known BOOLEAN DEFAULT false,
  p_answer_from       BIGINT DEFAULT NULL,
  p_answer_to         BIGINT DEFAULT NULL,
  p_partial_streams   UUID[] DEFAULT NULL,
  p_answer_continues  BOOLEAN DEFAULT false,
  p_answer_id         UUID DEFAULT NULL,
  p_bundle_index      INTEGER DEFAULT NULL,
  p_bundle_count      INTEGER DEFAULT NULL,
  p_answers_whole_type BOOLEAN DEFAULT false
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
  -- 530 (#2340, #2341): the bundle names its answer (v_tracked), which is to a whole-type request (v_whole); the
  -- answer's progress as this call leaves it, and, when this call completes the answer, the type lanes it settles.
  v_tracked          BOOLEAN;
  v_whole            BOOLEAN;
  v_complete         BOOLEAN := false;
  v_bl_tenants       TEXT[];
  v_bl_types         TEXT[];
  v_bl_states        TEXT[];
  v_p_count          INTEGER;
  v_p_in             INTEGER[];
  v_p_known          BOOLEAN;
  v_p_from           BIGINT;
  v_p_to             BIGINT;
  v_p_tenants        TEXT[];
  v_p_types          TEXT[];
  v_p_states         TEXT[];
  v_done_tenants     TEXT[];
  v_done_types       TEXT[];
  v_done_marks       BOOLEAN[];
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

  -- 530 (#2340): a bundle of a whole-type answer is judged with the rest of its answer. Its bundles can ride several
  -- broker sessions (one per stream, or one per scope), so they commit in any order, and the bundle that says it is the
  -- last can commit first. Each bundle records the (tenant, type)s it carried in the answer's progress row: answered (it
  -- brought something, so it answers their type lanes now), unmarkable (it is the answer's last bundle and carried a
  -- stream of the type in part, or it found none of the type's events present), or unchanged. The row is locked by its
  -- upsert, before any ledger row, so two bundles of one answer are judged one after the other and exactly one of them
  -- sees the count complete. 530 (#2341): a bundle of an answer to named streams keeps no progress: it marks no type lane.
  v_tracked := p_answer_id IS NOT NULL AND p_bundle_index IS NOT NULL;
  v_whole := v_tracked AND COALESCE(p_answers_whole_type, false);
  IF v_whole THEN
    SELECT array_agg(g.tenant_scope), array_agg(g.event_type), array_agg(g.state)
    INTO v_bl_tenants, v_bl_types, v_bl_states
    FROM (SELECT r.tenant_scope, r.event_type,
                 CASE WHEN NOT v_unchanged THEN 'answered'
                      WHEN NOT bool_or(r.present)
                        OR (p_bundle_count IS NOT NULL
                            AND bool_or(r.stream_id = ANY(COALESCE(p_partial_streams, ARRAY[]::uuid[])))) THEN 'unmarkable'
                      ELSE 'unchanged' END AS state
          FROM unnest(v_w_streams, v_w_tenants, v_w_types, v_w_present) AS r(stream_id, tenant_scope, event_type, present)
          WHERE r.stream_id IS NOT NULL AND r.stream_id <> __EMPTY_UUID__::uuid
          GROUP BY r.tenant_scope, r.event_type) g;

    -- A bundle recorded before (a redelivered copy) adds nothing: the answer is counted by distinct places.
    INSERT INTO __SCHEMA__.wh_integrity_answer_progress AS p
      (origin_service_id, answer_id, bundle_count, bundles_in, window_known, answer_from, answer_to,
       lane_tenants, lane_types, lane_states, last_touched)
    VALUES (p_origin_service_id, p_answer_id, p_bundle_count, ARRAY[p_bundle_index], COALESCE(p_answer_window_known, false),
            p_answer_from, p_answer_to, COALESCE(v_bl_tenants, ARRAY[]::text[]), COALESCE(v_bl_types, ARRAY[]::text[]),
            COALESCE(v_bl_states, ARRAY[]::text[]), p_now)
    ON CONFLICT (origin_service_id, answer_id) DO UPDATE
    SET bundle_count = COALESCE(p.bundle_count, EXCLUDED.bundle_count),
        bundles_in   = CASE WHEN p_bundle_index = ANY(p.bundles_in) THEN p.bundles_in ELSE p.bundles_in || p_bundle_index END,
        window_known = p.window_known AND EXCLUDED.window_known,
        -- The answer covers the union of its bundles' windows: a backfill window's pages are contiguous, and every
        -- bundle of an ordinary answer echoes the same window. NULL is unbounded.
        answer_from  = CASE WHEN p.answer_from IS NULL OR EXCLUDED.answer_from IS NULL THEN NULL
                            ELSE LEAST(p.answer_from, EXCLUDED.answer_from) END,
        answer_to    = CASE WHEN p.answer_to IS NULL OR EXCLUDED.answer_to IS NULL THEN NULL
                            ELSE GREATEST(p.answer_to, EXCLUDED.answer_to) END,
        lane_tenants = CASE WHEN p_bundle_index = ANY(p.bundles_in) THEN p.lane_tenants ELSE p.lane_tenants || EXCLUDED.lane_tenants END,
        lane_types   = CASE WHEN p_bundle_index = ANY(p.bundles_in) THEN p.lane_types ELSE p.lane_types || EXCLUDED.lane_types END,
        lane_states  = CASE WHEN p_bundle_index = ANY(p.bundles_in) THEN p.lane_states ELSE p.lane_states || EXCLUDED.lane_states END,
        last_touched = EXCLUDED.last_touched
    RETURNING p.bundle_count, p.bundles_in, p.window_known, p.answer_from, p.answer_to,
              p.lane_tenants, p.lane_types, p.lane_states
    INTO v_p_count, v_p_in, v_p_known, v_p_from, v_p_to, v_p_tenants, v_p_types, v_p_states;

    v_complete := v_p_count IS NOT NULL AND cardinality(v_p_in) >= v_p_count;
    IF v_complete THEN
      -- The answer is in. Its type lanes no bundle answered are settled now; those it never carried in part (in its last
      -- bundle) and always found present may be marked.
      SELECT array_agg(g.tenant_scope), array_agg(g.event_type), array_agg(g.markable)
      INTO v_done_tenants, v_done_types, v_done_marks
      FROM (SELECT t.tenant_scope, t.event_type, NOT bool_or(t.state = 'unmarkable') AS markable
            FROM unnest(v_p_tenants, v_p_types, v_p_states) AS t(tenant_scope, event_type, state)
            GROUP BY t.tenant_scope, t.event_type
            HAVING NOT bool_or(t.state = 'answered')) g;
      DELETE FROM __SCHEMA__.wh_integrity_answer_progress p
      WHERE p.origin_service_id = p_origin_service_id AND p.answer_id = p_answer_id;
    END IF;

    -- An answer whose bundles stopped coming (expired at the broker, or lost) can never complete; its type lanes wait
    -- out their answer timeout and are asked for again. Its progress is forgotten a day after its last bundle.
    DELETE FROM __SCHEMA__.wh_integrity_answer_progress p WHERE p.last_touched < p_now - interval '1 day';
  END IF;

  -- 333 (#2093): the covered keys, each bucket once with its stream and once with the empty id (its type lane), are
  -- collected into arrays first, so each key is one primary key probe of the ledger.
  SELECT array_agg(k.tenant_scope), array_agg(k.event_type), array_agg(k.sid)
  INTO v_key_tenants, v_key_types, v_key_streams
  FROM (SELECT DISTINCT r.tenant_scope, r.event_type, ks.sid
        FROM unnest(v_w_streams, v_w_tenants, v_w_types) AS r(stream_id, tenant_scope, event_type)
        CROSS JOIN LATERAL (VALUES (r.stream_id), (__EMPTY_UUID__::uuid)) AS ks(sid)
        WHERE r.stream_id IS NOT NULL) k;

  -- 530 (#2340): the type lanes the completed answer settles are written below too, so they are locked with the rest,
  -- in one primary key order.
  IF v_complete THEN
    SELECT array_agg(k.tenant_scope), array_agg(k.event_type), array_agg(k.sid)
    INTO v_key_tenants, v_key_types, v_key_streams
    FROM (SELECT b.tenant_scope, b.event_type, b.sid
          FROM unnest(v_key_tenants, v_key_types, v_key_streams) AS b(tenant_scope, event_type, sid)
          UNION
          SELECT d.tenant_scope, d.event_type, __EMPTY_UUID__::uuid
          FROM unnest(v_done_tenants, v_done_types) AS d(tenant_scope, event_type)) k;
  END IF;

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

  -- The answer landed: every bucket the bundle covered stops waiting, and its type lane too. 431 (#2311): except a type
  -- lane, when the bundle changed nothing and more of its answer follows; the type lane waits for the rest.
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
    AND (l.answered_at IS NULL OR l.answer_pending_until IS NOT NULL)
    AND (b.sid <> __EMPTY_UUID__::uuid
         -- A bundle that names no answer: as 431.
         OR (NOT v_tracked AND NOT (v_unchanged AND COALESCE(p_answer_continues, false)))
         -- 530 (#2340): a bundle of a whole-type answer that brought something answers its type lanes now, and the
         -- bundle that completes the answer answers the ones it settles. 530 (#2341): an answer to named streams, none.
         OR (v_whole AND NOT v_unchanged)
         OR (v_complete AND EXISTS (SELECT 1 FROM unnest(v_done_tenants, v_done_types) AS d(tenant_scope, event_type)
                                    WHERE d.tenant_scope = b.tenant_scope AND d.event_type = b.event_type)));

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
    WHERE g.refused > 0
       OR (v_unchanged AND g.present > 0 AND NOT (g.stream_id = ANY(COALESCE(p_partial_streams, ARRAY[]::uuid[]))))
    UNION ALL
    SELECT DISTINCT r.tenant_scope, r.event_type, __EMPTY_UUID__::uuid, 'unchanged'
    FROM unnest(v_w_streams, v_w_tenants, v_w_types, v_w_present) AS r(stream_id, tenant_scope, event_type, present)
    WHERE NOT v_tracked AND v_unchanged AND p_answer_window_known AND r.present AND r.stream_id IS NOT NULL
      -- 431 (#2311): only the answer's last bundle can show the whole answer brought nothing.
      AND NOT COALESCE(p_answer_continues, false)
      AND r.stream_id <> __EMPTY_UUID__::uuid
      AND EXISTS (SELECT 1 FROM unnest(v_lane_tenants, v_lane_types) AS p(tenant_scope, event_type)
                  WHERE p.tenant_scope = r.tenant_scope AND p.event_type = r.event_type)
      -- 410 (#2149): a bundle that carried a stream of the type only in part has not shown the type has nothing missing.
      AND NOT EXISTS (SELECT 1 FROM unnest(v_w_streams, v_w_tenants, v_w_types) AS x(stream_id, tenant_scope, event_type)
                      WHERE x.stream_id = ANY(COALESCE(p_partial_streams, ARRAY[]::uuid[]))
                        AND x.tenant_scope = r.tenant_scope AND x.event_type = r.event_type)
    UNION ALL
    -- 530 (#2340): the completed whole-type answer marks each type lane it settles that brought nothing, was carried
    -- whole and was awaiting it; the marking below holds it to the union of the answer's windows.
    SELECT d.tenant_scope, d.event_type, __EMPTY_UUID__::uuid, 'unchanged'
    FROM unnest(v_done_tenants, v_done_types, v_done_marks) AS d(tenant_scope, event_type, markable)
    WHERE v_complete AND d.markable AND v_p_known
      AND EXISTS (SELECT 1 FROM unnest(v_lane_tenants, v_lane_types) AS p(tenant_scope, event_type)
                  WHERE p.tenant_scope = d.tenant_scope AND p.event_type = d.event_type)
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
           -- 530 (#2340): a type lane a completed answer settles is held to the window the whole answer covered.
           OR (v_complete AND t.sid = __EMPTY_UUID__::uuid
               AND __SCHEMA__._wh_integrity_answer_covers(v_p_known, v_p_from, v_p_to,
                                                          l.window_from, l.window_until, l.missing_event_id, NULL))
           OR (NOT (v_complete AND t.sid = __EMPTY_UUID__::uuid)
               AND __SCHEMA__._wh_integrity_answer_covers(p_answer_window_known, p_answer_from, p_answer_to,
                                                          l.window_from, l.window_until, l.missing_event_id, p_event_ids)))
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

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_record_bundle_answer(UUID, UUID[], BIGINT[], TIMESTAMPTZ, BOOLEAN, BIGINT, BIGINT, UUID[], BOOLEAN, UUID, INTEGER, INTEGER, BOOLEAN) IS
'530 (#2340, #2341): a bundle that names its answer (p_answer_id, p_bundle_index, and on the last bundle p_bundle_count) is judged with the rest of the answer, whatever order its bundles commit in: a bundle of an answer to named streams (p_answers_whole_type false) neither answers nor marks a type lane; a bundle of a whole-type answer is recorded in wh_integrity_answer_progress, answers its type lanes at once only when it brought something, and the bundle that completes the count answers every type lane of the answer no bundle answered and marks those the union of the answer''s windows covered, that were awaiting it and that its last bundle did not carry in part. A bundle that names no answer is judged as 431 judged it. 431 (#2311): a bundle that says more of its answer follows (p_answer_continues) and changed nothing neither answers nor marks the type lanes it carried, which keep waiting for the rest of the answer; only the answer''s last bundle marks a type lane, and a bundle that brought something answers it, so a later bundle of the answer cannot mark it. 410 (#2149): a stream named in p_partial_streams (carried by the bundle only in part) is answered but never marked unchanged, and the type lane of a (tenant, type) with such a stream in the bundle is not marked. 383 (#2211): a bundle that changed nothing and says its answered window also marks the type lane of each (tenant, type) it carried whose whole-type request was awaiting the answer, when that window covered the one the request asked for (stamped by wh_integrity_mark_answer_pending), and returns it with zero counts, so a whole-type request whose answer brings nothing new is not granted again. 369: the bundle''s children are gathered into arrays by one statement, with no temporary table, so a call on a freshly reset pooled connection writes nothing to the system catalog. 357 (#2143): a bucket of a bundle that changed nothing is marked unhealable only when the answer covered its deficit: the answered window (p_answer_from exclusive, p_answer_to inclusive) includes the row''s compared window, or the bundle carried the row''s named missing event; p_answer_window_known false (an origin or caller that does not say) marks as before. Other buckets are answered and stay repairable. 333 (#2093): the covered ledger rows are found by primary key (no ledger scan), and a row already answered with no answer pending is not rewritten. 320 (#2029, #2033): records a committed redelivery bundle''s answer when its children commit. Per bucket: the events present in the event store, absent (the bundle stores them), refused as truncated (252) and adoptable; the covered ledger rows and their type lane stop waiting (answered); and every refused bucket is marked unhealable, returned with the reason so each is reported once.';
