-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 420_RedeliveryReconcilesKeepNoWorkTable.sql
-- Date: 2026-11-13
-- Description: The redelivery reconciles hold their working rows in arrays, not a temporary table (#2181).
--
--              wh_reconcile_redelivered_bundles (per batch of bundles), wh_reconcile_redelivered_bundle_by_first_hop
--              (per bundle) and wh_adopt_redelivered_origin gathered the stored events of a bundle into a temporary
--              table. 321 kept two of them per connection, but a pooled connection is reset (DISCARD ALL) each time
--              the pool hands it out, and the reset drops every temporary table, so in practice each was created
--              again on almost every call: about 70 system catalog rows written and deleted, a cache invalidation for
--              every other session of the database, a fresh plan for each statement that read the table, and new
--              statement-statistics entries on every call. 369 (#2179) found the same cost in the bundle answer.
--
--              Each function now gathers the same rows into parallel arrays with one statement and reads them back
--              with unnest. Everything else is 336's body unchanged: the locks, the predicates, the digest folds, the
--              epoch refolds and what each returns. A side-by-side check against 336's definitions
--              (RedeliveryReconcileEquivalenceSqlTests) compares results and every table they write.
--
--              Number 420: 412 is the highest on develop; 413 to 419 are left to changes in flight.
--
-- Reversal: reversible (336's three definitions are restored; the functions keep no state)
-- Dependencies: 336 (the last word of all three, copied whole with the delta above), 321 (the per-connection tables
--               this removes), 369 (the same fix in wh_integrity_record_bundle_answer)
-- Objects: wh_adopt_redelivered_origin, wh_reconcile_redelivered_bundle_by_first_hop, wh_reconcile_redelivered_bundles
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_adopt_redelivered_origin');

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#pre-stamp-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_EventsStoredWithoutAnOrigin_TakeTheBundlesOriginAndSequencesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_TheNextCheckpointCountsTheAdoptedEvents_InsteadOfAskingForThemAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_MovesTheDigestFromTheLocalLaneToTheOriginsLane_LeavingLocalEventsWhereTheyAreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AnEventReceivedFromAnotherOrigin_IsNeverReattributedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_IntoSealedEpochs_RefoldsBothLanes_AndAdvancesTheOriginGenerationAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:AdoptRedeliveredOrigin_MovesEveryLaneToTheOriginsBucketAsync</tests>
-- <docs>resilience/stream-integrity#per-bundle-work-table</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:AdoptRedeliveredOrigin_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryReconcileEquivalenceSqlTests.cs:AdoptRedeliveredOrigin_GivesWhatTheWorkTableVersionGaveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerCallFunctionsCreateNoTemporaryTableTests.cs:NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_adopt_redelivered_origin(
  p_origin_service_id UUID,
  p_event_ids UUID[],
  p_commit_sequences BIGINT[]
) RETURNS TABLE(stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER, adopted_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_local_service_id UUID;
  v_adoptable BOOLEAN;
  v_min BIGINT;
  v_max BIGINT;
  v_width BIGINT;
  -- 420: the bundle's stored events, one element per event, in parallel arrays (was a temporary table).
  v_w_ids UUID[];
  v_w_streams UUID[];
  v_w_tenants TEXT[];
  v_w_types TEXT[];
  v_w_left_local BOOLEAN[];
  v_w_local_seqs BIGINT[];
  v_w_origin_seqs BIGINT[];
  v_w_adopted BOOLEAN[];
  v_w_digested BOOLEAN[];
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_adopt_redelivered_origin: % event ids but % commit sequences',
      cardinality(p_event_ids), cardinality(p_commit_sequences);
  END IF;

  -- The emit chain's normalization (087): the local service and the empty id are "locally originated".
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;
  v_adoptable := p_origin_service_id IS NOT NULL
    AND p_origin_service_id <> __EMPTY_UUID__::uuid
    AND p_origin_service_id IS DISTINCT FROM v_local_service_id;

  -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT es.stream_id AS sid
    FROM unnest(p_event_ids) AS u(eid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
    ORDER BY 1
  ) s;

  -- 420 (#2181): the rows are gathered into arrays by one statement. The temporary table was dropped by every
  -- session reset, and a pooled connection is reset each time it is handed out, so it was created again on almost
  -- every call: catalog rows written and deleted, an invalidation for every other session, fresh plans, and new
  -- statement-statistics entries per call. The bundle answer had the same fix in 369 (#2179).
  SELECT array_agg(w.event_id), array_agg(w.stream_id), array_agg(w.tenant_scope), array_agg(w.event_type),
         array_agg(w.left_local_lane), array_agg(w.local_sequence), array_agg(w.origin_sequence),
         array_agg(w.adopted), array_agg(w.digested)
  INTO v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs, v_w_adopted,
       v_w_digested
  FROM (
  SELECT
    es.event_id,
    es.stream_id,
    COALESCE(es.scope ->> 't', ''),
    es.event_type,
    v_adoptable AND es.origin_service_id IS NULL,
    es.commit_sequence,
    COALESCE(es.origin_commit_sequence, NULLIF(m.seq, 0)),
    v_adoptable AND (
      es.origin_service_id IS NULL
      OR (es.origin_service_id = p_origin_service_id AND es.origin_commit_sequence IS NULL AND COALESCE(m.seq, 0) > 0)),
    COALESCE(es.flags, 0) & c_flag_ephemeral = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
  FROM (SELECT DISTINCT ON (u.eid) u.eid, u.seq FROM unnest(p_event_ids, p_commit_sequences) AS u(eid, seq) ORDER BY u.eid) m
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
  ) AS w(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence, adopted,
        digested);

  UPDATE __SCHEMA__.wh_event_store es
  SET origin_service_id = p_origin_service_id, origin_commit_sequence = r.origin_sequence
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, digested)
  WHERE es.event_id = r.event_id AND r.adopted;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins the origin's.
  -- 336 (#2106): every lane leaves through the one fold helper.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(__EMPTY_UUID__::uuid), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
    array_agg(r.event_id), 'unfold', now())
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid
    AND d.event_count <= 0
    AND d.stream_id IN (SELECT r.stream_id
                         FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs,
                                       v_w_origin_seqs, v_w_adopted, v_w_digested)
                                AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence,
                                     origin_sequence, adopted, digested)
                         WHERE r.adopted AND r.digested AND r.left_local_lane);

  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(p_origin_service_id), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
    array_agg(r.event_id), 'fold', now())
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  -- Sealed epochs over the moved events: the origin's lane by origin sequence, the local lane by local sequence.
  SELECT MIN(r.origin_sequence), MAX(r.origin_sequence) INTO v_min, v_max
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, digested)
  WHERE r.adopted AND r.digested AND r.origin_sequence IS NOT NULL;
  IF v_min IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refold_epochs_covering(p_origin_service_id, v_min, v_max);
  END IF;

  SELECT MIN(r.local_sequence), MAX(r.local_sequence) INTO v_min, v_max
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane AND r.local_sequence IS NOT NULL;
  SELECT f.epoch_width INTO v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = __EMPTY_UUID__::uuid;
  IF v_min IS NOT NULL AND v_width IS NOT NULL
     AND __SCHEMA__.refold_digest_epochs(__EMPTY_UUID__::uuid, v_min / v_width, v_max / v_width) > 0 THEN
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY
  SELECT r.stream_id, r.tenant_scope, r.event_type, COUNT(*)::int, (COUNT(*) FILTER (WHERE r.adopted))::int
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, digested)
  GROUP BY r.stream_id, r.tenant_scope, r.event_type
  ORDER BY r.stream_id, r.tenant_scope, r.event_type;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_adopt_redelivered_origin(UUID, UUID[], BIGINT[]) IS
'420 (#2181): its working rows are gathered into arrays, not a temporary table. 249 (#1790): reconciles a redelivery bundle with the events the store already holds. An empty origin takes the bundle''s origin and an empty origin commit sequence the event''s sequence (never overwriting, never re-attributing, never for the local service); the digest leaves the local lane for the origin''s; sealed epochs over the moved events are refolded and the origin generation advances when the local lane''s did. Returns per (stream, tenant, type) bucket the events present and the events that adopted something.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_reconcile_redelivered_bundle_by_first_hop');

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#pre-stamp-history-first-hop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_EmptyingALocalBucket_ReadsOnlyTheBucketsItChanges_NotTheWholeLocalLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_WithoutTheOriginsName_AdoptsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_ABlankOrUnknownOriginName_AdoptsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_ServiceNamesCompareWithoutCase_AndAnotherServiceInAnyCaseIsRefusedAsync</tests>
-- <docs>resilience/stream-integrity#pre-stamp-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_OnAStreamWhoseEarlierHistoryWasTruncated_IsRefused_AndReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_DeduplicationRemembers_TheStoreLost_IsRestored_AndTheBucketHealsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AnEventWhoseFirstHopNamesTheBundlesOrigin_AdoptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AnEventWhoseFirstHopNamesThisService_IsRefusedAndStaysLocalAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AFirstHopWithoutAServiceIdentity_AdoptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_TheLegacyLongFormHop_IsReadAndComparedWithoutCaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_EventsStoredWithoutAnOrigin_TakeTheBundlesOriginAndSequencesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_IntoSealedEpochs_RefoldsBothLanes_AndAdvancesTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBundle_ASecondCallOnTheConnection_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBundle_TwiceInOneTransaction_StartsFromAnEmptyWorkingTableAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>resilience/stream-integrity#per-bundle-work-table</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBundle_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryReconcileEquivalenceSqlTests.cs:ReconcileBundleByFirstHop_GivesWhatTheWorkTableVersionGaveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerCallFunctionsCreateNoTemporaryTableTests.cs:NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundle_by_first_hop(
  p_origin_service_id UUID,
  p_origin_service_name TEXT,
  p_event_ids UUID[],
  p_commit_sequences BIGINT[]
) RETURNS TABLE(stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER, adopted_count INTEGER,
                refused_truncated_count INTEGER, refused_foreign_origin_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_field_hops CONSTANT TEXT := 'Hops';        -- EnvelopeMetadata.Hops, as the emit chain stores it (230)
  c_unknown_service CONSTANT TEXT := 'unknown';  -- ServiceInstanceInfo.Unknown, lower-cased: no service identity
  v_local_service_id UUID;
  v_adoptable BOOLEAN;
  v_origin_name TEXT;
  v_min BIGINT;
  v_max BIGINT;
  v_width BIGINT;
  v_leaving_streams UUID[];
  -- 420: the bundle's stored events, one element per event, in parallel arrays (was a temporary table).
  v_w_ids UUID[];
  v_w_streams UUID[];
  v_w_tenants TEXT[];
  v_w_types TEXT[];
  v_w_left_local BOOLEAN[];
  v_w_local_seqs BIGINT[];
  v_w_origin_seqs BIGINT[];
  v_w_adopted BOOLEAN[];
  v_w_refused BOOLEAN[];
  v_w_digested BOOLEAN[];
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundle_by_first_hop: % event ids but % commit sequences',
      cardinality(p_event_ids), cardinality(p_commit_sequences);
  END IF;

  -- The emit chain's normalization (087): the local service and the empty id are "locally originated".
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;
  v_adoptable := p_origin_service_id IS NOT NULL
    AND p_origin_service_id <> __EMPTY_UUID__::uuid
    AND p_origin_service_id IS DISTINCT FROM v_local_service_id;
  -- No name (or the Unknown sentinel) leaves the first-hop check nothing to compare.
  v_origin_name := NULLIF(NULLIF(lower(btrim(p_origin_service_name)), ''), c_unknown_service);

  -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT es.stream_id AS sid
    FROM unnest(p_event_ids) AS u(eid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
    ORDER BY 1
  ) s;

  -- 420 (#2181): the rows are gathered into arrays by one statement. 321 kept a temporary table per connection
  -- instead, but every session reset dropped it, and a pooled connection is reset each time it is handed out, so it
  -- was created again on almost every call: catalog rows written and deleted, an invalidation for every other
  -- session, fresh plans, and new statement-statistics entries per call. The bundle answer had the same fix in 369
  -- (#2179).
  SELECT array_agg(w.event_id), array_agg(w.stream_id), array_agg(w.tenant_scope), array_agg(w.event_type),
         array_agg(w.left_local_lane), array_agg(w.local_sequence), array_agg(w.origin_sequence),
         array_agg(w.adopted), array_agg(w.refused), array_agg(w.digested)
  INTO v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs, v_w_adopted,
       v_w_refused, v_w_digested
  FROM (
  SELECT
    c.event_id,
    c.stream_id,
    c.tenant_scope,
    c.event_type,
    c.fills_origin AND NOT c.refused,
    c.local_sequence,
    c.origin_sequence,
    (c.fills_origin AND NOT c.refused) OR c.fills_sequence,
    c.refused,
    c.digested
  FROM (
    SELECT
      es.event_id,
      es.stream_id,
      COALESCE(es.scope ->> 't', '') AS tenant_scope,
      es.event_type,
      v_adoptable AND es.origin_service_id IS NULL AS fills_origin,
      COALESCE(v_adoptable AND es.origin_service_id = p_origin_service_id AND es.origin_commit_sequence IS NULL
        AND COALESCE(m.seq, 0) > 0, false) AS fills_sequence,
      COALESCE(fh.service IS NOT NULL AND fh.service <> c_unknown_service AND fh.service <> v_origin_name, false) AS refused,
      es.commit_sequence AS local_sequence,
      COALESCE(es.origin_commit_sequence, NULLIF(m.seq, 0)) AS origin_sequence,
      COALESCE(es.flags, 0) & c_flag_ephemeral = 0
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
        AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) AS digested
    FROM (SELECT DISTINCT ON (u.eid) u.eid, u.seq FROM unnest(p_event_ids, p_commit_sequences) AS u(eid, seq) ORDER BY u.eid) m
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
    -- The first hop's service, read only for an event the bundle would give an origin, and only when there is a
    -- name to compare it with: the one-time catch-up, never an event that already carries its origin.
    LEFT JOIN LATERAL (
      SELECT NULLIF(lower(btrim(COALESCE(
               h.hop -> 'si' ->> 'sn',
               h.hop -> 'ServiceInstance' ->> 'ServiceName'))), '') AS service
      FROM (SELECT (eb.metadata -> c_field_hops) -> 0 AS hop
            FROM __SCHEMA__.wh_event_body eb
            WHERE eb.event_id = es.event_id
              AND v_adoptable AND v_origin_name IS NOT NULL AND es.origin_service_id IS NULL) h
    ) fh ON true
  ) c
  ) AS w(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence, adopted,
        refused, digested);

  UPDATE __SCHEMA__.wh_event_store es
  SET origin_service_id = p_origin_service_id, origin_commit_sequence = r.origin_sequence
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE es.event_id = r.event_id AND r.adopted;

  -- 301 (#2000): the local-lane statements below are driven by the streams the bundle moved, as an array the planner
  -- can see. Joined only against the temporary rows (which carry no statistics), the planner reached the digests
  -- through the primary key's leading column, which matches the whole local lane, and read every local bucket on
  -- every bundle. With the streams named, idx_stream_digests_stream finds the handful of buckets the bundle changes.
  SELECT array_agg(DISTINCT r.stream_id) INTO v_leaving_streams
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins the origin's.
  -- 336 (#2106): every lane leaves through the one fold helper.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(__EMPTY_UUID__::uuid), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
    array_agg(r.event_id), 'unfold', now())
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  USING (SELECT DISTINCT r.tenant_scope, r.event_type, r.stream_id
         FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                       v_w_adopted, v_w_refused, v_w_digested)
                AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
                     adopted, refused, digested)
         WHERE r.adopted AND r.digested AND r.left_local_lane) l
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid
    AND d.scope_tenant = l.tenant_scope AND d.event_type = l.event_type AND d.stream_id = l.stream_id
    AND d.stream_id = ANY(v_leaving_streams)
    AND d.event_count <= 0;

  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(p_origin_service_id), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
    array_agg(r.event_id), 'fold', now())
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  -- Sealed epochs over the moved events: the origin's lane by origin sequence, the local lane by local sequence.
  SELECT MIN(r.origin_sequence), MAX(r.origin_sequence) INTO v_min, v_max
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.origin_sequence IS NOT NULL;
  IF v_min IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refold_epochs_covering(p_origin_service_id, v_min, v_max);
  END IF;

  SELECT MIN(r.local_sequence), MAX(r.local_sequence) INTO v_min, v_max
  FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                v_w_adopted, v_w_refused, v_w_digested)
         AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
              adopted, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane AND r.local_sequence IS NOT NULL;
  SELECT f.epoch_width INTO v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = __EMPTY_UUID__::uuid;
  IF v_min IS NOT NULL AND v_width IS NOT NULL
     AND __SCHEMA__.refold_digest_epochs(__EMPTY_UUID__::uuid, v_min / v_width, v_max / v_width) > 0 THEN
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY
  SELECT b.sid, b.tenant, b.etype, SUM(b.present)::int, SUM(b.adopted)::int, SUM(b.truncated)::int, SUM(b.foreign_origin)::int
  FROM (
    SELECT r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype, 1 AS present,
           r.adopted::int AS adopted, 0 AS truncated, r.refused::int AS foreign_origin
    FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                  v_w_adopted, v_w_refused, v_w_digested)
           AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
                adopted, refused, digested)
    UNION ALL
    -- 252: a child store_inbox_messages refused to restore, keyed as every digest fold keys it (the stream, the
    -- scope's tenant, the emit chain's normalized type).
    SELECT i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type), 0, 0, 1, 0
    FROM __SCHEMA__.wh_inbox i
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE i.message_id = ANY(p_event_ids)
      AND ist.error = __REPAIR_REFUSED_TRUNCATED__
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id)
  ) b
  GROUP BY b.sid, b.tenant, b.etype
  ORDER BY b.sid, b.tenant, b.etype;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundle_by_first_hop(UUID, TEXT, UUID[], BIGINT[]) IS
'420 (#2181): its working rows are gathered into arrays; 321''s per-connection temporary table is gone. 301 (#2000): the local-lane digest update and delete are driven by the moved streams through idx_stream_digests_stream. 253 (#1843, #1844): reconciles a redelivery bundle with the store; the one entry point per bundle. wh_adopt_redelivered_origin (249) with a first-hop check, plus the children store_inbox_messages (252) refused to restore past deduplication because the stream had earlier history truncated here (refused_truncated_count). An empty origin takes the bundle''s origin only when the stored event''s first hop names the bundle''s origin service (p_origin_service_name, compared without case) or carries no service identity; a first hop naming another service refuses it (the event keeps its empty origin and its local digest) and is counted in refused_foreign_origin_count. Everything else is 249''s: an empty origin commit sequence takes the event''s sequence, nothing is overwritten or re-attributed, the local service adopts nothing, digests move lanes and sealed epochs are refolded. Returns per (stream, tenant, type) bucket the events present, adopted, refused as truncated and refused by the first-hop check.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_reconcile_redelivered_bundles');

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_RefoldsEachSealedEpochOnce_NotOncePerBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_FillsEverySequence_AsTheSingleBundleCallsWouldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_AnEventTwoBundlesName_AdoptsOnce_ForTheFirstBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_EventsThatAlreadyCarryTheirOrigin_AdoptNothing_AndRefoldNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_ReportsOriginsFilled_ApartFromSequencesFilledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_AFirstHopNamingAnotherService_IsRefused_AndALaterBundleOfThatServiceAdoptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_MovesDigestsFromTheLocalLane_AndRefoldsTheLocalLaneOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_ReportsTheChildrenTheStoreRefusedToRestore_PerBundleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_MismatchedArrays_AreRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBatchReconcileSqlTests.cs:Batch_UnderAGenericPlan_FindsStoredEventsByKey_NotAStoreScanAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>resilience/stream-integrity#per-bundle-work-table</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBundleCatalogChurnSqlTests.cs:ReconcileBatch_AfterTheSessionIsReset_WritesNothingToTheSystemCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryReconcileEquivalenceSqlTests.cs:ReconcileBundles_GivesWhatTheWorkTableVersionGaveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerCallFunctionsCreateNoTemporaryTableTests.cs:NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundles(
  p_bundle_origins   UUID[],     -- per bundle, by position (1-based): the origin it was published by
  p_bundle_names     TEXT[],     -- per bundle: the origin's service name from its first hop, or NULL
  p_event_bundles    INTEGER[],  -- per event: the position of its bundle in p_bundle_origins
  p_event_ids        UUID[],     -- per event: its id
  p_commit_sequences BIGINT[]    -- per event: its origin sequence, or NULL / 0 when the origin did not stamp one
) RETURNS TABLE(bundle INTEGER, stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER,
                adopted_count INTEGER, origins_filled_count INTEGER, refused_truncated_count INTEGER,
                refused_foreign_origin_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;        -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;    -- EventFlags.AtMostOnce (1 << 5), 214
  c_field_hops CONSTANT TEXT := 'Hops';          -- EnvelopeMetadata.Hops, as the emit chain stores it (230)
  c_unknown_service CONSTANT TEXT := 'unknown';  -- ServiceInstanceInfo.Unknown, lower-cased: no service identity
  v_local_service_id UUID;
  v_ids UUID[];
  v_origins UUID[];
  v_sequences BIGINT[];
  v_leaving_streams UUID[];
  v_local_width BIGINT;
  v_local_refolded INTEGER := 0;
  r_epoch RECORD;
  -- 420: one element per (bundle, stored event), in parallel arrays (was a temporary table).
  v_w_bundles INTEGER[];
  v_w_ids UUID[];
  v_w_streams UUID[];
  v_w_tenants TEXT[];
  v_w_types TEXT[];
  v_w_origins UUID[];
  v_w_local_seqs BIGINT[];
  v_w_origin_seqs BIGINT[];
  v_w_adopted BOOLEAN[];
  v_w_left_local BOOLEAN[];
  v_w_refused BOOLEAN[];
  v_w_digested BOOLEAN[];
BEGIN
  IF cardinality(p_bundle_origins) IS DISTINCT FROM cardinality(p_bundle_names) THEN
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundles: % bundle origins but % bundle names',
      cardinality(p_bundle_origins), cardinality(p_bundle_names);
  END IF;
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_event_bundles)
     OR cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundles: % event ids, % bundle positions and % commit sequences',
      cardinality(p_event_ids), cardinality(p_event_bundles), cardinality(p_commit_sequences);
  END IF;

  -- The emit chain's normalization (087): the local service and the empty id are "locally originated".
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;

  -- Every bundle's streams' append locks, once, in the emit chain's order.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT es.stream_id AS sid
    FROM unnest(p_event_ids) AS u(eid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
    ORDER BY 1
  ) s;

  -- One row per (bundle, stored event). Each event adopts for the first bundle that can fill it; the others count it
  -- present only, as a later single-bundle call would have found it already filled.
  -- 420 (#2181): the rows are gathered into arrays by one statement. The temporary table was dropped by every
  -- session reset, and a pooled connection is reset each time it is handed out, so it was created again on almost
  -- every call: catalog rows written and deleted, an invalidation for every other session, fresh plans, and new
  -- statement-statistics entries per call. The bundle answer had the same fix in 369 (#2179).
  SELECT array_agg(g.bundle), array_agg(g.event_id), array_agg(g.stream_id), array_agg(g.tenant_scope),
         array_agg(g.event_type), array_agg(g.origin_service_id), array_agg(g.local_sequence),
         array_agg(g.origin_sequence), array_agg(g.adopted), array_agg(g.left_local_lane), array_agg(g.refused),
         array_agg(g.digested)
  INTO v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs, v_w_origin_seqs,
       v_w_adopted, v_w_left_local, v_w_refused, v_w_digested
  FROM (
  WITH bundles AS (
    SELECT b.pos,
           b.origin,
           b.origin IS NOT NULL AND b.origin <> __EMPTY_UUID__::uuid
             AND b.origin IS DISTINCT FROM v_local_service_id AS adoptable,
           NULLIF(NULLIF(lower(btrim(b.name)), ''), c_unknown_service) AS origin_name
    FROM unnest(p_bundle_origins, p_bundle_names) WITH ORDINALITY AS b(origin, name, pos)
  ), events AS (
    SELECT DISTINCT ON (u.pos, u.eid) u.pos::int AS pos, u.eid, NULLIF(u.seq, 0) AS seq
    FROM unnest(p_event_bundles, p_event_ids, p_commit_sequences) AS u(pos, eid, seq)
    ORDER BY u.pos, u.eid, u.seq DESC NULLS LAST
  ), candidates AS (
    SELECT
      e.pos,
      es.event_id,
      es.stream_id,
      COALESCE(es.scope ->> 't', '') AS tenant_scope,
      es.event_type,
      b.origin,
      es.commit_sequence AS local_sequence,
      es.origin_commit_sequence AS stored_sequence,
      e.seq,
      b.adoptable AND es.origin_service_id IS NULL AS fills_origin,
      COALESCE(b.adoptable AND es.origin_service_id = b.origin AND es.origin_commit_sequence IS NULL
        AND e.seq IS NOT NULL, false) AS fills_sequence,
      COALESCE(fh.service IS NOT NULL AND fh.service <> c_unknown_service AND fh.service <> b.origin_name, false)
        AS refused,
      COALESCE(es.flags, 0) & c_flag_ephemeral = 0
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
        AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) AS digested
    FROM events e
    JOIN bundles b ON b.pos = e.pos
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = e.eid
    -- The first hop's service, read only for an event the bundle would give an origin, and only when there is a
    -- name to compare it with: the one-time catch-up, never an event that already carries its origin.
    LEFT JOIN LATERAL (
      SELECT NULLIF(lower(btrim(COALESCE(
               h.hop -> 'si' ->> 'sn',
               h.hop -> 'ServiceInstance' ->> 'ServiceName'))), '') AS service
      FROM (SELECT (eb.metadata -> c_field_hops) -> 0 AS hop
            FROM __SCHEMA__.wh_event_body eb
            WHERE eb.event_id = es.event_id
              AND b.adoptable AND b.origin_name IS NOT NULL AND es.origin_service_id IS NULL) h
    ) fh ON true
  ), judged AS (
    SELECT c.*, (c.fills_origin AND NOT c.refused) OR c.fills_sequence AS can_adopt
    FROM candidates c
  ), winners AS (
    SELECT DISTINCT ON (j.event_id) j.event_id, j.pos, j.origin, j.can_adopt, j.fills_origin AND NOT j.refused AS fills_origin
    FROM judged j
    ORDER BY j.event_id, j.can_adopt DESC, j.pos
  )
  SELECT
    j.pos,
    j.event_id,
    j.stream_id,
    j.tenant_scope,
    j.event_type,
    CASE WHEN w.can_adopt THEN w.origin END,
    j.local_sequence,
    CASE WHEN w.can_adopt THEN COALESCE(j.stored_sequence, j.seq, (
      SELECT max(o.seq) FROM judged o WHERE o.event_id = j.event_id AND o.origin = w.origin)) END,
    w.can_adopt AND w.pos = j.pos,
    w.can_adopt AND w.pos = j.pos AND w.fills_origin,
    -- Refused as a single-bundle call would have refused it: before the bundle that filled it, or when none did.
    j.refused AND (NOT w.can_adopt OR j.pos < w.pos),
    j.digested
  FROM judged j
  JOIN winners w ON w.event_id = j.event_id
  ) AS g(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence, origin_sequence,
        adopted, left_local_lane, refused, digested);

  -- The fills, as arrays the planner can see, so each stored event is found by its key.
  SELECT array_agg(r.event_id ORDER BY r.event_id), array_agg(r.origin_service_id ORDER BY r.event_id),
         array_agg(r.origin_sequence ORDER BY r.event_id)
  INTO v_ids, v_origins, v_sequences
  FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
         AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
              origin_sequence, adopted, left_local_lane, refused, digested)
  WHERE r.adopted;

  IF v_ids IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_event_store es
    SET origin_service_id = m.origin, origin_commit_sequence = m.seq
    FROM unnest(v_ids, v_origins, v_sequences) AS m(eid, origin, seq)
    WHERE es.event_id = m.eid;
  END IF;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins its origin's.
  SELECT array_agg(DISTINCT r.stream_id) INTO v_leaving_streams
  FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
         AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
              origin_sequence, adopted, left_local_lane, refused, digested)
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  IF v_leaving_streams IS NOT NULL THEN
    -- 336 (#2106): every lane leaves through the one fold helper.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(__EMPTY_UUID__::uuid), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
      array_agg(r.event_id), 'unfold', now())
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    WHERE r.adopted AND r.digested AND r.left_local_lane;
    DELETE FROM __SCHEMA__.wh_stream_digests d
    USING (SELECT DISTINCT r.tenant_scope, r.event_type, r.stream_id
           FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                         v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
                  AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                       origin_sequence, adopted, left_local_lane, refused, digested)
           WHERE r.adopted AND r.digested AND r.left_local_lane) l
    WHERE d.origin_service_id = __EMPTY_UUID__::uuid
      AND d.scope_tenant = l.tenant_scope AND d.event_type = l.event_type AND d.stream_id = l.stream_id
      AND d.stream_id = ANY(v_leaving_streams)
      AND d.event_count <= 0;

    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(r.origin_service_id), array_agg(r.tenant_scope), array_agg(r.event_type), array_agg(r.stream_id),
      array_agg(r.event_id), 'fold', now())
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    WHERE r.adopted AND r.digested AND r.left_local_lane;
  END IF;

  -- The sealed epochs that hold a moved event, each once: the origins' lanes by origin sequence.
  FOR r_epoch IN
    SELECT DISTINCT r.origin_service_id AS lane, r.origin_sequence / f.epoch_width AS epoch, f.epoch_width AS width
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = r.origin_service_id
    WHERE r.adopted AND r.digested AND r.origin_sequence IS NOT NULL
      AND r.origin_sequence >= 0 AND r.origin_sequence / f.epoch_width <= f.closed_through_epoch
    ORDER BY 1, 2
  LOOP
    PERFORM __SCHEMA__._wh_fold_digest_epoch(r_epoch.lane, r_epoch.epoch, r_epoch.width, NOW());
  END LOOP;

  -- The local lane by local sequence; a refold there moves the origin generation, once.
  SELECT f.epoch_width INTO v_local_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = __EMPTY_UUID__::uuid;
  IF v_local_width IS NOT NULL THEN
    FOR r_epoch IN
      SELECT DISTINCT r.local_sequence / v_local_width AS epoch
      FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                    v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
             AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                  origin_sequence, adopted, left_local_lane, refused, digested)
      JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = __EMPTY_UUID__::uuid
      WHERE r.adopted AND r.digested AND r.left_local_lane AND r.local_sequence IS NOT NULL
        AND r.local_sequence >= 0 AND r.local_sequence / v_local_width <= f.closed_through_epoch
      ORDER BY 1
    LOOP
      PERFORM __SCHEMA__._wh_fold_digest_epoch(__EMPTY_UUID__::uuid, r_epoch.epoch, v_local_width, NOW());
      v_local_refolded := v_local_refolded + 1;
    END LOOP;
    IF v_local_refolded > 0 THEN
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  RETURN QUERY
  SELECT x.pos, x.sid, x.tenant, x.etype, SUM(x.present)::int, SUM(x.adopted)::int, SUM(x.filled)::int,
         SUM(x.truncated)::int, SUM(x.foreign_origin)::int
  FROM (
    SELECT r.bundle AS pos, r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype, 1 AS present,
           r.adopted::int AS adopted, r.left_local_lane::int AS filled, 0 AS truncated, r.refused::int AS foreign_origin
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    UNION ALL
    -- 252: a child store_inbox_messages refused to restore, keyed as every digest fold keys it (the stream, the
    -- scope's tenant, the emit chain's normalized type), for each bundle that named it.
    SELECT u.pos::int, i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type),
           0, 0, 0, 1, 0
    FROM (SELECT DISTINCT p.pos, p.eid FROM unnest(p_event_bundles, p_event_ids) AS p(pos, eid)) u
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = u.eid
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE ist.error = __REPAIR_REFUSED_TRUNCATED__
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id)
  ) x
  GROUP BY x.pos, x.sid, x.tenant, x.etype
  ORDER BY x.pos, x.sid, x.tenant, x.etype;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundles(UUID[], TEXT[], INTEGER[], UUID[], BIGINT[]) IS
'420 (#2181): its working rows are gathered into arrays, not a temporary table. 335 (#2099): reconciles many redelivery bundles at once, with the per-bundle semantics of wh_reconcile_redelivered_bundle_by_first_hop (253, 321): an empty origin takes its bundle''s origin unless the stored first hop names another service, an empty origin sequence of an event that carries the bundle''s origin takes the bundle''s sequence, nothing is overwritten, the local service adopts nothing, digests move lanes. Each event adopts at most once per batch, for the first bundle that can fill it. Every bundle''s streams are locked once in order, the fills are one update, and each sealed epoch holding a moved event is refolded once per batch rather than every epoch of each bundle''s range. Returns per (bundle, stream, tenant, type) the events present, adopted, whose origin was filled (origins_filled_count; the rest of adopted took only the sequence), refused as truncated and refused by the first-hop check.';
