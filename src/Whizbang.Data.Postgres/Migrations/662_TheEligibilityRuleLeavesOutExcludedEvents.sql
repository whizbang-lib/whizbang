-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 662_TheEligibilityRuleLeavesOutExcludedEvents.sql
-- Date: 2026-11-27
-- Description: Version 2 of the integrity eligibility rule (#2588): an event an operator excluded from integrity
--              (EventFlags.IntegrityExcluded, 256) is counted by no digest and no checkpoint.
--
--              The rule is written once, as migration constants, and every digest fold, answer, heal, checkpoint and
--              cleanup path reads it (#2352). Version 1 is the DIGEST_ELIGIBLE and INTEGRITY_COUNTED pair, and every
--              shipped migration writes it. Those files are never edited, and changing the constants' values would
--              change the hash of every shipped file that writes them, so the ledger would re-run all of them, data
--              passes included. Version 2 is therefore its own pair of tokens, DIGEST_ELIGIBLE_V2 and
--              INTEGRITY_COUNTED_V2 (constants.txt), and this file redefines every function whose latest
--              definition reads the rule, each copied whole from its last word with only the token changed
--              (README rule 5). The version 1 tokens stay defined for the shipped files only;
--              DigestEligibilityGuardTests fails a latest definition that still reads them.
--
--              No index changes: no index predicate reads the rule. The held-fold trigger already fires on a change
--              to flags (452), so setting the flag on a held row takes it out of the held fold.
--
--              RuleSemantics.ELIGIBILITY_SQL_VERSION moves to 2 with this rule, so a pair of services on different
--              versions halts its comparisons until both run version 2 (#2588 decision D2, open question #2615).
--
-- Reversal: reversible (the down file restores each function's previous definition, counting by version 1)
-- Dependencies: the last word of each function below (its section header names the file), 452 (the held-fold
--               triggers), 336 (_wh_fold_stream_digests, _wh_digest_lane)
-- Objects: _wh_track_unsequenced_digest, wh_rebuild_unsequenced_digests,
--          wh_unsequenced_received_type_digests, wh_remove_eventless_stream_digests,
--          wh_adopt_redelivered_origin, wh_reconcile_redelivered_bundle_by_first_hop,
--          wh_reconcile_redelivered_bundles, wh_integrity_correct_origins, wh_refile_events,
--          _wh_flag_at_most_once_occurrences, _wh_reclassify_ephemeral_events, wh_restore_archived_stream,
--          _wh_carry_cut, _wh_uncarry_restored, _wh_heal_stream_digests_statement, _wh_epoch_buckets,
--          compute_type_digests_epoch, compute_type_digests_epoch_window, wh_compute_stream_digests_window,
--          wh_seed_stream_digests_batch, wh_append_event, wh_retire_event_types, _wh_cut_awaits_seal,
--          _emit_event_store_chain, _emit_event_store_chain_for_inbox, _wh_touch_sealed_epochs_on_insert,
--          _wh_touch_sealed_epochs_on_update, wh_integrity_reattribute_origins,
--          wh_reconcile_redelivered_bundle_counting_satisfied,
--          wh_reconcile_redelivered_bundles_counting_satisfied, wh_restore_written_here_origins
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ── _wh_track_unsequenced_digest: last word 452, copied whole; the eligibility rule is version 2 ──
-- One row trigger for insert, update and delete: removes the old row's contribution when it was in the set, adds the
-- new row's when it is.
-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:EveryKindOfChange_LeavesTheAnswerEqualToAFoldOfTheRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_track_unsequenced_digest() RETURNS TRIGGER AS $$
BEGIN
  IF TG_OP <> 'INSERT'
     AND OLD.origin_service_id IS NOT NULL AND OLD.origin_commit_sequence IS NULL
     AND __DIGEST_ELIGIBLE_V2__(OLD) THEN
    INSERT INTO __SCHEMA__.wh_unsequenced_digest_changes
      (origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count)
    VALUES (OLD.origin_service_id, COALESCE(OLD.scope ->> 't', ''), OLD.event_type,
            __SCHEMA__._wh_digest_lane(OLD.event_id, 0), __SCHEMA__._wh_digest_lane(OLD.event_id, 1), -1);
  END IF;
  IF TG_OP <> 'DELETE'
     AND NEW.origin_service_id IS NOT NULL AND NEW.origin_commit_sequence IS NULL
     AND __DIGEST_ELIGIBLE_V2__(NEW) THEN
    INSERT INTO __SCHEMA__.wh_unsequenced_digest_changes
      (origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count)
    VALUES (NEW.origin_service_id, COALESCE(NEW.scope ->> 't', ''), NEW.event_type,
            __SCHEMA__._wh_digest_lane(NEW.event_id, 0), __SCHEMA__._wh_digest_lane(NEW.event_id, 1), 1);
  END IF;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- ── wh_rebuild_unsequenced_digests: last word 452, copied whole; the eligibility rule is version 2 ──
-- Recomputes the totals from the rows. The SHARE lock keeps writers out while it reads, so no change is missed or
-- counted twice; readers are not blocked.
-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ARebuild_FoldsTheRowsTheTrackingNeverSaw_AndAnswersAsTheRowsDoAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_rebuild_unsequenced_digests() RETURNS BIGINT AS $$
DECLARE
  v_rows BIGINT;
BEGIN
  LOCK TABLE __SCHEMA__.wh_event_store IN SHARE MODE;
  DELETE FROM __SCHEMA__.wh_unsequenced_digest_changes;
  DELETE FROM __SCHEMA__.wh_unsequenced_digest_totals;
  INSERT INTO __SCHEMA__.wh_unsequenced_digest_totals
    (origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count)
  SELECT es.origin_service_id, COALESCE(es.scope ->> 't', ''), es.event_type,
         bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)), bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)),
         COUNT(*)
  FROM __SCHEMA__.wh_event_store es
  WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL
    AND __DIGEST_ELIGIBLE_V2__(es)
  GROUP BY 1, 2, 3;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

-- ── wh_unsequenced_received_type_digests: last word 452, copied whole; the eligibility rule is version 2 ──
-- The type-level answer: one row per (tenant, type) of the eligible events held from the origin without its commit
-- sequence and older than the settle window.
-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ByType_UnderAGenericPlan_ReadsOnlyTheUnsettledRowsOfTheEventStore_NeverItsHistoryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:EveryKindOfChange_LeavesTheAnswerEqualToAFoldOfTheRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedReceivedDigestSqlTests.cs:ByType_FoldsPerTenantAndType_AcrossStreamsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_unsequenced_received_type_digests(
  p_origin_service_id UUID,
  p_event_types TEXT[],
  p_settle INTERVAL
) RETURNS TABLE(scope_tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_fold_batch CONSTANT INTEGER := 10000;
BEGIN
  PERFORM __SCHEMA__._wh_fold_unsequenced_digest_changes(p_origin_service_id, c_fold_batch);
  RETURN QUERY
  WITH parts AS (
    SELECT t.scope_tenant AS te, t.event_type AS ty, t.digest_lo AS lo, t.digest_hi AS hi, t.event_count AS n
    FROM __SCHEMA__.wh_unsequenced_digest_totals t
    WHERE t.origin_service_id = p_origin_service_id AND t.event_type = ANY(p_event_types)
    UNION ALL
    SELECT c.scope_tenant, c.event_type, c.digest_lo, c.digest_hi, c.event_count::bigint
    FROM __SCHEMA__.wh_unsequenced_digest_changes c
    WHERE c.origin_service_id = p_origin_service_id AND c.event_type = ANY(p_event_types)
    UNION ALL
    -- The held rows inside the settle window leave the answer: XOR removes them, the count subtracts them.
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type,
           __SCHEMA__._wh_digest_lane(es.event_id, 0), __SCHEMA__._wh_digest_lane(es.event_id, 1), -1::bigint
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = p_origin_service_id
      AND es.origin_service_id IS NOT NULL
      AND es.origin_commit_sequence IS NULL
      AND es.created_at >= NOW() - p_settle
      AND es.event_type = ANY(p_event_types)
      AND __DIGEST_ELIGIBLE_V2__(es)
  )
  SELECT p.te, p.ty, bit_xor(p.lo), bit_xor(p.hi), SUM(p.n)::int
  FROM parts p
  GROUP BY p.te, p.ty
  HAVING SUM(p.n) > 0
  ORDER BY p.te, p.ty;
END;
$$ LANGUAGE plpgsql;

-- ── wh_remove_eventless_stream_digests: last word 453, copied whole; the eligibility rule is version 2 ──
-- The stream digest buckets that hold no event the rule counts, removed. A bucket is (origin lane, tenant, type,
-- stream); each is probed through the stream index, so the cost is one probe per bucket, paid once.
-- <docs>resilience/stream-integrity#one-eligibility-rule</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEligibilitySqlTests.cs:DigestBucketsWithNoEligibleEvents_AreRemoved_AndBucketsWithEventsKeptAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_remove_eventless_stream_digests() RETURNS BIGINT AS $$
DECLARE
  v_removed BIGINT;
BEGIN
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = d.stream_id
      AND es.event_type = d.event_type
      AND COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) = d.origin_service_id
      AND COALESCE(es.scope ->> 't', '') = d.scope_tenant
      AND __DIGEST_ELIGIBLE_V2__(es));
  GET DIAGNOSTICS v_removed = ROW_COUNT;
  RETURN v_removed;
END;
$$ LANGUAGE plpgsql;

-- ── wh_adopt_redelivered_origin: last word 453, copied whole; the eligibility rule is version 2 ──
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
    __DIGEST_ELIGIBLE_V2__(es)
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

-- ── wh_reconcile_redelivered_bundle_by_first_hop: last word 453, copied whole; the eligibility rule is version 2 ──
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
      __DIGEST_ELIGIBLE_V2__(es) AS digested
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

-- ── wh_reconcile_redelivered_bundles: last word 453, copied whole; the eligibility rule is version 2 ──
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
      __DIGEST_ELIGIBLE_V2__(es) AS digested
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

-- ── wh_integrity_correct_origins: last word 453, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:Correct_ARowUnderAnOriginThatDoesNotOwnItsType_TakesTheOwner_AndItsDigestMovesLanesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:Correct_LeavesTheOwnersRows_AndTypesNotListedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:Correct_RefoldsTheWrongOriginsSealedEpoch_AndReArmsBothLanesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:Correct_FromAStaleCursor_ChangesNothingAndReportsNotAdvancedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:WindowAndCorrection_UnderAGenericPlan_RangeScanTheCommitSequenceIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_correct_origins(
  p_after BIGINT,
  p_through BIGINT,
  p_event_types TEXT[],
  p_owner_ids UUID[]
) RETURNS TABLE(advanced BOOLEAN, event_type TEXT, from_origin UUID, to_owner UUID, corrected_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_local_service_id UUID;
  v_ids UUID[];
  v_streams UUID[];
  v_tenants TEXT[];
  v_types TEXT[];
  v_from UUID[];
  v_owners UUID[];
  v_sequences BIGINT[];
  v_digested BOOLEAN[];
  r_epoch RECORD;
BEGIN
  IF cardinality(p_event_types) IS DISTINCT FROM cardinality(p_owner_ids) THEN
    RAISE EXCEPTION 'wh_integrity_correct_origins: % event types and % owner ids must pair by position',
      cardinality(p_event_types), cardinality(p_owner_ids);
  END IF;

  -- The cursor compare-and-swap first: it also serializes overlapping passes on the state row.
  UPDATE __SCHEMA__.wh_integrity_origin_correction c
  SET through_commit_sequence = p_through, updated_at = now()
  WHERE c.singleton AND c.through_commit_sequence = p_after AND p_through >= p_after;
  IF NOT FOUND THEN
    RETURN QUERY SELECT false, NULL::text, NULL::uuid, NULL::uuid, 0;
    RETURN;
  END IF;

  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;

  -- The batch's rows to move, held in parallel arrays rather than a temp table: no catalog churn per call.
  SELECT array_agg(c.event_id), array_agg(c.stream_id), array_agg(c.tenant_scope), array_agg(c.event_type),
         array_agg(c.from_origin), array_agg(c.owner_id), array_agg(c.origin_sequence), array_agg(c.digested)
    INTO v_ids, v_streams, v_tenants, v_types, v_from, v_owners, v_sequences, v_digested
  FROM (
    SELECT es.event_id, es.stream_id, COALESCE(es.scope ->> 't', '') AS tenant_scope, es.event_type,
           es.origin_service_id AS from_origin, t.owner_id, es.origin_commit_sequence AS origin_sequence,
           __DIGEST_ELIGIBLE_V2__(es) AS digested
    FROM __SCHEMA__.wh_event_store es
    JOIN unnest(p_event_types, p_owner_ids) AS t(event_type, owner_id) ON t.event_type = es.event_type
    WHERE es.commit_sequence > p_after
      AND es.commit_sequence <= p_through
      AND es.origin_service_id IS NOT NULL
      AND es.origin_service_id <> t.owner_id
      AND t.owner_id IS DISTINCT FROM v_local_service_id
    ORDER BY es.commit_sequence
  ) c;

  IF v_ids IS NULL THEN
    RETURN QUERY SELECT true, NULL::text, NULL::uuid, NULL::uuid, 0;
    RETURN;
  END IF;

  -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (SELECT DISTINCT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY 1) s;

  UPDATE __SCHEMA__.wh_event_store es
  SET origin_service_id = m.owner_id, origin_commit_sequence = NULL
  FROM unnest(v_ids, v_owners) AS m(event_id, owner_id)
  WHERE es.event_id = m.event_id;

  -- Digests: every lane leaves the wrong origin's bucket and joins the owner's, through the one fold helper.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(m.from_origin), array_agg(m.tenant_scope), array_agg(m.event_type), array_agg(m.stream_id),
    array_agg(m.event_id), 'unfold', now())
  FROM unnest(v_ids, v_streams, v_tenants, v_types, v_from, v_digested)
    AS m(event_id, stream_id, tenant_scope, event_type, from_origin, digested)
  WHERE m.digested;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  USING (SELECT DISTINCT u.from_origin FROM unnest(v_from) AS u(from_origin)) f
  WHERE d.origin_service_id = f.from_origin
    AND d.event_count <= 0
    AND d.stream_id = ANY(v_streams);
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(m.owner_id), array_agg(m.tenant_scope), array_agg(m.event_type), array_agg(m.stream_id),
    array_agg(m.event_id), 'fold', now())
  FROM unnest(v_ids, v_streams, v_tenants, v_types, v_owners, v_digested)
    AS m(event_id, stream_id, tenant_scope, event_type, owner_id, digested)
  WHERE m.digested;

  -- The wrong origin's sealed epochs that held a moved event, each once, by its old origin sequence. The owner's lane
  -- holds the moved events without a sequence, so none of its epochs changed.
  FOR r_epoch IN
    SELECT DISTINCT m.from_origin AS lane, m.origin_sequence / f.epoch_width AS epoch, f.epoch_width AS width
    FROM unnest(v_from, v_sequences, v_digested) AS m(from_origin, origin_sequence, digested)
    JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = m.from_origin
    WHERE m.digested AND m.origin_sequence IS NOT NULL AND m.origin_sequence >= 0
      AND m.origin_sequence / f.epoch_width <= f.closed_through_epoch
    ORDER BY 1, 2
  LOOP
    PERFORM __SCHEMA__._wh_fold_digest_epoch(r_epoch.lane, r_epoch.epoch, r_epoch.width, now());
  END LOOP;

  -- Both lanes' ledger buckets the moved rows touched changed on this side: their ladders start afresh.
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET unhealable_at = NULL, exhausted_at = NULL, answer_pending_until = NULL, answered_at = NULL, repair_attempts = 0
  FROM (
    SELECT m.lane, m.tenant_scope, m.event_type, m.stream_id
    FROM unnest(v_owners, v_tenants, v_types, v_streams) AS m(lane, tenant_scope, event_type, stream_id)
    UNION
    SELECT m.lane, m.tenant_scope, m.event_type, m.stream_id
    FROM unnest(v_from, v_tenants, v_types, v_streams) AS m(lane, tenant_scope, event_type, stream_id)
    UNION
    SELECT m.lane, m.tenant_scope, m.event_type, __EMPTY_UUID__::uuid
    FROM unnest(v_owners, v_tenants, v_types) AS m(lane, tenant_scope, event_type)
    UNION
    SELECT m.lane, m.tenant_scope, m.event_type, __EMPTY_UUID__::uuid
    FROM unnest(v_from, v_tenants, v_types) AS m(lane, tenant_scope, event_type)
  ) k(lane, tenant_scope, event_type, stream_id)
  WHERE l.origin_service_id = k.lane AND l.tenant_scope = k.tenant_scope
    AND l.event_type = k.event_type AND l.stream_id = k.stream_id;

  RETURN QUERY
  SELECT true, m.event_type, m.from_origin, m.owner_id, COUNT(*)::int
  FROM unnest(v_types, v_from, v_owners) AS m(event_type, from_origin, owner_id)
  GROUP BY m.event_type, m.from_origin, m.owner_id
  ORDER BY m.event_type, m.from_origin, m.owner_id;
END;
$$ LANGUAGE plpgsql;

-- ── wh_refile_events: last word 453, copied whole; the eligibility rule is version 2 ──
-- <docs>operations/infrastructure/purging-streams#refiling-composite-children</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanoutChildRefileSqlTests.cs</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_refile_events(
  p_event_ids UUID[],
  p_target_streams UUID[]
) RETURNS TABLE(event_id UUID, from_stream UUID, to_stream UUID, to_version BIGINT) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_target_streams) THEN
    RAISE EXCEPTION 'wh_refile_events: % event ids but % target streams', cardinality(p_event_ids), cardinality(p_target_streams);
  END IF;
  IF __EMPTY_UUID__::uuid = ANY(p_target_streams) THEN
    RAISE EXCEPTION 'wh_refile_events: the empty stream id names no stream';
  END IF;

  -- The streams an append to either side would lock, in the emit chain's order, before anything is read.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT x.sid
    FROM (
      SELECT es.stream_id AS sid
      FROM unnest(p_event_ids, p_target_streams) AS m(eid, target)
      JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
      WHERE es.stream_id <> m.target
      UNION
      SELECT m.target
      FROM unnest(p_event_ids, p_target_streams) AS m(eid, target)
      JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
      WHERE es.stream_id <> m.target
    ) x
    ORDER BY x.sid
  ) s;

  CREATE TEMP TABLE IF NOT EXISTS _wh_refile_moves (
    event_id UUID PRIMARY KEY,
    from_stream UUID NOT NULL,
    to_stream UUID NOT NULL,
    to_version BIGINT NOT NULL,
    origin_service_id UUID,
    scope_tenant TEXT NOT NULL,
    event_type TEXT NOT NULL,
    digested BOOLEAN NOT NULL
  ) ON COMMIT DROP;
  TRUNCATE _wh_refile_moves;

  INSERT INTO _wh_refile_moves
  SELECT
    es.event_id,
    es.stream_id,
    m.target,
    COALESCE((SELECT MAX(t.version) FROM __SCHEMA__.wh_event_store t WHERE t.stream_id = m.target), 0)
      + ROW_NUMBER() OVER (
          PARTITION BY m.target
          ORDER BY es.origin_commit_sequence NULLS LAST, es.commit_sequence NULLS LAST, es.event_id),
    es.origin_service_id,
    COALESCE(es.scope ->> 't', ''),
    es.event_type,
    __DIGEST_ELIGIBLE_V2__(es)
  FROM (SELECT DISTINCT ON (u.eid) u.eid, u.target FROM unnest(p_event_ids, p_target_streams) AS u(eid, target) ORDER BY u.eid) m
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
  WHERE es.stream_id <> m.target;

  -- The move itself. New versions sit past the target's tail, so no (stream_id, version) pair collides.
  UPDATE __SCHEMA__.wh_event_store es
  SET stream_id = r.to_stream, aggregate_id = r.to_stream, version = r.to_version
  FROM _wh_refile_moves r
  WHERE es.event_id = r.event_id;

  -- The streams left behind keep contiguous versions, in their old order, from the version they started at (a stream
  -- written by a direct append starts at 0, one stored from the inbox at 1). The new numbers are worked out first; the
  -- stream is then negated, so the renumbering never meets a version another row of the stream still holds.
  CREATE TEMP TABLE IF NOT EXISTS _wh_refile_renumber (event_id UUID PRIMARY KEY, new_version BIGINT NOT NULL) ON COMMIT DROP;
  TRUNCATE _wh_refile_renumber;
  INSERT INTO _wh_refile_renumber
  SELECT t.event_id,
         MIN(t.version) OVER (PARTITION BY t.stream_id) + ROW_NUMBER() OVER (PARTITION BY t.stream_id ORDER BY t.version) - 1
  FROM __SCHEMA__.wh_event_store t
  WHERE t.stream_id IN (SELECT DISTINCT r.from_stream FROM _wh_refile_moves r);
  UPDATE __SCHEMA__.wh_event_store es SET version = -es.version - 1
  FROM _wh_refile_renumber n WHERE es.event_id = n.event_id;
  UPDATE __SCHEMA__.wh_event_store es SET version = n.new_version
  FROM _wh_refile_renumber n WHERE es.event_id = n.event_id;

  -- Integrity digests: XOR is self-inverse, so the event's hash leaves one bucket and joins the other.
  -- 336 (#2106): every lane leaves through the one fold helper.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(COALESCE(r.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(r.scope_tenant), array_agg(r.event_type), array_agg(r.from_stream),
    array_agg(r.event_id), 'unfold', now())
  FROM _wh_refile_moves r
  WHERE r.digested;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.event_count <= 0
    AND d.stream_id IN (SELECT DISTINCT r.from_stream FROM _wh_refile_moves r);

  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(COALESCE(r.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(r.scope_tenant), array_agg(r.event_type), array_agg(r.to_stream),
    array_agg(r.event_id), 'fold', now())
  FROM _wh_refile_moves r
  WHERE r.digested;

  -- Rows keyed by the event that also name its stream follow it.
  UPDATE __SCHEMA__.wh_inbox i SET stream_id = r.to_stream FROM _wh_refile_moves r WHERE i.message_id = r.event_id;
  UPDATE __SCHEMA__.wh_inbox_state st SET stream_id = r.to_stream FROM _wh_refile_moves r WHERE st.message_id = r.event_id;
  UPDATE __SCHEMA__.wh_perspective_applied pa SET stream_id = r.to_stream FROM _wh_refile_moves r WHERE pa.event_id = r.event_id;

  -- Work that would apply the event to the old row, and folds and positions that no longer describe either stream.
  DELETE FROM __SCHEMA__.wh_perspective_events pe USING _wh_refile_moves r WHERE pe.event_id = r.event_id;
  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn
  WHERE sn.stream_id IN (SELECT r.from_stream FROM _wh_refile_moves r UNION SELECT r.to_stream FROM _wh_refile_moves r);
  DELETE FROM __SCHEMA__.wh_perspective_cursors c USING _wh_refile_moves r
  WHERE c.stream_id = r.from_stream AND c.last_event_id = r.event_id;

  RETURN QUERY SELECT r.event_id, r.from_stream, r.to_stream, r.to_version FROM _wh_refile_moves r ORDER BY r.to_stream, r.to_version;
END;
$$ LANGUAGE plpgsql;

-- ── _wh_flag_at_most_once_occurrences: last word 453, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AtMostOnceOccurrenceIntegrityTests.cs:FlagAtMostOnceOccurrences_FlagsRowsStoredWithoutTheFlag_AndTakesThemOutOfDigestsAndEpochsAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_flag_at_most_once_occurrences()
RETURNS INTEGER AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5)
  c_at_most_once CONSTANT INTEGER := 1;        -- ScheduleDeliveryGuarantee.AtMostOnce
  v_flagged INTEGER := 0;
  v_streams UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
BEGIN
  -- An outbox row the emit chain could not store yet (a version conflict leaves it for the next claim) is
  -- re-emitted from wh_outbox.flags, so it carries the flag too. Reached by primary key from the runs, never
  -- by scanning the outbox for a metadata key.
  UPDATE __SCHEMA__.wh_outbox o
  SET flags = COALESCE(o.flags, 0) | c_flag_at_most_once
  FROM (SELECT DISTINCT r.occurrence_id FROM __SCHEMA__.wh_schedule_runs r) r
  WHERE o.message_id = r.occurrence_id
    -- not the digest rule: the outbox rows still to flag
    AND COALESCE(o.flags, 0) & c_flag_at_most_once = 0
    AND (o.metadata ->> 'deliveryGuarantee')::integer = c_at_most_once;

  -- The event-store rows. The outbox row records the guarantee declared at fire time and outranks the
  -- schedule, whose guarantee may have changed since; the schedule answers once the outbox row is gone.
  WITH targets AS (
    -- 453 (#2352): whether the row counted before it is flagged; its flags after the stamp say only that it no longer does.
    SELECT DISTINCT es.event_id, __DIGEST_ELIGIBLE_V2__(es) AS was_eligible
    FROM (SELECT DISTINCT r.occurrence_id, r.schedule_id FROM __SCHEMA__.wh_schedule_runs r) r
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = r.occurrence_id
    LEFT JOIN __SCHEMA__.wh_outbox o ON o.message_id = r.occurrence_id
    LEFT JOIN __SCHEMA__.wh_schedules s ON s.schedule_id = r.schedule_id
    -- not the digest rule: the event rows still to flag
    WHERE COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND COALESCE((o.metadata ->> 'deliveryGuarantee')::integer, s.delivery_guarantee::integer) = c_at_most_once
  ),
  flagged AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = COALESCE(es.flags, 0) | c_flag_at_most_once
    FROM targets t
    WHERE es.event_id = t.event_id
    RETURNING es.event_id, es.stream_id, es.event_type, es.scope, es.flags, es.origin_service_id, t.was_eligible,
              CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS lane_seq
  ),
  -- Only a row the old fold could have taken in (not ephemeral) is in a bucket; XOR is self-inverse, so folding
  -- its hashes again takes it out. Reads the flagged rows from RETURNING (data-modifying CTEs do not see each
  -- other's table effects). A missing bucket is a no-op; the verification sweep heals anything left over.
  -- 336 (#2106): every lane leaves through the one fold helper; read by the final SELECT, so it always runs.
  digest_subtract AS (
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(f.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(f.scope ->> 't', '')),
      array_agg(f.event_type), array_agg(f.stream_id), array_agg(f.event_id), 'unfold', NOW()) AS unfolded
    FROM flagged f
    WHERE f.was_eligible
  ),
  lanes AS (
    SELECT COALESCE(f.origin_service_id, __EMPTY_UUID__::uuid) AS lane, MIN(f.lane_seq) AS mn, MAX(f.lane_seq) AS mx
    FROM flagged f
    WHERE f.was_eligible
    GROUP BY 1
  )
  SELECT (SELECT COUNT(*)::int FROM flagged CROSS JOIN digest_subtract),
         (SELECT array_agg(DISTINCT f.stream_id) FROM flagged f),
         (SELECT array_agg(l.lane) FROM lanes l WHERE l.mn IS NOT NULL),
         (SELECT array_agg(l.mn) FROM lanes l WHERE l.mn IS NOT NULL),
         (SELECT array_agg(l.mx) FROM lanes l WHERE l.mn IS NOT NULL)
  INTO v_flagged, v_streams, v_lanes, v_mins, v_maxs;

  IF v_flagged = 0 THEN
    RETURN 0;
  END IF;

  -- Drop the buckets the subtraction emptied (an occurrence stream holds nothing else). A separate statement so
  -- it sees the subtraction.
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.stream_id = ANY(v_streams) AND d.event_count <= 0;

  -- The sealed epochs that counted the flagged rows refold without them (_wh_epoch_buckets above reads the
  -- flag), and the generation moves once so consumers reset their seals instead of alarming on the change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN v_flagged;
END;
$$ LANGUAGE plpgsql;

-- ── _wh_reclassify_ephemeral_events: last word 453, copied whole; the eligibility rule is version 2 ──
-- <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
-- <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
-- <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
-- <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_AMixedStream_IsReportedAndLeftUntouched_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifySqlTests.cs:Reclassify_MixedStream_SkipsAndReportsBlocked_ButReclassifiesCleanStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_StampsTheBlockingEvents_OnlyInStreamsHoldingTheEphemeralType_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:NoRelease_NoDurableEventChangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_AStreamStillHeldByAnUnreleasedType_IsLeftWhole_AndReportedForThatTypeOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Reclassify_AnAllowedPair_ReclassifiesTheEphemeralEvents_KeepsTheDurable_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:ReclassifyEphemeral_SubtractsEveryLaneOfTheReclassifiedEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:OneWalk_ConvertsEveryDeclaredEphemeralTypeInTheStream_Together_AndTheDigestsEqualARecountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:AnAllowedDurableType_PairedWithEveryDeclaredTypeInTheStream_StaysDurable_AndTheRestConvertAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_reclassify_ephemeral_events(
  p_names TEXT[],
  p_event_ids UUID[],
  p_released TEXT[] DEFAULT NULL,
  p_allowed TEXT[] DEFAULT NULL,
  p_declared TEXT[] DEFAULT NULL,
  p_rule_owner TEXT[] DEFAULT NULL,
  p_rule_kind TEXT[] DEFAULT NULL,
  p_rule_type TEXT[] DEFAULT NULL
)
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  events_blocked BIGINT,
  streams_blocked BIGINT,
  events_released BIGINT,
  events_declared BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_streams UUID[];
  v_refused UUID[];
  v_stream_ids UUID[];
  v_types TEXT[];
  v_verdicts TEXT[];
  v_ids UUID[];
  v_events BIGINT := 0;
  v_streams_done BIGINT := 0;
  v_released_events BIGINT := 0;
  v_declared_events BIGINT := 0;
  v_blocked_events BIGINT := 0;
  v_blocked_streams BIGINT := 0;
  v_touched UUID[];
  v_touched_types TEXT[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
BEGIN
  IF cardinality(COALESCE(p_names, ARRAY[]::TEXT[])) = 0 OR cardinality(COALESCE(p_event_ids, ARRAY[]::UUID[])) = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  -- The streams of the given Sourced events of the names: the candidates.
  SELECT array_agg(DISTINCT es.stream_id) INTO v_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    -- not the digest rule: the events still Sourced
    AND (es.flags & c_flag_ephemeral) = 0;

  -- 397 (#2278): each candidate stream judged once, by every Sourced type it holds. A stream holding a blocking type is
  -- refused whole; in every other stream the walked, declared and released types are stamped, and the allowed stay.
  SELECT COALESCE(array_agg(DISTINCT v.stream_id) FILTER (WHERE v.verdict = 'blocking'), ARRAY[]::UUID[]),
         array_agg(v.stream_id) FILTER (WHERE v.verdict IN ('walked', 'declared', 'released')),
         array_agg(v.event_type) FILTER (WHERE v.verdict IN ('walked', 'declared', 'released')),
         array_agg(v.verdict) FILTER (WHERE v.verdict IN ('walked', 'declared', 'released'))
  INTO v_refused, v_stream_ids, v_types, v_verdicts
  FROM __SCHEMA__._wh_ephemeral_stream_verdicts(
    v_streams, p_names, p_released, p_allowed, p_declared, p_rule_owner, p_rule_kind, p_rule_type) v;

  -- The given Sourced events whose stream would become MIXED, left Sourced and reported here.
  SELECT COUNT(*), COUNT(DISTINCT es.stream_id) INTO v_blocked_events, v_blocked_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.event_type = ANY(p_names)
    -- not the digest rule: the events still Sourced
    AND (es.flags & c_flag_ephemeral) = 0
    AND es.stream_id = ANY(v_refused);

  -- Every Sourced event of a stamped type in the streams that convert: the whole stream, never part of it.
  SELECT array_agg(es.event_id) INTO v_ids
  FROM unnest(v_stream_ids, v_types) AS c(sid, event_type)
  JOIN __SCHEMA__.wh_event_store es ON es.stream_id = c.sid AND es.event_type = c.event_type
  WHERE NOT (c.sid = ANY(v_refused))
    -- not the digest rule: the events still Sourced
    AND (es.flags & c_flag_ephemeral) = 0;
  v_ids := COALESCE(v_ids, ARRAY[]::UUID[]);

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows the stamp below will remove
  -- from the fold, evaluated BEFORE it.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, c_zero) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_ids)
      AND __DIGEST_ELIGIBLE_V2__(es)
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Full split (078): bodies live in wh_event_body from birth for both classes, so reclassification is a flags stamp.
  WITH reclassified AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = es.flags | c_flag_ephemeral
    WHERE es.event_id = ANY(v_ids)
      -- not the digest rule: the events still Sourced
      AND (es.flags & c_flag_ephemeral) = 0
    RETURNING es.event_id, es.stream_id, es.event_type, es.scope, es.origin_service_id, es.flags
  ),
  -- Migration 087 (A1c): the reclassified rows leave the audited digest set (flags & 8 excluded), so they are XORed
  -- back out of wh_stream_digests. 336 (#2106): every lane leaves through the one fold helper; read by the final
  -- SELECT, so it always runs.
  digest_subtract AS (
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(r.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(r.scope ->> 't', '')),
      array_agg(r.event_type), array_agg(r.stream_id), array_agg(r.event_id), 'unfold', NOW()) AS unfolded
    FROM reclassified r
    -- 453 (#2352): a reclassified row was Sourced before the stamp, so it counted exactly when the rest of the rule held:
    -- not at most once (214, #1484) and not exempt. Its flags now carry the ephemeral bit, so the digest rule would
    -- read every one as never folded.
    WHERE __INTEGRITY_COUNTED_V2__(r)
  ),
  judged AS (
    -- Each stamped row with its stream's verdict on its type (one verdict per stream and type, so one row each).
    SELECT r.stream_id, r.event_type, x.verdict
    FROM reclassified r
    JOIN unnest(v_stream_ids, v_types, v_verdicts) AS x(sid, event_type, verdict)
      ON x.sid = r.stream_id AND x.event_type = r.event_type
  )
  SELECT COUNT(*) FILTER (WHERE j.verdict = 'walked'),
         COUNT(DISTINCT j.stream_id) FILTER (WHERE j.verdict = 'walked'),
         COUNT(*) FILTER (WHERE j.verdict = 'released'),
         COUNT(*) FILTER (WHERE j.verdict = 'declared'),
         array_agg(DISTINCT j.stream_id),
         array_agg(DISTINCT j.event_type)
  INTO v_events, v_streams_done, v_released_events, v_declared_events, v_touched, v_touched_types
  FROM judged j CROSS JOIN digest_subtract;

  -- Migration 087: drop the buckets the subtraction emptied, in the streams it touched (a separate statement, so it sees
  -- the subtraction).
  IF v_touched IS NOT NULL THEN
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_touched) AND d.event_type = ANY(v_touched_types) AND d.event_count <= 0;
  END IF;

  -- Migration 093 (#80-F): refold the sealed epochs the reclassification touched, then bump the generation once.
  IF v_lanes IS NOT NULL AND v_events + v_released_events + v_declared_events > 0 THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT v_events, v_streams_done, v_blocked_events, v_blocked_streams, v_released_events, v_declared_events;
END;
$$ LANGUAGE plpgsql;

-- ── wh_restore_archived_stream: last word 460, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:ArchiveWhole_KeepsTheBucketCarried_AndARestoreTakesItBack_NothingBumpedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Restore_OfAStreamArchivedBeforeCutsCarried_FoldsItBackAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restore_archived_stream(p_stream_id UUID)
RETURNS TABLE(restore_status TEXT, events_restored BIGINT, origin_generation BIGINT) AS $$
DECLARE
  v_legacy BOOLEAN;   -- 460: restoring a stream archived before cuts carried
  v_generation BIGINT;
  v_ids UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_bytes BIGINT;
  v_hot_bytes BIGINT;
  v_type TEXT;
  v_tenant TEXT;
  v_first_at TIMESTAMPTZ;
  v_bucket TIMESTAMPTZ;
  v_last BIGINT;
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;
  c_flags_off_the_ledger CONSTANT INTEGER := 24;  -- ephemeral (8) or a compaction origin (16): no ledger row (260)
BEGIN
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id) THEN
    -- 271 (#1738): a stream that is not archived any more (a concurrent restore put it back first) completes its restore
    -- request too, so a second restore of one stream ends the same way as the first. A tombstone whose archive was
    -- deleted is still Archived: nothing completes, and the caller reports the request Failed.
    IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = p_stream_id AND l.state = 'Archived') THEN
      PERFORM __SCHEMA__._wh_complete_archive_restore(p_stream_id);
    END IF;
    RETURN QUERY SELECT 'nothing_archived'::TEXT, 0::BIGINT, v_generation;
    RETURN;
  END IF;

  -- Two events at one version would break every reader of the stream: refuse and move nothing.
  IF EXISTS (
    SELECT 1
    FROM __SCHEMA__.wh_event_archive a
    JOIN __SCHEMA__.wh_event_store es ON es.stream_id = a.stream_id AND es.version = a.version AND es.event_id <> a.event_id
    WHERE a.stream_id = p_stream_id) THEN
    RETURN QUERY SELECT 'version_conflict'::TEXT, 0::BIGINT, v_generation;
    RETURN;
  END IF;

  -- Active again before the events go back. 266 (#1733): Sealed instead
  -- when the stream was sealed, so a restore never reopens a stream that ended.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET state = CASE WHEN l.state = 'Archived' THEN (CASE WHEN l.sealed_at IS NOT NULL THEN 'Sealed' ELSE 'Active' END)
                   ELSE l.state END,
      archive_locator = NULL
  WHERE l.stream_id = p_stream_id;

  -- Each event at its original version and position: a restored row with its commit sequence is not stamped again,
  -- so it is not read as new by anything that follows the lanes.
  WITH restored AS (
    INSERT INTO __SCHEMA__.wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at,
       flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order)
    SELECT a.event_id, a.stream_id, a.aggregate_id, a.aggregate_type, a.event_type, a.scope, a.version,
           COALESCE(a.created_at, a.archived_at), COALESCE(a.flags, 0), a.commit_sequence, a.origin_service_id,
           a.origin_commit_sequence, COALESCE(a.insert_order, nextval('__SCHEMA__.wh_event_insert_order_seq'))
    FROM __SCHEMA__.wh_event_archive a
    WHERE a.stream_id = p_stream_id
    ORDER BY a.version
    ON CONFLICT DO NOTHING
    RETURNING event_id)
  SELECT COALESCE(array_agg(r.event_id), ARRAY[]::UUID[]) INTO v_ids FROM restored r;

  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  SELECT a.event_id, a.event_data, a.metadata
  FROM __SCHEMA__.wh_event_archive a
  WHERE a.stream_id = p_stream_id AND a.event_id = ANY(v_ids)
    AND a.event_data IS NOT NULL AND a.metadata IS NOT NULL
  ON CONFLICT DO NOTHING;

  -- 460 (#2363): the restored events are taken back out of what their archive carried, so every bucket's digest and every
  -- seal stays as it was. A stream archived before 460 carried nothing: its events fold back in, its covering epochs
  -- refold and the generation is bumped below, as before.
  v_legacy := NOT __SCHEMA__._wh_uncarry_restored(v_ids, NOW());
  IF v_legacy THEN
    -- Fold the restored rows back in with the chain's own predicates.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
      array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'fold', NOW())
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_ids)
      AND __DIGEST_ELIGIBLE_V2__(es);

    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(v_ids)
        AND __DIGEST_ELIGIBLE_V2__(es)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;

    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
    END IF;
  END IF;

  -- The parked cursors return as they were; a cursor a perspective has made since is kept.
  INSERT INTO __SCHEMA__.wh_perspective_cursors
  SELECT (jsonb_populate_record(NULL::__SCHEMA__.wh_perspective_cursors, p.cursor_row)).*
  FROM __SCHEMA__.wh_event_archive_cursors p
  WHERE p.stream_id = p_stream_id
  ON CONFLICT DO NOTHING;

  DELETE FROM __SCHEMA__.wh_event_archive_cursors p WHERE p.stream_id = p_stream_id;
  -- 299 (#1739): the perspective rows the archive moved out of their tables come back with the events.
  PERFORM __SCHEMA__._wh_restore_archived_stream_rows(p_stream_id);
  DELETE FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id;

  IF v_legacy THEN
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  -- The ledger row ends Active and counts the bytes that came back. A stream with no row gets one, by 260's rules.
  SELECT COALESCE(SUM(b.sz) FILTER (WHERE b.restored), 0), COALESCE(SUM(b.sz), 0) INTO v_bytes, v_hot_bytes
  FROM (
    SELECT es.event_id = ANY(v_ids) AS restored,
           COALESCE(pg_column_size(eb.event_data), 0) + COALESCE(pg_column_size(eb.metadata), 0) AS sz
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
  ) b;
  SELECT es.aggregate_type, es.scope ->> 't', es.created_at INTO v_type, v_tenant, v_first_at
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
  ORDER BY (COALESCE(es.flags, 0) & c_flags_off_the_ledger <> 0), es.version
  LIMIT 1;
  SELECT MAX(es.version), date_trunc('hour', MAX(es.created_at), 'UTC') INTO v_last, v_bucket
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;

  INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
    (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
     archive_locator, origin_generation)
  VALUES (p_stream_id, v_type, v_tenant, v_first_at, v_bucket, v_last, v_hot_bytes, 'Active', NULL, v_generation)
  ON CONFLICT (stream_id) DO UPDATE SET
    state = CASE WHEN l.state = 'Archived' THEN (CASE WHEN l.sealed_at IS NOT NULL THEN 'Sealed' ELSE 'Active' END)
                 ELSE l.state END,
    last_version = GREATEST(l.last_version, EXCLUDED.last_version),
    last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
    approx_bytes = l.approx_bytes + v_bytes,

    archive_locator = NULL,
    origin_generation = EXCLUDED.origin_generation;

  -- 271 (#1738): whoever asked for this restore, the stream's restore request is Restored, its AwaitingRestore dead
  -- letters are due for the recovery worker, and its snapshots no longer read through an archive.
  PERFORM __SCHEMA__._wh_complete_archive_restore(p_stream_id);

  RETURN QUERY SELECT 'restored'::TEXT, cardinality(v_ids)::BIGINT, v_generation;
END;
$$ LANGUAGE plpgsql;

-- ── _wh_carry_cut: last word 461, copied whole; the eligibility rule is version 2 ──
-- Carries the events of the streams (at or below p_through_version; NULL: whole streams) that the digest counts, which
-- the caller is about to remove: their contribution moves into their buckets' carried part (the bucket's digest stays;
-- a missing bucket is written with the contribution as both) and into the removed row of the epoch each sat in.
-- Returns the buckets it carried into. Call it while the rows are still stored.
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Truncate_CarriesWhatItRemoves_TheDigestStaysAndNothingIsRefoldedOrBumpedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:ArchiveWhole_KeepsTheBucketCarried_AndARestoreTakesItBack_NothingBumpedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Purge_KeepsTheBucketCarried_AndARedeliveryOfThePurgedStreamIsRefusedAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_carry_cut(p_stream_ids UUID[], p_through_version BIGINT, p_now TIMESTAMPTZ)
RETURNS INTEGER AS $$
DECLARE
  v_rows INTEGER := 0;
  v_epochs INTEGER := 0;   -- the removed rows written; read so both inserts run before the function returns
BEGIN
  WITH ev AS (
    -- 461 (#2364): a superseding key counts as its slot, once, at its first copy's place. A cut carries it only when it
    -- removes every copy of the key; when a newer copy stays, the slot stays counted and its place is handed on below.
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane, COALESCE(es.scope ->> 't', '') AS tenant,
           CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS ty, es.stream_id AS s, CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END AS event_id,
           COALESCE(es.supersede_slot_at,
                    CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS pos
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = ANY(p_stream_ids)
      AND (p_through_version IS NULL OR es.version <= p_through_version)
      AND __DIGEST_ELIGIBLE_V2__(es)
      AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      AND (es.supersede_key IS NULL OR p_through_version IS NULL OR NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store o
        WHERE o.stream_id = es.stream_id
          AND COALESCE(o.supersede_group, o.event_type) = COALESCE(es.supersede_group, es.event_type)
          AND o.supersede_key = es.supersede_key
          AND o.version > p_through_version))
  ),
  removed AS (
    INSERT INTO __SCHEMA__.wh_digest_epoch_removed AS r
      (origin_service_id, scope_tenant, event_type, epoch_id, removed_lo, removed_hi, removed_count, updated_at)
    SELECT e.lane, e.tenant, e.ty,
           CASE WHEN e.pos IS NULL THEN -1 ELSE e.pos / __SCHEMA__._wh_epoch_width(e.lane) END,
           bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)), bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)),
           COUNT(*)::int, p_now
    FROM ev e
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, epoch_id) DO UPDATE SET
      removed_lo = r.removed_lo # EXCLUDED.removed_lo,
      removed_hi = r.removed_hi # EXCLUDED.removed_hi,
      removed_count = r.removed_count + EXCLUDED.removed_count,
      updated_at = EXCLUDED.updated_at
    RETURNING 1
  ),
  carried AS (
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at, id_lo, id_hi,
       carried_lo, carried_hi, carried_count, carried_id_lo, carried_id_hi, carried_at)
    SELECT b.lane, b.tenant, b.ty, b.s, b.lo, b.hi, b.n, p_now, b.ilo, b.ihi, b.lo, b.hi, b.n, b.ilo, b.ihi, p_now
    FROM (
      SELECT e.lane, e.tenant, e.ty, e.s,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 2)) AS ilo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 3)) AS ihi,
             COUNT(*)::int AS n
      FROM ev e
      GROUP BY 1, 2, 3, 4
    ) b
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      carried_lo = d.carried_lo # EXCLUDED.carried_lo,
      carried_hi = d.carried_hi # EXCLUDED.carried_hi,
      carried_count = d.carried_count + EXCLUDED.carried_count,
      carried_id_lo = d.carried_id_lo # EXCLUDED.carried_id_lo,
      carried_id_hi = d.carried_id_hi # EXCLUDED.carried_id_hi,
      carried_at = EXCLUDED.carried_at
    RETURNING 1
  )
  SELECT (SELECT COUNT(*) FROM carried)::int, (SELECT COUNT(*) FROM removed)::int INTO v_rows, v_epochs;

  -- 461 (#2364): a prefix that removes a key's anchor while a newer copy stays hands the anchor's place on to it.
  IF p_through_version IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_hand_on_slots(ARRAY(
      SELECT es.event_id FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = ANY(p_stream_ids) AND es.version <= p_through_version AND es.supersede_key IS NOT NULL));
  END IF;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

-- ── _wh_uncarry_restored: last word 461, copied whole; the eligibility rule is version 2 ──
-- Takes restored events back out of what was carried for them: out of their buckets' carried part and their epochs'
-- removed rows. Only when every bucket they belong to carries at least as many events as come back; otherwise (a stream
-- archived before 460, which carried nothing) it changes nothing and returns false, and the caller folds them in.
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:ArchiveWhole_KeepsTheBucketCarried_AndARestoreTakesItBack_NothingBumpedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Restore_OfAStreamArchivedBeforeCutsCarried_FoldsItBackAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_uncarry_restored(p_event_ids UUID[], p_now TIMESTAMPTZ)
RETURNS BOOLEAN AS $$
DECLARE
  v_short BOOLEAN;
  v_buckets INTEGER;   -- the buckets and removed rows updated; read so both updates run
  v_epochs INTEGER;
BEGIN
  IF COALESCE(cardinality(p_event_ids), 0) = 0 THEN
    RETURN TRUE;
  END IF;

  SELECT EXISTS (
    SELECT 1
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane, COALESCE(es.scope ->> 't', '') AS tenant,
             CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS ty, es.stream_id AS s, COUNT(*)::int AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(p_event_ids) AND __DIGEST_ELIGIBLE_V2__(es) AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      GROUP BY 1, 2, 3, 4
    ) b
    LEFT JOIN __SCHEMA__.wh_stream_digests d
      ON d.origin_service_id = b.lane AND d.scope_tenant = b.tenant AND d.event_type = b.ty AND d.stream_id = b.s
    WHERE d.carried_count IS NULL OR d.carried_count < b.n) INTO v_short;
  IF v_short THEN
    RETURN FALSE;
  END IF;

  WITH ev AS (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane, COALESCE(es.scope ->> 't', '') AS tenant,
           CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS ty, es.stream_id AS s, CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END AS event_id,
           COALESCE(es.supersede_slot_at,
                    CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS pos
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids) AND __DIGEST_ELIGIBLE_V2__(es) AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
  ),
  buckets AS (
    UPDATE __SCHEMA__.wh_stream_digests d
    SET carried_lo = d.carried_lo # b.lo, carried_hi = d.carried_hi # b.hi, carried_count = d.carried_count - b.n,
        carried_id_lo = d.carried_id_lo # b.ilo, carried_id_hi = d.carried_id_hi # b.ihi
    FROM (
      SELECT e.lane, e.tenant, e.ty, e.s,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 2)) AS ilo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 3)) AS ihi,
             COUNT(*)::int AS n
      FROM ev e GROUP BY 1, 2, 3, 4
    ) b
    WHERE d.origin_service_id = b.lane AND d.scope_tenant = b.tenant AND d.event_type = b.ty AND d.stream_id = b.s
    RETURNING 1
  ),
  epochs AS (
    UPDATE __SCHEMA__.wh_digest_epoch_removed r
    SET removed_lo = r.removed_lo # x.lo, removed_hi = r.removed_hi # x.hi, removed_count = r.removed_count - x.n,
        updated_at = p_now
    FROM (
      SELECT e.lane, e.tenant, e.ty,
             CASE WHEN e.pos IS NULL THEN -1 ELSE e.pos / __SCHEMA__._wh_epoch_width(e.lane) END AS epoch,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)) AS hi,
             COUNT(*)::int AS n
      FROM ev e GROUP BY 1, 2, 3, 4
    ) x
    WHERE r.origin_service_id = x.lane AND r.scope_tenant = x.tenant AND r.event_type = x.ty AND r.epoch_id = x.epoch
    RETURNING 1
  )
  SELECT (SELECT COUNT(*) FROM buckets)::int, (SELECT COUNT(*) FROM epochs)::int INTO v_buckets, v_epochs;

  DELETE FROM __SCHEMA__.wh_digest_epoch_removed r
  WHERE r.removed_count <= 0 AND r.removed_lo = 0 AND r.removed_hi = 0;
  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

-- ── _wh_heal_stream_digests_statement: last word 461, copied whole; the eligibility rule is version 2 ──
-- The heal statement for a named batch (p_named) or the whole store, with $1 the streams, $2 the settle window and
-- $3 whether to fill id lanes never computed. One text, planned and run as written.
-- <docs>resilience/stream-integrity#the-heal-is-linear-in-its-batch</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestHealPlanSqlTests.cs:TheHealStatement_NeverRescansASetPerRow_ForANamedBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestHealPlanSqlTests.cs:TheHealStatement_NeverRescansASetPerRow_ForTheWholeStoreAsync</tests>
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Heal_FindsACarriedBucketTrue_AndStillFindsAnUnaccountedLossAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Heal_ABucketWhoseWholeStreamWasCut_IsKept_NotReportedAsAPhantomAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:AfterAReap_TheHealAndTheSealRecheckFindNoDrift_AndStillFindALossAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_heal_stream_digests_statement(p_named BOOLEAN)
RETURNS TEXT AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- The stream filter, as an index condition when streams are named; the whole store for the sweep.
  v_events TEXT := CASE WHEN p_named THEN 'es.stream_id = ANY($1)' ELSE 'TRUE' END;
  v_digests TEXT := CASE WHEN p_named THEN 'd.stream_id = ANY($1)' ELSE 'TRUE' END;
BEGIN
  RETURN format($q$
    WITH recomputed AS (
      -- 461 (#2364): a superseding key counts as its slot, once (its anchor), in its first copy's type bucket.
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS o, COALESCE(es.scope ->> 't', '') AS t,
             CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS ty, es.stream_id AS s,
             bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 2)) AS ilo,
             bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 3)) AS ihi,
             COUNT(*)::int AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE %1$s
        AND __DIGEST_ELIGIBLE_V2__(es)
        AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
        AND es.created_at < NOW() - $2
      GROUP BY 1, 2, 3, 4
    ),
    stored AS (
      SELECT d.origin_service_id AS o, d.scope_tenant AS t, d.event_type AS ty, d.stream_id AS s,
             d.digest_lo, d.digest_hi, d.event_count, d.id_lo, d.id_hi,
             d.carried_lo, d.carried_hi, d.carried_count, d.carried_id_lo, d.carried_id_hi,
             (d.carried_count <> 0 OR d.carried_lo <> 0 OR d.carried_hi <> 0) AS carries
      FROM __SCHEMA__.wh_stream_digests d
      -- 460 (#2363): a bucket whose carried detail rolled off cannot be recomputed from its events: left alone.
      WHERE %2$s AND d.updated_at < NOW() - $2 AND d.carried_lo IS NOT NULL
    ),
    -- One comparison of the two sets, as a FULL JOIN: PostgreSQL runs a full join only as a hash or merge join, never
    -- as a nested loop, so its cost stays linear whatever the planner believes about either set (#2337). A bucket on
    -- both sides is compared; one only stored has no events behind it; one only recomputed has no bucket yet.
    -- 460 (#2363): what the stored digest should be is the live fold plus the carried part, so a bucket a cut carried
    -- is compared with both, and one whose whole stream was cut (no live events, a carried part) is no phantom.
    expected AS (
      SELECT st.o, st.t, st.ty, st.s, st.digest_lo, st.digest_hi, st.event_count, st.id_lo, st.id_hi,
             COALESCE(r.lo, 0) # st.carried_lo AS lo, COALESCE(r.hi, 0) # st.carried_hi AS hi,
             COALESCE(r.ilo, 0) # st.carried_id_lo AS ilo, COALESCE(r.ihi, 0) # st.carried_id_hi AS ihi,
             COALESCE(r.n, 0) + st.carried_count AS n,
             r.s IS NULL AND NOT st.carries AS stored_only,
             (st.s IS NOT NULL AND (r.s IS NOT NULL OR st.carries)) AS on_both
      FROM stored st
      FULL JOIN recomputed r ON r.o = st.o AND r.t = st.t AND r.ty = st.ty AND r.s = st.s
    ),
    compared AS (
      SELECT e.o, e.t, e.ty, e.s, e.digest_lo, e.digest_hi, e.event_count, e.lo, e.hi, e.ilo, e.ihi, e.n,
             e.stored_only, e.on_both,
             (e.digest_lo <> e.lo OR e.digest_hi <> e.hi OR e.event_count <> e.n
              OR e.id_lo <> e.ilo OR e.id_hi <> e.ihi) AS drifted,
             (e.id_lo IS NULL OR e.id_hi IS NULL) AS unfilled
      FROM expected e
    ),
    rewrite AS (
      SELECT c.*, CASE WHEN c.drifted THEN 'drift_updated' ELSE 'filled' END AS why
      FROM compared c
      WHERE c.on_both AND (c.drifted OR ($3 AND c.unfilled))
    ),
    updated AS (
      UPDATE __SCHEMA__.wh_stream_digests d
      SET digest_lo = w.lo, digest_hi = w.hi, id_lo = w.ilo, id_hi = w.ihi, event_count = w.n, updated_at = NOW()
      FROM rewrite w
      WHERE d.origin_service_id = w.o AND d.scope_tenant = w.t AND d.event_type = w.ty AND d.stream_id = w.s
      RETURNING 1
    ),
    removed AS (
      DELETE FROM __SCHEMA__.wh_stream_digests d
      USING compared c
      WHERE c.stored_only
        AND d.origin_service_id = c.o AND d.scope_tenant = c.t AND d.event_type = c.ty AND d.stream_id = c.s
      RETURNING d.origin_service_id, d.scope_tenant, d.event_type, d.stream_id, d.digest_lo, d.digest_hi, d.event_count
    ),
    added AS (
      INSERT INTO __SCHEMA__.wh_stream_digests
        (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, id_lo, id_hi)
      SELECT r.o, r.t, r.ty, r.s, r.lo, r.hi, r.n, r.ilo, r.ihi
      FROM recomputed r
      WHERE NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_stream_digests d
        WHERE d.origin_service_id = r.o AND d.scope_tenant = r.t AND d.event_type = r.ty AND d.stream_id = r.s)
      ORDER BY 1, 2, 3, 4
      ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO NOTHING
      RETURNING origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count
    )
    SELECT w.why, w.o, w.t, w.ty, w.s, w.digest_lo, w.digest_hi, w.event_count, w.lo, w.hi, w.n
    FROM rewrite w
    UNION ALL
    SELECT 'drift_removed', x.origin_service_id, x.scope_tenant, x.event_type, x.stream_id,
           x.digest_lo, x.digest_hi, x.event_count, NULL::bigint, NULL::bigint, NULL::int
    FROM removed x
    UNION ALL
    SELECT 'drift_added', a.origin_service_id, a.scope_tenant, a.event_type, a.stream_id,
           NULL::bigint, NULL::bigint, NULL::int, a.digest_lo, a.digest_hi, a.event_count
    FROM added a
    $q$, v_events, v_digests);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- ── _wh_epoch_buckets: last word 461, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#what-is-digested</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:EpochRecheck_AfterACut_FindsNoDrift_AndStillFindsADamagedSealAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Refold_OfAnEpochACutTouched_KeepsTheRemovedPartInTheSealAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:AfterAReap_TheHealAndTheSealRecheckFindNoDrift_AndStillFindALossAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_epoch_buckets(
  p_lane  UUID,
  p_epoch BIGINT,
  p_width BIGINT
) RETURNS TABLE (b_tenant TEXT, b_type TEXT, b_lo BIGINT, b_hi BIGINT, b_cnt INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_from  BIGINT := p_epoch * p_width;
  v_to    BIGINT := (p_epoch + 1) * p_width;   -- exclusive
BEGIN
  IF p_lane = c_zero THEN
    RETURN QUERY
    SELECT u.t, u.ty, bit_xor(u.lo), bit_xor(u.hi), SUM(u.n)::int
    FROM (
      -- 461 (#2364): a superseding key counts as its slot, once (its anchor), where its first copy sat: a row that
      -- inherited a place (supersede_slot_at) counts there, read through idx_event_store_slot_at, and nowhere else.
      SELECT COALESCE(es.scope ->> 't', '') AS t, (CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END)::text AS ty,
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 0) AS lo,
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 1) AS hi,
             1 AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id IS NULL
        AND es.commit_sequence >= v_from AND es.commit_sequence < v_to
        AND es.supersede_slot_at IS NULL
        AND __DIGEST_ELIGIBLE_V2__(es)  -- 216 (#1492)
        AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      UNION ALL
      SELECT COALESCE(es.scope ->> 't', ''), (CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END)::text,
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 0),
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 1),
             1
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id IS NULL
        AND es.supersede_slot_at >= v_from AND es.supersede_slot_at < v_to
        AND __DIGEST_ELIGIBLE_V2__(es)
        AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      UNION ALL
      -- 460 (#2363): what a cut removed from this epoch still counts in its fold.
      SELECT r.scope_tenant, r.event_type, r.removed_lo, r.removed_hi, r.removed_count
      FROM __SCHEMA__.wh_digest_epoch_removed r
      WHERE r.origin_service_id = p_lane AND r.epoch_id = p_epoch
    ) u
    GROUP BY 1, 2;
  ELSE
    RETURN QUERY
    SELECT u.t, u.ty, bit_xor(u.lo), bit_xor(u.hi), SUM(u.n)::int
    FROM (
      -- 461 (#2364): a superseding key counts as its slot, once (its anchor), where its first copy sat: a row that
      -- inherited a place (supersede_slot_at) counts there, read through idx_event_store_slot_at, and nowhere else.
      SELECT COALESCE(es.scope ->> 't', '') AS t, (CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END)::text AS ty,
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 0) AS lo,
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 1) AS hi,
             1 AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id = p_lane
        AND es.origin_commit_sequence >= v_from AND es.origin_commit_sequence < v_to
        AND es.supersede_slot_at IS NULL
        AND __DIGEST_ELIGIBLE_V2__(es)  -- 216 (#1492)
        AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      UNION ALL
      SELECT COALESCE(es.scope ->> 't', ''), (CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END)::text,
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 0),
             __SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 1),
             1
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id = p_lane
        AND es.supersede_slot_at >= v_from AND es.supersede_slot_at < v_to
        AND __DIGEST_ELIGIBLE_V2__(es)
        AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      UNION ALL
      -- 460 (#2363): what a cut removed from this epoch still counts in its fold.
      SELECT r.scope_tenant, r.event_type, r.removed_lo, r.removed_hi, r.removed_count
      FROM __SCHEMA__.wh_digest_epoch_removed r
      WHERE r.origin_service_id = p_lane AND r.epoch_id = p_epoch
    ) u
    GROUP BY 1, 2;
  END IF;
END;
$$;

-- ── compute_type_digests_epoch: last word 461, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#unstamped-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:LocalLane_AnUnstampedRow_IsLeftOutOfTheOpenWindowUntilItIsStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:ReceivedLane_ARowWithNoOriginSequence_StillFoldsInTheOpenWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:EpochComposition_EqualsTheFullFold_AcrossClosedAndOpenRangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:TypeAnswer_IsTheSameBeforeAndAfterACut_SealedOrOpenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Reap_DeletesOlderCopies_AndLeavesTheDigestTheSealsAndTheGenerationAsTheyWereAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch(
  p_origin_lane    UUID,          -- NULL = the local lane (this service's own emissions)
  p_event_types    TEXT[],        -- NULL = all types
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_lane      UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier  BIGINT := -1;
  v_width     BIGINT;
  v_open_from BIGINT := 0;
  v_types     TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_frontier := -1;      -- nothing sealed: the whole lane folds live below
    v_open_from := 0;
  ELSE
    v_open_from := (v_frontier + 1) * v_width;
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id <= v_frontier
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  open_fold AS (
    -- The live remainder. #1791: in the local lane only stamped rows fold. A row the stamper has not yet
    -- sequenced is not settled here: the windowed answer, integrity_settled_max and the stamp fence all wait for its
    -- sequence, and once it has one it folds where that sequence puts it, so it never falls through the crack.
    -- A received row keeps the NULL-sequence branch: it can lack the origin's sequence for good (published
    -- before its stamp, or received before 087), and the open window is the only place it can ever fold.
    -- 461 (#2364): a superseding key counts as its slot, once, at the place its first copy sat.
    SELECT COALESCE(es.scope ->> 't', '') AS b_tenant, CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS b_type,
           bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 0)) AS lo,
           bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
      AND (CASE WHEN p_origin_lane IS NULL
                THEN COALESCE(es.supersede_slot_at, es.commit_sequence) IS NOT NULL
                     AND COALESCE(es.supersede_slot_at, es.commit_sequence) >= v_open_from
                ELSE COALESCE(es.supersede_slot_at, es.origin_commit_sequence) IS NULL
                     OR COALESCE(es.supersede_slot_at, es.origin_commit_sequence) >= v_open_from
           END)
      AND (v_types IS NULL OR CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END = ANY(v_types))
      AND __DIGEST_ELIGIBLE_V2__(es)  -- 216 (#1492)
      AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  ),
  open_removed AS (
    -- 460 (#2363): what a cut removed above the frontier (or with no position) still counts in the open window.
    SELECT r.scope_tenant AS b_tenant, r.event_type AS b_type,
           bit_xor(r.removed_lo) AS lo, bit_xor(r.removed_hi) AS hi, SUM(r.removed_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epoch_removed r
    WHERE r.origin_service_id = v_lane
      AND (r.epoch_id > v_frontier OR r.epoch_id = -1)
      AND (v_types IS NULL OR r.event_type = ANY(v_types))
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM open_fold UNION ALL SELECT * FROM open_removed) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

-- ── compute_type_digests_epoch_window: last word 461, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#windowed-manifests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestLaneReadTests.cs:TypeDigestWindow_ReadsTheWindow_NotTheLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Reap_DeletesOlderCopies_AndLeavesTheDigestTheSealsAndTheGenerationAsTheyWereAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch_window(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_event_types    TEXT[],        -- NULL = all types
  p_since          BIGINT,        -- inclusive
  p_until          BIGINT,        -- exclusive, pre-clamped at the settled max
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_lane     UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier BIGINT := -1;
  v_width    BIGINT;
  v_e_lo     BIGINT;
  v_e_hi     BIGINT;
  v_types    TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_e_lo := 0;
    v_e_hi := -1;   -- no sealed epochs: the whole window folds live
  ELSE
    -- Sealed epochs fully inside [p_since, p_until): first with start >= since, last whose
    -- exclusive end fits, both clamped to the frontier. An empty run (e_lo > e_hi) = all live.
    v_e_lo := (p_since + v_width - 1) / v_width;
    v_e_hi := LEAST(p_until / v_width - 1, v_frontier);
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id BETWEEN v_e_lo AND v_e_hi
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  lane AS (
    -- 332 (#2080): one branch per lane, each with the window as a direct range on its own indexed column, so the read
    -- is the window's rows (idx_event_store_commit_sequence for the local lane, the 155 lane index for an origin's)
    -- instead of the whole lane filtered through a CASE no index can serve. Exactly one branch is live per call.
    -- 461 (#2364): a row that inherited its key's place (supersede_slot_at) is read at that place, through
    -- idx_event_store_slot_at, and not at its own.
    SELECT es.*, es.commit_sequence AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NULL AND es.origin_service_id IS NULL
      AND es.commit_sequence >= p_since AND es.commit_sequence < p_until
      AND es.supersede_slot_at IS NULL
    UNION ALL
    SELECT es.*, es.origin_commit_sequence AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NOT NULL AND es.origin_service_id = p_origin_lane
      AND es.origin_commit_sequence >= p_since AND es.origin_commit_sequence < p_until
      AND es.supersede_slot_at IS NULL
    UNION ALL
    SELECT es.*, es.supersede_slot_at AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL) OR es.origin_service_id = p_origin_lane)
      AND es.supersede_slot_at >= p_since AND es.supersede_slot_at < p_until
  ),
  live_fold AS (
    -- The window minus the sealed run: the low fringe [since, e_lo*width) and the tail
    -- [(e_hi+1)*width, until), or the whole window when no epoch fits (e_lo > e_hi).
    SELECT COALESCE(l.scope ->> 't', '') AS b_tenant, CASE WHEN l.supersede_key IS NULL THEN l.event_type ELSE COALESCE(l.supersede_slot_type, l.event_type) END AS b_type,
           bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN l.supersede_key IS NULL THEN l.event_id ELSE __SCHEMA__._wh_slot_id(l.stream_id, COALESCE(l.supersede_group, l.event_type), l.supersede_key) END, 0)) AS lo,
           bit_xor(__SCHEMA__._wh_digest_lane(CASE WHEN l.supersede_key IS NULL THEN l.event_id ELSE __SCHEMA__._wh_slot_id(l.stream_id, COALESCE(l.supersede_group, l.event_type), l.supersede_key) END, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM lane l
    WHERE l.lane_seq IS NOT NULL
      AND l.lane_seq >= p_since AND l.lane_seq < p_until
      AND (v_e_lo > v_e_hi
           OR l.lane_seq < v_e_lo * v_width
           OR l.lane_seq >= (v_e_hi + 1) * v_width)
      AND (v_types IS NULL OR CASE WHEN l.supersede_key IS NULL THEN l.event_type ELSE COALESCE(l.supersede_slot_type, l.event_type) END = ANY(v_types))
      AND __DIGEST_ELIGIBLE_V2__(l)  -- 216 (#1492)
      AND (l.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(l.event_id, l.stream_id, COALESCE(l.supersede_group, l.event_type), l.supersede_key, l.origin_service_id, l.origin_commit_sequence, l.version))
      AND l.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM live_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

-- ── wh_compute_stream_digests_window: last word 461, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#windowed-manifests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestTypeIndexTests.cs:TypeFilteredWindow_ReadsThroughAnIndexConditionOnTheType_UnderAGenericPlanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestTypeIndexTests.cs:TheRead_PlansEachCallAgainstItsOwnWindow_WhateverTheCallersPlanModeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestLaneReadTests.cs:ManifestStreamWindow_ReadsTheWindow_NotTheLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochWindowedDigestSqlTests.cs:StreamWindow_OnlyEventsInsideTheWindowContributeAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_compute_stream_digests_window(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_event_types    TEXT[],        -- NULL = all types
  p_since          BIGINT,        -- inclusive
  p_until          BIGINT,        -- exclusive, pre-clamped at the settled max
  p_resume_after   UUID,          -- page start: only streams above this id (NULL = from the first)
  p_max_streams    INTEGER,       -- whole streams on this page
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, stream_id UUID, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER,
                id_lo BIGINT, id_hi BIGINT)
LANGUAGE plpgsql STABLE
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_types   TEXT[];
  v_settled TIMESTAMPTZ := p_now - make_interval(secs => p_settle_seconds);
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  RETURN QUERY
  WITH win AS (
    -- One branch per lane (332), each with the window as a direct range on its own indexed column. Exactly one is live
    -- per call; under the custom plan the other is pruned and the type list is a constant the planner can index.
    -- 461 (#2364): a superseding key counts as its slot, once, at its first copy's place and in its type bucket.
    SELECT CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END AS w_event, es.stream_id AS w_stream, (CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END)::text AS w_type,
           COALESCE(es.scope ->> 't', '') AS w_tenant
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NULL AND es.origin_service_id IS NULL
      AND es.commit_sequence >= p_since AND es.commit_sequence < p_until
      AND es.supersede_slot_at IS NULL
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND __DIGEST_ELIGIBLE_V2__(es)
      AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      AND es.created_at < v_settled
      AND (p_resume_after IS NULL OR es.stream_id > p_resume_after)
    UNION ALL
    SELECT CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, es.stream_id, (CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END)::text, COALESCE(es.scope ->> 't', '')
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NOT NULL AND es.origin_service_id = p_origin_lane
      AND es.origin_commit_sequence >= p_since AND es.origin_commit_sequence < p_until
      AND es.supersede_slot_at IS NULL
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND __DIGEST_ELIGIBLE_V2__(es)
      AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      AND es.created_at < v_settled
      AND (p_resume_after IS NULL OR es.stream_id > p_resume_after)
    UNION ALL
    SELECT CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END, es.stream_id, (CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END)::text, COALESCE(es.scope ->> 't', '')
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL) OR es.origin_service_id = p_origin_lane)
      AND es.supersede_slot_at >= p_since AND es.supersede_slot_at < p_until
      -- A row with an inherited place carries a key, so its bucket type is this expression, which the index holds.
      AND (v_types IS NULL OR COALESCE(es.supersede_slot_type, es.event_type) = ANY(v_types))
      AND __DIGEST_ELIGIBLE_V2__(es)
      AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
      AND es.created_at < v_settled
      AND (p_resume_after IS NULL OR es.stream_id > p_resume_after)
  ),
  last_picked AS (
    -- The page's last stream: the p_max_streams-th distinct stream id in order. Every row up to it is on the page.
    SELECT p.w_stream
    FROM (SELECT DISTINCT w.w_stream FROM win w ORDER BY w.w_stream LIMIT p_max_streams) p
    ORDER BY p.w_stream DESC
    LIMIT 1
  )
  SELECT w.w_tenant, w.w_type, w.w_stream,
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 0)),
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 1)),
         COUNT(*)::int,
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 2)),
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 3))
  FROM win w
  WHERE w.w_stream <= (SELECT lp.w_stream FROM last_picked lp)
  GROUP BY 1, 2, 3
  ORDER BY 3, 1, 2;
END;
$$;

-- ── wh_seed_stream_digests_batch: last word 461, copied whole; the eligibility rule is version 2 ──
-- <docs>operations/infrastructure/migrations#batched-regions</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationWorklistBatchTests.cs:TheStreamDigestSeedClaimsEachStreamExactlyOnceAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationWorklistBatchTests.cs:TheStreamDigestSeedKeepsEveryBucketWholeAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:SeedStreamDigestsBatch_FoldsEveryLaneOfTheSeededStreamsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_seed_stream_digests_batch(p_limit INT)
RETURNS BIGINT AS $$
DECLARE
  v_from BIGINT;
  v_to   BIGINT;
  v_rows BIGINT := 0;
BEGIN
  IF to_regclass('__SCHEMA__.wh_stream_digest_seed_todo') IS NULL THEN
    RETURN 0;
  END IF;

  SELECT last_seq INTO v_from FROM __SCHEMA__.wh_stream_digest_seed_cursor WHERE only_row;
  v_from := coalesce(v_from, 0);

  SELECT max(seq) INTO v_to
  FROM (SELECT seq FROM __SCHEMA__.wh_stream_digest_seed_todo
        WHERE seq > v_from ORDER BY seq LIMIT p_limit) s;

  -- Exhausted worklist is the only way the loop ends, independent of how many buckets the last slice
  -- happened to write -- a replay writes none and must still advance to the end.
  IF v_to IS NULL THEN
    RETURN 0;
  END IF;

  -- Identical projection, predicate and grouping to the unbounded form this replaced, restricted to
  -- the slice's streams. Keeping it byte-identical matters: any change here changes stored digests,
  -- which the periodic sweep then reports as corruption.
  -- 336 (#2106): the same projection and predicate, folded through the one helper; 'seed' writes a bucket only
  -- where none exists, as ON CONFLICT DO NOTHING did.
  -- 461 (#2364): a superseding key seeds as its slot, once, in its first copy's type bucket (passed slotted).
  SELECT __SCHEMA__._wh_fold_stream_digests(
    array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)),
    array_agg(COALESCE(es.scope ->> 't', '')), array_agg(CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END), array_agg(es.stream_id),
    array_agg(CASE WHEN es.supersede_key IS NULL THEN es.event_id ELSE __SCHEMA__._wh_slot_id(es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key) END), 'seed', NOW(), TRUE)
  INTO v_rows
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
  WHERE es.stream_id IN (SELECT t.stream_id FROM __SCHEMA__.wh_stream_digest_seed_todo t
                         WHERE t.seq > v_from AND t.seq <= v_to)
    AND __DIGEST_ELIGIBLE_V2__(es)
    AND (es.supersede_key IS NULL OR __SCHEMA__._wh_is_slot_anchor(es.event_id, es.stream_id, COALESCE(es.supersede_group, es.event_type), es.supersede_key, es.origin_service_id, es.origin_commit_sequence, es.version))
    AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1;

  UPDATE __SCHEMA__.wh_stream_digest_seed_cursor SET last_seq = v_to WHERE only_row;

  -- At least one, so the runner keeps going while entries remain: a slice can legitimately write
  -- nothing on a replay, and reporting that would stop the pass with work outstanding.
  RETURN GREATEST(v_rows, 1);
END;
$$ LANGUAGE plpgsql SET enable_seqscan = off;

-- ── wh_append_event: last word 537, copied whole; the eligibility rule is version 2 ──
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:DirectAppend_StampsTheGroup_AndQueuesAcrossTypesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSupersedingKeyTests.cs:AppendAsync_GroupedTypes_SupersedeEachOther_AndTheOlderIsReapedAsync</tests>
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_ThroughTheEventStore_CreatesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EphemeralAndCompactionOriginEvents_CreateNoRow_WhileASourcedEventDoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EventsWithoutAStream_CreateNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ParallelAppendsToOneStream_AllSucceed_WithVersionsZeroToNMinusOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:MixedPathStream_TableDigestEqualsARecomputationOverAllItsEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendMixedStreamGuardTests.cs:DirectAppend_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_DirectAppend_SealsTheStream_AndTheNextAppendThrowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:DirectAppend_ToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs:AppendAsync_TerminalEvent_SealsTheStream_AndTheNextAppendThrowsStreamSealedExceptionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationTests.cs:EnsureWhizbangDatabaseInitialized_ADatabaseAlreadyPast267_StillApplies267_AndANewStreamStartsAtOneAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:DirectAppend_StampsTheKey_AndQueuesTheOlderOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:DirectAppend_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:DirectAppend_AutoRestore_RaisesTheRestoreState_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveAppendPolicyTests.cs:AppendAsync_ToAnArchivedStream_Refuse_ThrowsStreamArchivedExceptionAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DirectAppend_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:EFCoreEventStore_Append_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DirectAppend_KeysTheSlimFormDoesNotReduce_AreKeptAsWrittenAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AppendAsync_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_EveryParkedCursorHasItsSnapshot_StoresAtTheNextVersion_AndTheArchiveBecomesAPrefixAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_AParkedCursorWithoutASnapshotAtIt_FallsBackToAutoRestoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_SealedAndArchived_TheSealStillRefusesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamDispositionTests.cs:AppendAsync_NewGeneration_ContinuesAtTheNextVersion_AndTheCoordinatorResumesFromTheParkedCursorAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:DirectAppend_FoldsEveryLaneAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:DirectAppend_LeavesOutTheMessageIdInEitherDriversForm_AndTheScopeTheColumnHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AppendAsync_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_append_event(
  p_event_id UUID,
  p_stream_id UUID,
  p_aggregate_id UUID,
  p_aggregate_type TEXT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_metadata JSONB,
  p_scope JSONB,
  p_created_at TIMESTAMPTZ,
  -- 228 (#1546): the event's kind bits (EventFlags.Ephemeral, EventFlags.Compacted), stored on the row as the outbox
  -- and inbox copies store them. Defaulted so a caller from before this migration still resolves.
  p_flags INTEGER DEFAULT 0
) RETURNS INTEGER AS $$
DECLARE
  v_version INTEGER;
  v_flags INTEGER;
  v_refusal RECORD;
  v_body_bytes BIGINT;  -- 260: the size of the body row as written, for the lifecycle ledger
  c_flag_ephemeral CONSTANT INTEGER := 8;     -- EventFlags.Ephemeral (1 << 3), as the emit chain tests it
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_key TEXT;                              -- 295: the superseding key this event is stored with
  v_group TEXT;                            -- 352: the superseding group it is stored with (#2124)
  v_ends_stream BOOLEAN;                   -- 266: this event seals its stream
  v_closed RECORD;                         -- 271: the stream's row when it is sealed or archived
  v_archived_refusal RECORD;               -- 296: the archived stream's refusal, when its policy refuses
  v_new_generation BOOLEAN := false;       -- 296: this append starts the stream's new generation
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  v_full JSONB;                            -- 285: the metadata as the caller wrote it
  v_route_id UUID;                         -- 285: the event's route id
  v_body_meta JSONB;                       -- 285: the metadata its row keeps
BEGIN
  -- The emit chains' key for this stream (214), so a direct append and a chain emit serialize too.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  -- 266 (#1733): a sealed stream refuses every append. The caller awaits the append, so the refusal is raised (SQLSTATE
  -- WHS01, which both drivers surface as StreamSealedException) and nothing is stored.
  -- 271 (#1738): so does an archived stream (its tombstone holds the versions an append here would reuse), raised as
  -- SQLSTATE WHA01 under its type's Refuse policy or WHA02 under AutoRestore, which tells the caller to restore the
  -- stream and retry once (both drivers surface either as StreamArchivedException). One probe of
  -- idx_stream_lifecycle_closed, which holds only the sealed and the archived streams, answers both, under the lock above.
  SELECT l.sealed_at, l.sealed_reason, l.state, l.last_version, l.stream_type INTO v_closed
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = p_stream_id
    AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
  IF FOUND THEN
    IF v_closed.sealed_at IS NOT NULL THEN
      RAISE EXCEPTION USING
        ERRCODE = 'WHS01',
        MESSAGE = __SCHEMA__._wh_stream_sealed_refusal_text(p_stream_id, v_closed.sealed_at, v_closed.sealed_reason),
        HINT = 'A sealed stream has ended; append to a new stream instead.';
    END IF;
    -- 296 (#1734): the policy as every write path reads it. NewGeneration with a snapshot at every parked cursor is no
    -- refusal: the stream starts its new generation below; without one it reads AutoRestore.
    SELECT r.append_policy, r.refusal_text INTO v_archived_refusal
    FROM __SCHEMA__._wh_archived_stream_refusals(ARRAY[p_event_id], ARRAY[p_stream_id]) r
    LIMIT 1;
    IF FOUND THEN
      RAISE EXCEPTION USING
        ERRCODE = CASE WHEN v_archived_refusal.append_policy = 'AutoRestore' THEN 'WHA02' ELSE 'WHA01' END,
        MESSAGE = v_archived_refusal.refusal_text,
        HINT = 'Restore the stream (IArchiveStore.RestoreAsync) before appending to it.';
    END IF;
    PERFORM __SCHEMA__._wh_start_new_generations(ARRAY[p_stream_id]);
    v_new_generation := true;
  END IF;

  -- 228 (#1546): an event that would make its stream hold both ephemeral and Sourced events is refused, under the
  -- lock above, before anything is stored. The caller awaits the append, so the refusal is raised (SQLSTATE WH133,
  -- which both drivers surface as MixedStreamEventException). The check only reads.
  SELECT r.refusal_text, r.refusal_detail INTO v_refusal
  FROM __SCHEMA__._wh_mixed_stream_refusals(
    ARRAY[p_event_id], ARRAY[p_stream_id], ARRAY[COALESCE(p_flags, 0)], ARRAY[p_event_type]) r
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION USING
      ERRCODE = 'WH133',
      MESSAGE = v_refusal.refusal_text,
      DETAIL = v_refusal.refusal_detail::TEXT,
      HINT = 'Publish the ephemeral events on their own stream.';
  END IF;

  -- 285 (#1725): the row keeps the slim form unless the event is kept full, and carries its route id; under the Linked
  -- form the metadata as written is staged. The two drivers write different forms (EnvelopeMetadata, Hops; the Dapper
  -- store's, hops), and the slim form reduces whichever hop list is there and keeps every other key as written.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;
  v_full := COALESCE(p_metadata, '{}'::jsonb);
  v_route_id := __SCHEMA__._wh_route_id(split_part(p_event_type, ',', 1), COALESCE(v_full -> 'Hops', v_full -> 'hops'));
  v_body_meta := CASE
    WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(p_event_type, ',', 1), v_full_for)
    THEN v_full || jsonb_build_object('rid', v_route_id)
    -- 537 (#2323): once the trim gate is open, less what the row holds in its own columns, its event id and scope.
    WHEN __SCHEMA__._wh_metadata_trim_on()
    THEN __SCHEMA__._wh_trim_row_values(__SCHEMA__._wh_slim_event_metadata(v_full, v_route_id), p_event_id, p_scope)
    ELSE __SCHEMA__._wh_slim_event_metadata(v_full, v_route_id)
  END;

  -- 295 (#1729): a superseding event's key, from the metadata the driver stamped it in, on a row carrying the flag only.
  v_key := CASE WHEN COALESCE(p_flags, 0) & c_flag_superseding <> 0 THEN p_metadata ->> 'sk' END;
  -- 352 (#2124): and its superseding group, beside the key.
  v_group := CASE WHEN COALESCE(p_flags, 0) & c_flag_superseding <> 0 THEN p_metadata ->> 'sg' END;

  -- A separate statement: its snapshot is taken after the lock was granted.
  INSERT INTO __SCHEMA__.wh_event_store (
    event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at, flags, supersede_key,
    supersede_group
  )
  SELECT
    p_event_id,
    p_stream_id,
    p_aggregate_id,
    p_aggregate_type,
    p_event_type,
    p_scope,
    -- 296 (#1734): a new generation continues from its ledger row's last version; no other append reads the ledger here.
    COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id),
             CASE WHEN v_new_generation THEN
               (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = p_stream_id)
             END,
             0) + 1,
    p_created_at,
    COALESCE(p_flags, 0),
    v_key,
    v_group
  RETURNING version, flags INTO v_version, v_flags;

  -- 295 (#1729): queue the older events of this key for the reaper.
  IF v_key IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(ARRAY[p_event_id], p_created_at);
  END IF;

  -- The body half of the pointer/body split (077). Constraint-less ON CONFLICT, as the emit chain writes it:
  -- event_id is the table's only key.
  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  VALUES (p_event_id, p_event_data, v_body_meta)
  ON CONFLICT DO NOTHING
  -- 260: measured on the row the insert formed, as the emit chains measure theirs (the values as the row holds them,
  -- before any TOAST compression), so the two paths count a body the same way.
  RETURNING pg_column_size(event_data) + pg_column_size(metadata) INTO v_body_bytes;

  IF v_write_form = 'Linked' THEN
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    VALUES (p_event_id, p_created_at, v_route_id, split_part(p_event_type, ',', 1), v_full);
  END IF;

  -- 225 (#1600): fold the event into its stream digest bucket, as the emit chain's digest_folds does, still under
  -- the stream lock taken above. One event, so one row: the chain's bit_xor and COUNT(*) over a single event are
  -- its two hashes and 1.
  IF EXISTS (SELECT 1 FROM (SELECT v_flags AS flags, p_event_type AS event_type) ev WHERE __DIGEST_ELIGIBLE_V2__(ev)) THEN
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      ARRAY[__EMPTY_UUID__::uuid], ARRAY[COALESCE(p_scope ->> 't', '')], ARRAY[p_event_type], ARRAY[p_stream_id],
      ARRAY[p_event_id], 'fold', p_created_at);
  END IF;

  -- 260 (#1732): the stream lifecycle ledger, as the emit chains keep it, still under the stream lock taken above. The
  -- same events count (not ephemeral, not a compaction origin, on a stream), and the same columns move: an append never
  -- writes an indexed column, so the update is heap-only.
  -- 266 (#1733): a terminal event seals the stream in this transaction; any other append writes the sealing columns
  -- back unchanged, which keeps the update heap-only.
  IF COALESCE(v_flags, 0) & c_flags_off_the_ledger = 0 AND p_stream_id <> __EMPTY_UUID__::uuid THEN
    v_ends_stream := COALESCE(v_flags, 0) & c_flag_terminal <> 0;
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    VALUES (
      p_stream_id,
      p_aggregate_type,
      p_scope ->> 't',
      p_created_at,
      date_trunc('hour', p_created_at, 'UTC'),
      v_version,
      COALESCE(v_body_bytes, 0),
      CASE WHEN v_ends_stream THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN v_ends_stream THEN p_created_at END,
      CASE WHEN v_ends_stream THEN __SCHEMA__._wh_terminal_seal_reason(split_part(p_event_type, ',', 1), p_event_id) END
    )
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at);
  END IF;

  RETURN v_version;
END;
$$ LANGUAGE plpgsql;

-- ── wh_retire_event_types: last word 539, copied whole; the eligibility rule is version 2 ──
-- <docs>fundamentals/events/retired-event-types#what-the-purge-does</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_RemovesEveryEventOfTheType_FromAMixedStream_AndTheOtherTypesStayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_UnfoldsTheTypeFromTheDigests_SoTheyEqualARecount_AndRefoldsAndBumpsOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_DropsWhatACutCarriedForTheType_AndItsRemovedEpochRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_TakesTheEventsCompanionRows_AndKeepsTheirDeduplicationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_AStreamAGateHolds_WaitsWhole_AndTheOthersAreRetiredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_TakesAtMostTheBatchOfStreams_AndTheNextBatchTakesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ASupersedingType_UnfoldsEachKeysSlotOnce_AndASlotSharedWithAStayingTypeIsHandedOnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_DebugMode_NoNames_OrNothingStored_ChangeNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorOnARetiredEvent_StepsBackToTheEventBeforeIt_AndTheStreamIsPurgedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorWithNoEarlierEventToStepBackTo_KeepsTheStreamWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_ACursorWithOtherWorkPendingBeforeIt_KeepsTheStreamWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeSqlTests.cs:Retire_TheStreamsFirstEvent_LeavesItsBusinessTimeOnTheLedgerAsync</tests>
-- <docs>resilience/stream-integrity#retired-event-types</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CleanupIntegrityMatrixTests.cs:CleanupAndIntegrity_Converge_AndAGenuineLossBesideTheCleanupIsStillRepairedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_retire_event_types(p_names TEXT[], p_batch_streams INTEGER DEFAULT 500)
RETURNS TABLE(events_retired BIGINT, streams_retired INTEGER, streams_waiting INTEGER, status TEXT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_stored TEXT[];
  v_streams UUID[];
  v_leaving UUID[];
  v_waiting UUID[];
  v_victims UUID[];
  v_done UUID[];
  v_held INTEGER := 0;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_deleted BIGINT := 0;
BEGIN
  IF COALESCE(cardinality(p_names), 0) = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 0, 0, 'ok'::TEXT;
    RETURN;
  END IF;

  -- debug_mode keeps every event for forensics, as every other reaper does.
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT 0::BIGINT, 0, 0, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  SELECT array_agg(DISTINCT n.stored_name) INTO v_stored
  FROM __SCHEMA__.wh_retired_event_type_names(p_names) n
  WHERE n.stored_name IS NOT NULL;
  IF v_stored IS NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0, 0, 'ok'::TEXT;
    RETURN;
  END IF;

  -- The streams a gate holds wait whole: the stamper has not reached one of their retired events, a destruction hold
  -- is active on one, or a snapshot rests on one (a rewind restores a snapshot and moves the cursor onto its event).
  SELECT COUNT(DISTINCT g.stream_id)::INTEGER INTO v_held
  FROM __SCHEMA__.wh_event_store g
  WHERE g.event_type = ANY(v_stored)
    AND (g.commit_sequence IS NULL
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = g.event_id AND h.hold_until > NOW())
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots sn
                 WHERE sn.stream_id = g.stream_id AND sn.snapshot_event_id = g.event_id));

  -- The batch: streams holding a retired event, none of which a gate holds, in stream id order.
  SELECT array_agg(c.stream_id ORDER BY c.stream_id) INTO v_streams
  FROM (
    SELECT DISTINCT es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_type = ANY(v_stored)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store g
        WHERE g.stream_id = es.stream_id
          AND g.event_type = ANY(v_stored)
          AND (g.commit_sequence IS NULL
            OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = g.event_id AND h.hold_until > NOW())
            OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots sn
                       WHERE sn.stream_id = g.stream_id AND sn.snapshot_event_id = g.event_id)))
    ORDER BY es.stream_id
    LIMIT GREATEST(p_batch_streams, 1)
  ) c;
  IF v_streams IS NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0, v_held,
      CASE WHEN v_held > 0 THEN 'waiting (' || v_held || ' streams held)' ELSE 'ok' END;
    RETURN;
  END IF;

  -- The emit chains' per-stream locks, in stream id order, so nothing is appended to, truncated from or archived out of
  -- these streams while their events are judged and removed.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
  FROM unnest(v_streams) AS u(sid)
  ORDER BY u.sid;

  SELECT array_agg(es.event_id) INTO v_leaving
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = ANY(v_streams) AND es.event_type = ANY(v_stored);

  -- A cursor resting on a retired event steps back to the stream's last event before it, which its perspective applied
  -- too: the one hand-on every removal shares (538).
  PERFORM __SCHEMA__._wh_hand_on_cursors(v_leaving, v_streams, FALSE);

  -- Read again under the locks: a stream whose retired events a gate now holds, or a cursor still rests on, waits whole.
  SELECT COALESCE(array_agg(DISTINCT es.stream_id), ARRAY[]::UUID[]) INTO v_waiting
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_leaving)
    AND (es.commit_sequence IS NULL
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = es.event_id AND h.hold_until > NOW())
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots sn
                 WHERE sn.stream_id = es.stream_id AND sn.snapshot_event_id = es.event_id)
      OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_cursors cu
                 WHERE cu.stream_id = es.stream_id AND cu.last_event_id = es.event_id));

  SELECT array_agg(es.event_id), array_agg(DISTINCT es.stream_id) INTO v_victims, v_done
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_leaving) AND es.stream_id <> ALL(v_waiting);

  IF v_victims IS NOT NULL THEN
    -- The sequence range of the counted victims and of the retired types' removed-epoch rows, per lane, for the epoch
    -- refold after the delete (as 263). An epoch row's range is its epoch's.
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT r.lane, MIN(r.mn) AS mn, MAX(r.mx) AS mx
      FROM (
        SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
               CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS mn,
               CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS mx
        FROM __SCHEMA__.wh_event_store es
        WHERE es.event_id = ANY(v_victims)
          AND __DIGEST_ELIGIBLE_V2__(es)
        UNION ALL
        SELECT er.origin_service_id, er.epoch_id * __SCHEMA__._wh_epoch_width(er.origin_service_id),
               (er.epoch_id + 1) * __SCHEMA__._wh_epoch_width(er.origin_service_id) - 1
        FROM __SCHEMA__.wh_digest_epoch_removed er
        WHERE er.event_type = ANY(v_stored)
          AND er.epoch_id >= 0
      ) r
      WHERE r.mn IS NOT NULL
      GROUP BY r.lane
    ) t;

    -- Unfold the victims from their stream buckets through the one fold. A superseding copy counts as its key's slot
    -- through the key's anchor; the whole stream leaves, so each slot is unfolded once. A key whose copies of another
    -- type of its group stay keeps its slot: it is handed on to them below, not unfolded.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
      array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_victims)
      AND __DIGEST_ELIGIBLE_V2__(es)
      AND NOT (es.supersede_key IS NOT NULL AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store k
        WHERE k.stream_id = es.stream_id
          AND COALESCE(k.supersede_group, k.event_type) = COALESCE(es.supersede_group, es.event_type)
          AND k.supersede_key = es.supersede_key
          AND k.event_id <> ALL(v_victims)));
    PERFORM __SCHEMA__._wh_hand_on_slots(v_victims);

    -- A retired type is counted by no digest: its buckets in these streams with no live part left go, with whatever cuts
    -- carried into them (460), and so do its removed-epoch rows. Any other bucket the unfold emptied goes too.
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_done)
      AND (d.event_count <= 0
        OR (d.event_type = ANY(v_stored) AND d.event_count - COALESCE(d.carried_count, 0) <= 0));
    DELETE FROM __SCHEMA__.wh_digest_epoch_removed er WHERE er.event_type = ANY(v_stored);

    PERFORM __SCHEMA__._wh_keep_stream_start(v_victims);

    -- Each victim's companion rows go with it. Deduplication rows stay: a resend of a retired event is recognized as
    -- one, and the receive boundary turns away the type anyway.
    DELETE FROM __SCHEMA__.wh_perspective_events pe WHERE pe.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_superseded_events q WHERE q.event_id = ANY(v_victims);

    -- Bodies first, then pointers.
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ANY(v_victims);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    -- A retirement is a rule change: refold the sealed epochs the type sat in, then bump the generation once, floored at
    -- the lowest position touched, so consumers lower their seals that far and re-verify instead of alarming (093, 408).
    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  v_held := v_held + COALESCE(cardinality(v_waiting), 0);
  RETURN QUERY SELECT v_deleted, COALESCE(cardinality(v_done), 0), v_held,
    CASE WHEN v_held > 0 THEN 'ok (' || v_held || ' streams waiting)' ELSE 'ok' END;
END;
$$ LANGUAGE plpgsql;

-- ── _wh_cut_awaits_seal: last word 560, copied whole; the eligibility rule is version 2 ──
-- Whether a cut of the streams (at or below p_through_version; NULL: whole streams) must wait for the seal. Only when
-- integrity_cut_waits_for_seal is on. An event the digest counts must sit below its lane's sealed frontier; a received
-- one also below this service's verified seal for its origin, when it audits that origin (560: known, with a request
-- address); a local one, with integrity_cut_waits_for_consumers on, also below the lowest seal a consumer reported in the
-- last 7 days. 557: a received event with no origin position never waits.
-- <docs>resilience/stream-integrity#a-cut-waits-for-the-seal</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_OfAPositionedEventFromAnOriginThisServiceNeverAudits_IsNotHeldByItsSealAtZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_OfAPositionedEventFromAnAuditedOrigin_IsStillHeldByItsSealAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_OfAPositionedEventFromAKnownOriginWithNoRequestAddress_IsNotHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_FromAnUnauditedOrigin_StillWaitsForItsLanesSealedFrontierAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:Truncate_OfReceivedHistoryWithNoOriginSequence_IsNotHeld_EvenWithNothingSealedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:Truncate_OfLocalHistoryTheStamperHasNotSequenced_IsStillHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Truncate_OfLocalHistory_WaitsForEveryConsumersReportedSeal_OnlyWhenThatSettingIsOnAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_cut_awaits_seal(p_stream_ids UUID[], p_through_version BIGINT)
RETURNS BOOLEAN AS $$
DECLARE
  v_gate BOOLEAN;
  v_consumers BOOLEAN;
  v_lowest_consumer BIGINT;
BEGIN
  SELECT (s.setting_value = 'true') INTO v_gate
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_cut_waits_for_seal';
  IF NOT COALESCE(v_gate, FALSE) THEN
    RETURN FALSE;
  END IF;

  SELECT (s.setting_value = 'true') INTO v_consumers
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_cut_waits_for_consumers';
  IF COALESCE(v_consumers, FALSE) THEN
    SELECT MIN(c.sealed_through) INTO v_lowest_consumer
    FROM __SCHEMA__.wh_integrity_consumer_seals c
    WHERE c.reported_at >= NOW() - INTERVAL '7 days';
  END IF;

  RETURN EXISTS (
    SELECT 1
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_digest_epoch_frontiers f
      ON f.origin_service_id = COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)
    LEFT JOIN __SCHEMA__.wh_integrity_seals s ON s.origin_service_id = es.origin_service_id
    -- 560 (#2544): this service's seal for an origin counts only while this service audits that origin.
    LEFT JOIN __SCHEMA__.wh_integrity_known_origins k
      ON k.origin_service_id = es.origin_service_id AND NULLIF(k.request_topic, '') IS NOT NULL
    WHERE es.stream_id = ANY(p_stream_ids)
      AND (p_through_version IS NULL OR es.version <= p_through_version)
      AND __DIGEST_ELIGIBLE_V2__(es)
      -- 557 (#2529): a received event with no origin position is never sealed; its cut is carried into the open window.
      AND NOT (es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL)
      AND (
        f.origin_service_id IS NULL
        OR CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END IS NULL
        OR CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END
             >= (f.closed_through_epoch + 1) * f.epoch_width
        OR (es.origin_service_id IS NOT NULL AND s.sealed_through IS NOT NULL AND k.origin_service_id IS NOT NULL
            AND es.origin_commit_sequence >= s.sealed_through)
        OR (es.origin_service_id IS NULL AND v_lowest_consumer IS NOT NULL
            AND es.commit_sequence >= v_lowest_consumer)));
END;
$$ LANGUAGE plpgsql STABLE;

-- ── _emit_event_store_chain: last word 573, copied whole; the eligibility rule is version 2 ──
-- ---------------------------------------------------------------------------------------------
-- _emit_event_store_chain: copied from its last word (567), with the 573 delta: emission position, never id.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmitEventStoreChainCollectiveSqlTests.cs:EmitEventStoreChain_CollectiveEvent_CreatesSingleCollectiveSinkRowAsync</tests>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:OutboxAppend_CreatesTheRow_FromTheStreamsFirstEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Append_WritesOneLedgerRowPerStreamPerBatch_NotOnePerEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:AppendWithinTheHour_IsAHeapOnlyUpdate_WhileAnIndexedColumnChangeIsNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Bucket_AdvancesOnlyWhenTheHourChanges_AndAnEarlierClockNeverMovesItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:ConcurrentBatchesOnDifferentStreams_NeitherWaitsForTheOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheOutbox_SealsTheStream_InTheTransactionThatStoresItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxBatch_EventsAfterATerminalEvent_AreDeadLettered_AndTheOnesBeforeItAreStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalFlag_OnAnEphemeralEvent_SealsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/StreamSealingEmitCostScenarioTests.cs:EmitChain_WithAndWithoutTheSealCheck_ReportAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Outbox_StampsTheKeyOnlyOnASupersedingEvent_AndQueuesTheOlderOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_TwoEventsOfOneKeyInOneBatch_QueueTheOlderAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_ANewerEventOfAnotherTypeInTheGroup_QueuesTheOlder_InEitherOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_Refuse_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesOneRequestPerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/ArchivedStreamEmitCostScenarioTests.cs:EmitChain_With266sProbeAndWith271s_ReportAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:OutboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:FullListedType_KeepsTheFullRow_ByTypeAndByNamespaceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DebugMode_KeepsEveryRowFullAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:WriteFormFull_KeepsFullRows_AndStagesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:WriteFormSlim_StoresSlimRows_AndStagesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/StoredMetadataEmitCostScenarioTests.cs:EmitChain_FullAgainstLinked_ReportAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_StoresTheEventAtTheNextVersion_AndDeadLettersNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_AParkedCursorWithoutASnapshotAtIt_IsDeadLetteredAwaitingRestoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:StoreOutboxMessages_AnAwaitedWrite_IsStoredAtTheNextVersionAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldsEveryLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldingIntoARowWithoutIdLanes_KeepsThemNullAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_LeavesOutTheMessageIdAndTheHopScopeTheRowHolds_AndNothingElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_KeepsWhatTheRowDoesNotHoldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:FullRows_KeepEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesReadTests.cs:EveryReader_ReadsATrimmedRow_AsItReadTheSameRowInTheSlimFormAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:EmitChain_InMapMode_LeasesByPartition_NeverToTheStoredOwnerAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:EmitChain_EventsListedAgainstTheirIdOrder_AreVersionedInTheOrderListedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:EmitChain_AnIdListedTwice_KeepsItsFirstPositionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreVersionOrderingSqlTests.cs:EmitChain_BatchedShuffledMessageIds_InsertsInTheBatchsOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreVersionOrderingSqlTests.cs:EmitEventStoreChain_AssignsVersions_InTheOrderTheBatchListsThem_NotByIdOrCreatedAtAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain(
  p_outbox_message_ids UUID[],
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;  -- NOSONAR S1192: the field name recurs per function; PL/pgSQL has no file-level constants
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_superseding_ids UUID[];                -- 295: the stored events that carry a superseding key
  v_streams UUID[];                        -- 266: the batch's streams, in lock order
  v_ends_a_stream BOOLEAN;                 -- 266: the batch carries a terminal event
  v_any_sealed BOOLEAN;                    -- 271: a stream of the batch is sealed
  v_any_archived BOOLEAN;                  -- 271: a stream of the batch is archived (and not sealed)
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_trim BOOLEAN;                          -- 537: the trim gate is open (wh_settings metadata_trim_row_values)
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  IF p_outbox_message_ids IS NULL OR cardinality(p_outbox_message_ids) = 0 THEN
    RETURN 0;
  END IF;

  -- Per-stream advisory locks. Without these, two concurrent transactions can both
  -- read MAX(version)=N from wh_event_store and both attempt INSERT at version=N+1,
  -- violating idx_event_store_stream UNIQUE(stream_id, version) (PG error 23505). The
  -- legacy process_work_batch ran serially through one ProcessWorkBatchAsync call so
  -- the race didn't exist; the new path lets parallel handlers / strategy flushes
  -- target the same stream concurrently.
  --
  -- Lock order is the stream id, sorted ascending, which keeps the nesting deadlock-free between
  -- any pair of transactions touching overlapping stream sets.
  -- pg_advisory_xact_lock auto-releases at commit/rollback.
  -- 266 (#1733): the read that finds the batch's streams also notes whether the batch ends one, so the sealing check
  -- below costs no second read of the batch.
  SELECT array_agg(b.sid ORDER BY b.sid), COALESCE(bool_or(b.ends_stream), false)
    INTO v_streams, v_ends_a_stream
  FROM (
    SELECT o.stream_id AS sid,
           bool_or(COALESCE(o.flags, 0) & c_flag_terminal <> 0
                   AND COALESCE(o.flags, 0) & c_flags_off_the_ledger = 0) AS ends_stream
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
    GROUP BY o.stream_id
  ) AS b;

  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || streams_to_lock.sid::text))
  FROM (
    SELECT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY u.sid
  ) AS streams_to_lock;

  -- 266 (#1733): a sealed stream refuses every append. The refused event alone is dead-lettered, with the refusal as
  -- its error text, and the copy below stores the rest of the batch; a raise here would fail every stream in the batch
  -- for one sealed stream. Under the locks taken above, so a seal cannot land between this check and the copy.
  -- 271 (#1738): so does an archived stream, by its type's AppendToArchived policy, after the seal (a sealed stream's
  -- refusal is the seal's). One probe per stream of idx_stream_lifecycle_closed, which holds only the sealed and the
  -- archived streams, answers both, so the common case (none of either, no terminal event) costs what 266's probe cost.
  SELECT COALESCE(bool_or(l.sealed_at IS NOT NULL), false),
         COALESCE(bool_or(l.sealed_at IS NULL AND l.state = 'Archived'), false)
    INTO v_any_sealed, v_any_archived
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = ANY(v_streams)
    AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
  IF v_ends_a_stream OR v_any_sealed THEN
    PERFORM __SCHEMA__._wh_refuse_sealed_stream_outbox_events(p_outbox_message_ids, p_instance_id, p_now);
  END IF;
  IF v_any_archived THEN
    PERFORM __SCHEMA__._wh_refuse_archived_stream_outbox_events(p_outbox_message_ids, p_instance_id, p_now);
    -- 296 (#1734): the archived streams the refusal left alone start their new generation.
    PERFORM __SCHEMA__._wh_start_new_generations(v_streams);
  END IF;

  -- 285 (#1725): the stored-metadata settings, read once for the whole batch.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;
  -- 537 (#2323): the trim gate, read once for the whole batch, beside the settings.
  v_trim := __SCHEMA__._wh_metadata_trim_on();

  -- Phase 4.5A-equivalent: store outbox events into wh_event_store with sequential versioning.
  -- 573 (#2551): a stream's events are numbered in the order the batch lists them, which is the order the producer
  -- emitted them (store_outbox_messages hands them over in that order), never by id. An id is time-ordered and says
  -- nothing about stream position: a derived id sorts at its cause, so an effect derived from a cause can sort before
  -- it. An id listed twice keeps its first position.
  -- Phase H step 10 slice 3: version is computed via a correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, if a future refactor weakens or bypasses the lock, the
  -- per-row MAX read still picks up any concurrent commits.
  -- Migration 061: o.flags carried into wh_event_store.flags (was dropped here previously).
  WITH outbox_events AS (
    SELECT
      o.message_id,
      o.stream_id,
      o.message_type,
      o.event_data,
      o.metadata,
      o.scope,
      o.flags,
      o.created_at,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY batch.position) AS row_num
    FROM (
      SELECT listed.message_id, min(listed.position) AS position
      FROM unnest(p_outbox_message_ids) WITH ORDINALITY AS listed(message_id, position)
      GROUP BY listed.message_id
    ) AS batch
    JOIN __SCHEMA__.wh_outbox o ON o.message_id = batch.message_id
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  -- Migration 072: materialize the extracted payload + built metadata ONCE, so the pointer INSERT
  -- and the ephemeral body offload read identical values without recomputing the JSON extraction.
  computed_full AS (
    SELECT
      oe.message_id,
      oe.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(oe.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(oe.message_type) AS event_type,
      COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload') AS body_data,
      COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb) AS hops,
      jsonb_build_object(
        c_field_message_id, COALESCE(oe.event_data::jsonb -> 'id', oe.event_data::jsonb -> c_field_message_id, oe.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN oe.metadata IS NOT NULL
             AND jsonb_typeof(oe.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((oe.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS full_meta,
      oe.scope,
      oe.row_num,
      oe.flags,
      -- 295 (#1729): a superseding event's key, from the metadata the dispatch or receive path stamped it in. Only
      -- a row carrying the flag gets one, so a stray 'sk' on another event is never read.
      CASE WHEN COALESCE(oe.flags, 0) & c_flag_superseding <> 0 THEN oe.metadata::jsonb ->> 'sk' END AS supersede_key,
      -- 352 (#2124): and its superseding group, beside the key, on a flagged row only.
      CASE WHEN COALESCE(oe.flags, 0) & c_flag_superseding <> 0 THEN oe.metadata::jsonb ->> 'sg' END AS supersede_group
    FROM outbox_events oe
  ),
  -- 285 (#1725): each event's route id, and the metadata its row keeps: the full form (plus the route id) under debug
  -- mode, under the Full form or for a listed type, otherwise the slim form. Pure CPU on values already in hand.
  -- Materialized, so each event's route id is computed once however many times the next step reads it.
  computed_routes AS MATERIALIZED (
    SELECT cf.*, __SCHEMA__._wh_route_id(split_part(cf.event_type, ',', 1), cf.hops) AS route_id
    FROM computed_full cf
  ),
  computed AS (
    SELECT cr.*,
      CASE
        WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(cr.event_type, ',', 1), v_full_for)
        THEN cr.full_meta || jsonb_build_object('rid', cr.route_id)
        -- 537 (#2323): once the trim gate is open, less what the row holds in its own columns, its event id and scope.
        WHEN v_trim THEN __SCHEMA__._wh_trim_row_values(
               __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id), cr.message_id, cr.scope::jsonb)
        ELSE __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id)
      END AS body_meta
    FROM computed_routes cr
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, supersede_key, supersede_group
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      -- 296 (#1734): a stream with nothing hot in a batch that held an archived stream may be a new generation, which
      -- continues from its ledger row's last version; any other batch reads nothing more.
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id),
               CASE WHEN v_any_archived THEN
                 (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = c.stream_id)
               END,
               0) + c.row_num,
      p_now,
      c.flags,
      c.supersede_key,
      c.supersede_group
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version, supersede_key
  ),
  -- Migration 077 (full split): offload EVERY body. Joined to stored_events so only events actually
  -- stored this call get a body row; ON CONFLICT keeps re-store idempotent.
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- 285 (#1725): under the Linked form, the full form is appended to the staging table for maintenance to distill into
  -- routes and traces. Append-only: the key is new for every stored event, so there is nothing to check.
  staged_metadata AS (
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    SELECT c.message_id, p_now, c.route_id, split_part(c.event_type, ',', 1), c.full_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE v_write_form = 'Linked'
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    -- 336 (#2106): every lane, through the one fold helper. Referenced by the statement's final SELECT, so it
    -- always runs (a CTE that writes nothing runs only when read).
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(__EMPTY_UUID__::uuid), array_agg(COALESCE(c.scope::jsonb ->> 't', '')), array_agg(c.event_type),
      array_agg(c.stream_id),
      -- 461 (#2364): a superseding copy counts as its key's slot, once: only when the store holds no copy of its key yet,
      -- and only the first such copy of this batch. The slot is passed already: the rows this statement stores are not
      -- visible to it, so the helper could not read them back.
      array_agg(CASE WHEN c.supersede_key IS NULL THEN c.message_id
                     ELSE __SCHEMA__._wh_slot_id(c.stream_id, COALESCE(c.supersede_group, c.event_type), c.supersede_key) END),
      'fold', p_now, TRUE) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE __DIGEST_ELIGIBLE_V2__(c)
      AND (c.supersede_key IS NULL
           OR (NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_event_store o
                 WHERE o.stream_id = c.stream_id
                   AND COALESCE(o.supersede_group, o.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND o.supersede_key = c.supersede_key)
               AND NOT EXISTS (
                 SELECT 1 FROM computed c2
                 JOIN stored_events se2 ON se2.event_id = c2.message_id
                 WHERE c2.stream_id = c.stream_id
                   AND COALESCE(c2.supersede_group, c2.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND c2.supersede_key = c.supersede_key
                   AND se2.version < se.version)))
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  -- 266 (#1733): a terminal event among them seals the stream in this statement, so the event and the seal commit
  -- together; every other append writes state, sealed_at and sealed_reason back unchanged, which keeps it heap-only.
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0)),
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN p_now END,
      (array_agg(__SCHEMA__._wh_terminal_seal_reason(c.aggregate_type, c.message_id) ORDER BY se.version)
         FILTER (WHERE COALESCE(c.flags, 0) & c_flag_terminal <> 0))[1]
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at)
  )
  SELECT array_agg(event_id), array_agg(event_id) FILTER (WHERE supersede_key IS NOT NULL)
    INTO v_stored_event_ids, v_superseding_ids
  FROM stored_events CROSS JOIN digest_folds;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 295 (#1729): a stored superseding event queues the older events of its stream, type and key for the reaper. A
  -- separate statement, so it sees every event this batch stored as well as the ones before it. Skipped outright when
  -- the batch stored none, so an ordinary batch pays nothing; otherwise one probe of idx_event_store_superseding per
  -- superseding event.
  IF v_superseding_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(v_superseding_ids, p_now);
  END IF;

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Phase 4.6-equivalent: auto-create perspective events for matching event types.
  -- Phase H step 6 slice 2: populate partition_number via compute_partition(stream_id, p_partition_count)
  -- so claim_orphaned_perspective_events can apply partition-modulo load balancing symmetric
  -- with the outbox / inbox claim paths.
  -- Slice 26.14: route the lease through wh_active_streams. When a live owner is pinned for
  -- the stream, lease to that owner regardless of which instance ran the commit, eliminates
  -- the cross-instance saga race (different instances commit to the same stream, each
  -- selfishly leasing to themselves, drainer races) that produced the residual ~1000 cursor
  -- inversions in a production run. When no live owner is pinned yet (new stream OR stale owner),
  -- fall back to the commit instance so sync paths (UI waiting on a perspective checkpoint)
  -- don't pay claim_orphaned-polling-interval latency on first-event-per-stream.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Migration 061: collective-event routing. A collective event carries no perspective
  -- association, route it to the single fixed __collective__ sink so the perspective worker
  -- dispatches it via ICollectiveDispatcher exactly once (the dispatcher fans out to every
  -- matching model handler internally). One sink row per collective event, driven by the
  -- flag bit, independent of associations. Same partition / owner-lease / dedupe semantics as
  -- the association branch above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    c_collective_sink,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND (es.flags & c_flag_collective) = c_flag_collective
    -- 391 (#2145): only a type a handler on this service applies, once every service registered here gates.
    AND __SCHEMA__._wh_collective_row_wanted(es.event_type)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. PG buffers NOTIFY until COMMIT and
  -- dedups (channel, payload) within the transaction, so a tx storing 10k events
  -- delivers exactly one wh_committed notification to each LISTEN-er. The stamper
  -- is the only listener; on wake it runs stamp_pending_commit_sequences within ~1ms
  -- instead of waiting for its polling tick.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

-- ── _emit_event_store_chain_for_inbox: last word 574, copied whole; the eligibility rule is version 2 ──
-- ---------------------------------------------------------------------------------------------
-- _emit_event_store_chain_for_inbox: copied from its last word (567), with the 574 delta: receive order, never id.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Inbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Inbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync</tests>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:InboxAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamOrderSqlTests.cs:TwoClaimsOfOneStream_TheHeadsClaimCommitsLast_VersionsFollowTheProducerOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:InboxStoredAuditRecord_LeavesTheOriginsDigestLaneUntouched_WhileADomainEventFoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheInbox_SealsTheStream_AndLaterRowsAreRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:InboxAppend_ToASealedStream_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#across-services</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Inbox_StampsTheKey_AndQueuesTheOlderOneAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_Inbox_StampsTheGroup_AndQueuesAcrossTypesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_Refuse_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesTheRequestAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:InboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:InboxAppend_StoresTheReceivedEventAtTheNextVersionAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:InboxEmitChain_FoldsEveryLaneIntoTheOriginsBucketAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#the-claim-runs-only-what-can-find-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_ARowWhoseEventIsAlreadyStored_IsStamped_WithoutRunningTheCopyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_AfterAPassWithNoCandidate_AReadyRowIsStoredByTheNextClaimAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_AnOrdinaryEvent_RunsNoCollectiveRoutingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_ACollectiveEvent_IsStillRoutedToTheCollectiveSinkAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#a-held-back-stream-is-claimed-whole</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackStreamsClaimSqlTests.cs:Claim_ARowHeldBackBehindAPredecessorAPeerHolds_IsNotChargedAnAttemptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackStreamsClaimSqlTests.cs:Claim_AHeldBackRowAcquiredByAnEarlierClaim_IsNotRefundedAgainAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:InboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#a-row-waiting-for-its-stream-head</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_ARowHeldBackBehindAnUnstoredHead_ShowsItAwaitsTheStreamHeadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_AWaitingRowWhoseLeaseLapsed_IsReclaimedStillAwaitingTheHead_NotAsALeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_OnceTheHeadIsHeld_TheWaitingRowIsStored_AndNoLongerShowsItWaitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_AHeldBackRowCarryingAnEarlierLeaseExpiry_ShowsItAwaitsTheHeadInsteadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_AHeldBackRowCarryingAnotherRecordedFailure_KeepsItAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <docs>fundamentals/event-store/version-ordering#receive-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:OneClaim_AnOriginsEvents_AreVersionedByTheOriginsSequence_NotByIdAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:AnUnpositionedEvent_TakesTheReplaysPlace_AmongItsOriginsPositionedEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:TwoOrigins_KeepTheirArrivalSlots_EachDealtInItsOwnOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:AnOriginsEarlierEvent_HeldElsewhere_HoldsBackTheLaterOne_WhateverTheIdsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:ALaterArrivalHeldElsewhere_DoesNotHoldBackAnEarlierArrivalOfAnotherOriginAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcileRewindDeclarationSqlTests.cs:InboxEmitChain_LateEventBelowCursor_DeclaresRewindOnTheCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcileRewindDeclarationSqlTests.cs:InboxEmitChain_EventAboveCursor_DoesNotDeclareRewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcileRewindDeclarationSqlTests.cs:InboxEmitChain_AnotherOriginsEvent_WithALowerId_DoesNotDeclareRewindAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  v_local_service_id UUID;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  -- 228 (#1546): the rows this pass would copy, for the refusal before the copy.
  v_candidate_ids UUID[];
  v_candidate_streams UUID[];
  v_candidate_flags INTEGER[];
  v_candidate_types TEXT[];
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_superseding_ids UUID[];                -- 295: the stored events that carry a superseding key
  v_sealed_refused UUID[];                 -- 266: the rows a sealed stream refused
  v_any_sealed BOOLEAN;                    -- 271: a candidate's stream is sealed
  v_any_archived BOOLEAN;                  -- 271: a candidate's stream is archived (and not sealed)
  v_archived_refused UUID[];               -- 271: the rows an archived stream refused
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_trim BOOLEAN;                          -- 537: the trim gate is open (wh_settings metadata_trim_row_values)
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  v_any_collective BOOLEAN;                -- 367: an event this pass stored carries the collective flag
  -- 551 (#1630): the failure reasons a held-back row's state is read and written with (MessageFailureReason).
  c_reason_awaiting_stream_head CONSTANT INTEGER := 30;  -- AwaitingStreamHead
  c_reason_lease_expired CONSTANT INTEGER := 6;          -- LeaseExpired, as the claim's lapsed-lease branch records it
  c_reason_unknown CONSTANT INTEGER := 99;               -- Unknown, the column's default: a row that has not failed
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  -- Migration 087: resolve the LOCAL service id once. store_inbox_messages (062) COALESCEs a
  -- missing envelope SourceServiceId to the local id, so a wh_inbox row attributed to SELF (or
  -- zero) is a locally-originated event (loopback), its origin_service_id must stay NULL,
  -- matching the 046 contract ("NULL for locally-originated events").
  SELECT service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config LIMIT 1;

  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Reproduced in production on a consumer's
  -- service during job creation. Lock order is hashtext(stream_id::text), sorted ASC,
  -- ensures deadlock-free nesting between any pair of transactions touching overlapping stream
  -- sets. pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    -- 162: every column this pass reads is on wh_inbox_state, so the lock pass never touches the
    -- wide message row at all. chain_emitted_at moved here precisely so this predicate stays on one
    -- table and one partial index can cover it; see idx_inbox_state_chain_pending.
    SELECT DISTINCT ist.stream_id AS sid
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      -- 158: only rows this chain has never read (idx_inbox_state_chain_pending); every held row was
      -- read against the event store on every poll before, twice.
      AND ist.chain_emitted_at IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
    ORDER BY ist.stream_id
  ) AS streams_to_lock;

  -- 367: no candidate, nothing to copy. The lock pass selects by the same predicate as every later read of this pass
  -- (the refusals' candidates, the copy's inbox_events), so when it locks nothing they find nothing, and only the stamp
  -- below has work: rows whose event is already stored. Each statement skipped is one not planned, and this function
  -- inherits claim_work's force_custom_plan. A row a concurrent claim commits after this read is not lost: it is still
  -- unstamped, so the next claim's guard runs this pass for it, as it would have after any pass that missed it.
  IF NOT FOUND THEN
    UPDATE __SCHEMA__.wh_inbox_state ist
    SET chain_emitted_at = p_now,
        -- 551 (#1630): a row that waited for its stream's head and is now stored no longer waits.
        failure_reason = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN c_reason_unknown
                              ELSE ist.failure_reason END,
        error = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN NULL ELSE ist.error END
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      );
    RETURN 0;
  END IF;

  -- 228 (#1546): a received event that would make its stream hold both ephemeral and Sourced events is refused
  -- before the copy. Nobody awaits a claim, so every refusal here is dead-lettered: recorded in wh_dead_letters and
  -- its lease row completed in place, so the copy below no longer sees it and the claim never offers it again. The
  -- message row is not touched here: this runs inside claim_work after the stream ledger, and wh_inbox comes first
  -- in the canonical lock order. The rows are the ones the copy reads, by the same predicate, under the locks taken
  -- above.
  --
  -- 574 (#2551): the candidates are listed in RECEIVE ORDER, the order this pass versions each stream in and the order
  -- the refusals judge "first" and "after" by (their position in these arrays). Never by id: an id is time-ordered,
  -- never stream position. Receive order is the order a replay folds the stream in (ReplayEventOrder, #2257), so what
  -- is stored and what a replay folds agree:
  --   * each origin's events in the origin's order: its commit sequence (the origin position the event carries);
  --   * an origin's event that carries no position (0: a fan-out child, or a redelivery that lost it) goes before the
  --     first of its origin's positioned events, in origin order, whose id sorts after its own, and after them all when
  --     none does. This is the replay's rule for it (#2379) and the one fallback to an id: nothing positional exists;
  --   * between origins, the arrival order: each origin's events are dealt, in the origin's order, into the arrival
  --     slots that origin holds (received_at, then origin, then the origin's own order breaking a tie).
  -- A received event of this service, or one with no origin, is one origin (the empty id), as the copy files it.
  WITH cand AS (
    SELECT ist.message_id, ist.stream_id, i.flags, i.message_type, ist.received_at,
           CASE WHEN i.source_service_id IS NULL OR i.source_service_id = __EMPTY_UUID__::uuid
                     OR i.source_service_id = v_local_service_id
                THEN __EMPTY_UUID__::uuid ELSE i.source_service_id END AS origin,
           NULLIF(i.source_commit_sequence, 0) AS origin_seq
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
  ),
  -- The origin-order key: a positioned event's own (position, id); for an unpositioned event, the (position, id) of the
  -- positioned event it goes just before (#2379), and none when it goes after them all.
  keyed AS (
    SELECT k.*,
           COALESCE(k.origin_seq, nx.origin_seq) AS origin_key,
           CASE WHEN k.origin_seq IS NOT NULL THEN k.message_id ELSE nx.message_id END AS origin_key_id
    FROM cand k
    LEFT JOIN LATERAL (
      SELECT p.origin_seq, p.message_id FROM cand p
      WHERE p.stream_id = k.stream_id AND p.origin = k.origin AND p.origin_seq IS NOT NULL
        AND p.message_id > k.message_id
      ORDER BY p.origin_seq, p.message_id
      LIMIT 1
    ) AS nx ON k.origin_seq IS NULL
  ),
  -- The event's rank in its origin's order: by the key, an unpositioned event just before the positioned one whose key it
  -- took, the id breaking what is left (the replay's tie-break).
  ranked AS (
    SELECT r.*,
           row_number() OVER (PARTITION BY r.stream_id, r.origin
                              ORDER BY r.origin_key NULLS LAST, r.origin_key_id, (r.origin_seq IS NOT NULL), r.message_id)
             AS origin_rank
    FROM keyed r
  ),
  -- The arrival slots, and each slot's rank among its origin's slots.
  slotted AS (
    SELECT s.*,
           row_number() OVER (PARTITION BY s.stream_id ORDER BY s.received_at, s.origin, s.origin_rank) AS slot
    FROM ranked s
  ),
  -- The event of origin rank n takes its origin's n-th slot. Each side is numbered over the whole batch in (stream,
  -- origin, rank) order, so the n-th event and the n-th slot of an origin share one number and the two meet on a single
  -- key: a join on (stream, origin) with the rank as a filter compared every pair of an origin's rows.
  events AS (
    SELECT e.message_id, e.stream_id, e.flags, e.message_type,
           row_number() OVER (ORDER BY e.stream_id, e.origin, e.origin_rank) AS pair
    FROM slotted e
  ),
  slots AS (
    SELECT p.slot, row_number() OVER (ORDER BY p.stream_id, p.origin, p.slot) AS pair
    FROM slotted p
  )
  SELECT array_agg(e.message_id ORDER BY e.stream_id, place.slot), array_agg(e.stream_id ORDER BY e.stream_id, place.slot),
         array_agg(e.flags ORDER BY e.stream_id, place.slot), array_agg(e.message_type ORDER BY e.stream_id, place.slot)
    INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
  FROM events e
  JOIN slots place ON place.pair = e.pair;
  -- 266 (#1733): a sealed stream refuses every append, so its rows are dead-lettered and completed in place first, as
  -- the mixed-stream refusal does, and leave the candidates.
  -- 271 (#1738): so does an archived stream, by its type's AppendToArchived policy, after the seal. One probe per stream
  -- of idx_stream_lifecycle_closed, which holds only the sealed and the archived streams, answers both, so the common
  -- case (none of either, no terminal event) costs what 266's probe cost.
  IF v_candidate_ids IS NOT NULL THEN
    SELECT COALESCE(bool_or(l.sealed_at IS NOT NULL), false),
           COALESCE(bool_or(l.sealed_at IS NULL AND l.state = 'Archived'), false)
      INTO v_any_sealed, v_any_archived
    FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.stream_id = ANY(v_candidate_streams)
      AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
    IF v_any_sealed OR EXISTS (
         SELECT 1 FROM unnest(v_candidate_flags) AS f(bits)
         WHERE COALESCE(f.bits, 0) & c_flag_terminal <> 0
           AND COALESCE(f.bits, 0) & c_flags_off_the_ledger = 0) THEN
      v_sealed_refused := __SCHEMA__._wh_refuse_sealed_stream_inbox_events(
        v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
      IF cardinality(v_sealed_refused) > 0 THEN
        SELECT array_agg(k.id ORDER BY k.ord), array_agg(k.sid ORDER BY k.ord), array_agg(k.bits ORDER BY k.ord),
               array_agg(k.type_name ORDER BY k.ord)
          INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
        FROM unnest(v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types)
               WITH ORDINALITY AS k(id, sid, bits, type_name, ord)
        WHERE k.id <> ALL(v_sealed_refused);
      END IF;
    END IF;
    IF v_any_archived AND v_candidate_ids IS NOT NULL THEN
      v_archived_refused := __SCHEMA__._wh_refuse_archived_stream_inbox_events(
        v_candidate_ids, v_candidate_streams, p_instance_id, p_now);
      IF cardinality(v_archived_refused) > 0 THEN
        SELECT array_agg(k.id ORDER BY k.ord), array_agg(k.sid ORDER BY k.ord), array_agg(k.bits ORDER BY k.ord),
               array_agg(k.type_name ORDER BY k.ord)
          INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
        FROM unnest(v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types)
               WITH ORDINALITY AS k(id, sid, bits, type_name, ord)
        WHERE k.id <> ALL(v_archived_refused);
      END IF;
      -- 296 (#1734): the archived streams the refusal left alone start their new generation.
      IF v_candidate_ids IS NOT NULL THEN
        PERFORM __SCHEMA__._wh_start_new_generations(v_candidate_streams);
      END IF;
    END IF;
  END IF;
  IF v_candidate_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refuse_mixed_stream_inbox_events(
      v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
  END IF;

  -- 285 (#1725): the stored-metadata settings, read once for the whole batch.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;
  -- 537 (#2323): the trim gate, read once for the whole batch, beside the settings.
  v_trim := __SCHEMA__._wh_metadata_trim_on();

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   - are events (is_event = true)
  --   - have a stream_id
  --   - aren't yet in wh_event_store (idempotent, ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- 574 (#2551): versions follow the candidates' receive order (their position in v_candidate_ids), never the id.
  -- Migration 061: i.flags carried into wh_event_store.flags (was dropped here previously).
  WITH inbox_events AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.flags,
      i.source_service_id,
      i.source_commit_sequence,
      ist.received_at,
      -- 574 (#2551): the event's place in receive order, and its receive-order key (arrival, origin, origin position;
      -- an unpositioned event sorts after its origin's positioned events of the same arrival, then by id, the one
      -- fallback where nothing positional exists), which the hold-back below compares with a row this pass does not hold.
      cand.position AS receive_position,
      CASE WHEN i.source_service_id IS NULL OR i.source_service_id = __EMPTY_UUID__::uuid
                OR i.source_service_id = v_local_service_id
           THEN __EMPTY_UUID__::uuid ELSE i.source_service_id END AS receive_origin,
      COALESCE(NULLIF(i.source_commit_sequence, 0), 9223372036854775807) AS receive_seq
    -- 162: this pass needs the message body, so both tables. The LEASE table drives the join, so
    -- selection happens on the narrow table and the wide row is fetched only for the rows that
    -- survive it, which is the opposite of scanning the wide row to apply the same predicate.
    -- 574: a row the candidates do not list (committed by a concurrent claim after they were read) is left for the
    -- next pass, unstamped, as a row that pass would have missed.
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    JOIN unnest(v_candidate_ids) WITH ORDINALITY AS cand(message_id, position) ON cand.message_id = ist.message_id
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL  -- 158: see the lock pass above
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
  ),
  -- 230 (#1607): a received stream is stored in its receive order, however many claims carry it. A claim numbers
  -- the rows it holds after the stream's tail, so a claim that holds a stream's later events and reaches this pass
  -- first would number them ahead of the head another claim leased earlier and has not stored yet. The head is visible
  -- here as a pending event of the stream that this pass does not hold: not yet in the event store, and not leased to
  -- this instance under a live lease (a peer's lease, an expired one, none, or a lease a concurrent claim has not
  -- committed). A row of this batch waits when such an event comes before it, and so does every row after it in the
  -- batch's receive order: it is not stored and not stamped, so the next claim re-reads it, and fetch_inbox_batch does
  -- not hand it to the dispatcher before it is stored. Rows before it are stored now, so the head never waits.
  --
  -- 574 (#2551): "comes before" is the receive-order key (arrival, origin, origin position, then the id for an
  -- unpositioned event), never the id alone. The key is a strict total order, so two claims of one stream can never
  -- each wait for the other. An event of another origin that arrived after this row is never its head: between origins
  -- the arrival order stands.
  --
  -- One probe per stream, over idx_inbox_state_stream_order (stream_id, received_at, message_id), bounded by the
  -- batch: only rows received no later than the batch's newest row can hold it back. In a stream's FIFO backlog that
  -- range is the batch and the rows before it, never the rows queued behind it, and the lease test is answered from the
  -- index, so only a row this pass does not hold reads the heap (and its message row, for the origin). No index is
  -- added: wh_inbox_state's index count is a gated design property (InboxStateWriteCostScenarioTests).
  batch_streams AS (
    SELECT ie.stream_id, MAX(ie.received_at) AS newest_received_at
    FROM inbox_events ie
    GROUP BY ie.stream_id
  ),
  held_back_from AS (
    SELECT s.stream_id, h.received_at, h.origin, h.seq, h.message_id
    FROM batch_streams s
    CROSS JOIN LATERAL (
      SELECT b.received_at,
             CASE WHEN bi.source_service_id IS NULL OR bi.source_service_id = __EMPTY_UUID__::uuid
                       OR bi.source_service_id = v_local_service_id
                  THEN __EMPTY_UUID__::uuid ELSE bi.source_service_id END AS origin,
             COALESCE(NULLIF(bi.source_commit_sequence, 0), 9223372036854775807) AS seq,
             b.message_id
      FROM __SCHEMA__.wh_inbox_state b
      JOIN __SCHEMA__.wh_inbox bi ON bi.message_id = b.message_id
      WHERE b.stream_id = s.stream_id
        AND b.processed_at IS NULL
        AND b.received_at <= s.newest_received_at
        AND NOT COALESCE(b.instance_id = p_instance_id AND b.lease_expiry > p_now, false)
        AND b.is_event = true
        AND b.chain_emitted_at IS NULL
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = b.message_id
        )
      ORDER BY b.received_at, 2, 3, b.message_id
      LIMIT 1
    ) AS h
  ),
  -- Per stream, the first row (in receive order) that the unheld head comes before; it and every row after it wait.
  first_waiting AS (
    SELECT ie.stream_id, MIN(ie.receive_position) AS position
    FROM inbox_events ie
    JOIN held_back_from h ON h.stream_id = ie.stream_id
    WHERE (h.received_at, h.origin, h.seq, h.message_id) < (ie.received_at, ie.receive_origin, ie.receive_seq, ie.message_id)
    GROUP BY ie.stream_id
  ),
  storable_events AS (
    SELECT ie.*,
           ROW_NUMBER() OVER (PARTITION BY ie.stream_id ORDER BY ie.receive_position) AS row_num
    FROM inbox_events ie
    LEFT JOIN first_waiting w ON w.stream_id = ie.stream_id
    WHERE w.position IS NULL OR ie.receive_position < w.position
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, see _emit_event_store_chain above for the rationale.
  -- Migration 072: materialize the extracted payload + built metadata ONCE (see outbox fn above).
  computed_full AS (
    SELECT
      ie.message_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(ie.message_type) AS event_type,
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload') AS body_data,
      COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb) AS hops,
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN ie.metadata IS NOT NULL
             AND jsonb_typeof(ie.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((ie.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS full_meta,
      ie.scope,
      ie.row_num,
      ie.flags,
      -- 295 (#1729): a superseding event's key, from the metadata the dispatch or receive path stamped it in. Only
      -- a row carrying the flag gets one, so a stray 'sk' on another event is never read.
      CASE WHEN COALESCE(ie.flags, 0) & c_flag_superseding <> 0 THEN ie.metadata::jsonb ->> 'sk' END AS supersede_key,
      -- 352 (#2124): and the superseding group the receive path stamped beside the key, on a flagged row only.
      CASE WHEN COALESCE(ie.flags, 0) & c_flag_superseding <> 0 THEN ie.metadata::jsonb ->> 'sg' END AS supersede_group,
      -- Migration 087: normalize the received origin, self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = __EMPTY_UUID__::uuid
             OR ie.source_service_id = v_local_service_id
        THEN NULL
        ELSE ie.source_service_id
      END AS origin_service_id,
      NULLIF(ie.source_commit_sequence, 0) AS origin_commit_sequence
    FROM storable_events ie
  ),
  -- 285 (#1725): each event's route id, and the metadata its row keeps: the full form (plus the route id) under debug
  -- mode, under the Full form or for a listed type, otherwise the slim form. Pure CPU on values already in hand.
  -- Materialized, so each event's route id is computed once however many times the next step reads it.
  computed_routes AS MATERIALIZED (
    SELECT cf.*, __SCHEMA__._wh_route_id(split_part(cf.event_type, ',', 1), cf.hops) AS route_id
    FROM computed_full cf
  ),
  computed AS (
    SELECT cr.*,
      CASE
        WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(cr.event_type, ',', 1), v_full_for)
        THEN cr.full_meta || jsonb_build_object('rid', cr.route_id)
        -- 537 (#2323): once the trim gate is open, less what the row holds in its own columns, its event id and scope.
        WHEN v_trim THEN __SCHEMA__._wh_trim_row_values(
               __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id), cr.message_id, cr.scope::jsonb)
        ELSE __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id)
      END AS body_meta
    FROM computed_routes cr
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, origin_service_id, origin_commit_sequence, supersede_key, supersede_group
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      -- 296 (#1734): see _emit_event_store_chain.
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id),
               CASE WHEN v_any_archived THEN
                 (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = c.stream_id)
               END,
               0) + c.row_num,
      p_now,
      c.flags,
      -- Migration 087: stamp the origin identity the transport delivered (046 columns were never
      -- populated by the emit chain before this, consumer-side origin-keyed verification needs them).
      c.origin_service_id,
      CASE WHEN c.origin_service_id IS NULL THEN NULL ELSE c.origin_commit_sequence END,
      c.supersede_key,
      c.supersede_group
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version, supersede_key, flags
  ),
  -- Migration 077 (full split): offload EVERY body (see outbox fn above).
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- 285 (#1725): under the Linked form, the full form is appended to the staging table for maintenance to distill into
  -- routes and traces. Append-only: the key is new for every stored event, so there is nothing to check.
  staged_metadata AS (
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    SELECT c.message_id, p_now, c.route_id, split_part(c.event_type, ',', 1), c.full_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE v_write_form = 'Linked'
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    -- 336 (#2106): every lane, through the one fold helper. Referenced by the statement's final SELECT, so it
    -- always runs (a CTE that writes nothing runs only when read).
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(c.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(c.scope::jsonb ->> 't', '')), array_agg(c.event_type),
      array_agg(c.stream_id),
      -- 461 (#2364): a superseding copy counts as its key's slot, once: only when the store holds no copy of its key yet,
      -- and only the first such copy of this batch. The slot is passed already: the rows this statement stores are not
      -- visible to it, so the helper could not read them back.
      array_agg(CASE WHEN c.supersede_key IS NULL THEN c.message_id
                     ELSE __SCHEMA__._wh_slot_id(c.stream_id, COALESCE(c.supersede_group, c.event_type), c.supersede_key) END),
      'fold', p_now, TRUE) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE __DIGEST_ELIGIBLE_V2__(c)
      AND (c.supersede_key IS NULL
           OR (NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_event_store o
                 WHERE o.stream_id = c.stream_id
                   AND COALESCE(o.supersede_group, o.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND o.supersede_key = c.supersede_key)
               AND NOT EXISTS (
                 SELECT 1 FROM computed c2
                 JOIN stored_events se2 ON se2.event_id = c2.message_id
                 WHERE c2.stream_id = c.stream_id
                   AND COALESCE(c2.supersede_group, c2.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND c2.supersede_key = c.supersede_key
                   AND se2.version < se.version)))
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  -- 266 (#1733): a terminal event among them seals the stream in this statement (see _emit_event_store_chain).
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0)),
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN p_now END,
      (array_agg(__SCHEMA__._wh_terminal_seal_reason(c.aggregate_type, c.message_id) ORDER BY se.version)
         FILTER (WHERE COALESCE(c.flags, 0) & c_flag_terminal <> 0))[1]
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at)
  )
  SELECT array_agg(event_id), array_agg(event_id) FILTER (WHERE supersede_key IS NOT NULL),
         -- 367: whether the collective routing below has anything to route, from the rows this statement stored.
         COALESCE(bool_or((COALESCE(flags, 0) & c_flag_collective) = c_flag_collective), false)
    INTO v_stored_event_ids, v_superseding_ids, v_any_collective
  FROM stored_events CROSS JOIN digest_folds;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 295 (#1729): a stored superseding event queues the older events of its stream, type and key for the reaper. A
  -- separate statement, so it sees every event this batch stored as well as the ones before it. Skipped outright when
  -- the batch stored none, so an ordinary batch pays nothing; otherwise one probe of idx_event_store_superseding per
  -- superseding event.
  IF v_superseding_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(v_superseding_ids, p_now);
  END IF;

  -- 158: stamp the held rows the chain is finished with, so the next poll reads none of them.
  -- "Finished" is the row's event being in the event store: either this pass inserted it, or it was
  -- already there and the pass above excluded the row for exactly that reason. A row whose insert hit
  -- ON CONFLICT DO NOTHING has no event-store row, so it stays unstamped and the next poll re-attempts
  -- it with a fresh version snapshot, which is the contract that conflict clause states. The stamp runs
  -- before the nothing-was-stored return below, because that return is the steady state of a busy
  -- instance (every held event already chained) and the case the stamp exists for. The stamp and the
  -- inserts share this transaction, so a failure leaves both undone.
  -- 162: the stamp is the write this migration helps most. It used to rewrite a row carrying the
  -- whole message body across twenty-four indexes to set one timestamp, measured at about 41 blocks
  -- per row stamped with only 30 of those spent finding the rows. It now writes the narrow table,
  -- and the predicate that finds them is local to it.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET chain_emitted_at = p_now,
      -- 551 (#1630): a row that waited for its stream's head and is now stored no longer waits.
      failure_reason = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN c_reason_unknown
                            ELSE ist.failure_reason END,
      error = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN NULL ELSE ist.error END
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );

  -- 411 (#2120): a row this claim leased (its lease is the one this claim set) that the pass above held back keeps the
  -- attempt the claim counted for it. The claim charges an attempt to every row it acquires (claim_orphaned_inbox), but
  -- a held-back row is not stored, so the re-offer never hands it to the drain (349) and the attempt bought nothing:
  -- waiting behind an earlier event its stream needs first is not failing. Only rows this claim acquired are refunded; a
  -- row held back since an earlier claim was refunded by that claim's pass.
  UPDATE __SCHEMA__.wh_inbox_state ist
  -- 551 (#1630): and shows that it waits for its stream's head (MessageFailureReason.AwaitingStreamHead), so a waiting row
  -- is told apart from a fresh one and from a failing one. The error names the stream and when the wait began; a row
  -- already waiting keeps the time it began, however many claims re-lease it. The state replaces the claim's lapsed-lease
  -- record (an unstored row was never dispatched, so a lease that ran out on it was a wait, not a dispatch cut off), and
  -- nothing else a row recorded: that failure is the better diagnostic. Once a row shows it waits, the claim's
  -- lapsed-lease branch (guarded on error IS NULL) leaves it as it is. The stamps above clear the state when the row is
  -- stored. The same statement as the refund, on the same rows: no extra read.
  SET attempts = GREATEST(ist.attempts - 1, 0),
      failure_reason = CASE
        WHEN ist.error IS NULL OR ist.failure_reason IN (c_reason_lease_expired, c_reason_awaiting_stream_head)
        THEN c_reason_awaiting_stream_head
        ELSE ist.failure_reason
      END,
      error = CASE
        WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN ist.error
        WHEN ist.error IS NULL OR ist.failure_reason = c_reason_lease_expired
        THEN 'Waiting for the stream head since ' || p_now::text || ': an earlier event of stream '
             || ist.stream_id::text || ' is not yet stored and is not held by this instance, so this event is held back,'
             || ' unstored and undispatched, until it is. The wait is not charged an attempt.'
        ELSE ist.error
      END
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry = p_lease_expiry
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Auto-create perspective_events for the newly-stored events.
  -- Phase H step 6 slice 2: populate partition_number for symmetric load balancing.
  -- Slice 26.14: route the lease through wh_active_streams (live owner wins; fall back to
  -- commit instance when no live owner). Mirror of the outbox-side change in
  -- _emit_event_store_chain.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- 100: reconcile self-declares the rewind. Any of THIS invocation's fresh work items that
  -- slots BELOW its cursor's last-applied event is a straggler by construction: its fresh local
  -- commit_sequence sits above the cursor and hides it from the runtime inversion detector forever.
  -- Flag the cursor RewindRequired with the earliest straggler as trigger (min-merge with any
  -- existing trigger); the worker's existing rewind routing replays the stream through the
  -- corrected order.
  -- 574 (#2551): "below the cursor" is the replay's order, never the id. A stored event is appended after everything
  -- the cursor applied, and a replay moves it only within its own origin's slots (#2257), so it is a straggler exactly
  -- when its origin had already delivered a later event the cursor applied: the origin's latest event at or below the
  -- cursor's version comes after it in the origin's order (origin sequence; when either carries none, the id, the
  -- replay's rule for an unpositioned event, #2379). A local event, or another origin's, keeps its place and is never
  -- one. The earliest straggler, and the earlier of it and an existing trigger, are by stream version; the worker judges
  -- the replay floor from the trigger in the replay's order (ReplayFloor).
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN s.straggler_event_id
        WHEN s.straggler_version < COALESCE(
               (SELECT t.version FROM __SCHEMA__.wh_event_store t WHERE t.event_id = pc.rewind_trigger_event_id),
               9223372036854775807)
          THEN s.straggler_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
  FROM (
    SELECT pe.stream_id, pe.perspective_name,
           (array_agg(pe.event_id ORDER BY se.version))[1] AS straggler_event_id,
           MIN(se.version) AS straggler_version
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_perspective_cursors c
      ON c.stream_id = pe.stream_id AND c.perspective_name = pe.perspective_name
    JOIN __SCHEMA__.wh_event_store se ON se.event_id = pe.event_id
    -- The cursor's position: the stream version of its last applied event (a bound on the stream, never a judgment of
    -- which copy of a superseding key is newer, which _wh_supersedes alone decides).
    CROSS JOIN LATERAL (
      SELECT ce.version AS through_version FROM __SCHEMA__.wh_event_store ce WHERE ce.event_id = c.last_event_id
    ) AS cursor_at
    CROSS JOIN LATERAL (
      -- The origin's latest event at or below the cursor's version: one backward probe of idx_event_store_stream.
      SELECT a.event_id, a.origin_commit_sequence
      FROM __SCHEMA__.wh_event_store a
      WHERE a.stream_id = se.stream_id
        AND a.origin_service_id = se.origin_service_id
        AND a.version <= cursor_at.through_version
      ORDER BY a.version DESC
      LIMIT 1
    ) AS applied
    WHERE pe.event_id = ANY(v_stored_event_ids)
      AND pe.processed_at IS NULL
      AND c.last_event_id IS NOT NULL
      AND se.origin_service_id IS NOT NULL
      AND CASE WHEN se.origin_commit_sequence IS NOT NULL AND applied.origin_commit_sequence IS NOT NULL
               THEN se.origin_commit_sequence < applied.origin_commit_sequence
               ELSE se.event_id < applied.event_id
          END
    GROUP BY pe.stream_id, pe.perspective_name
  ) s
  WHERE pc.stream_id = s.stream_id AND pc.perspective_name = s.perspective_name;

  -- 367: only when an event this pass stored is collective; v_any_collective is read from the stored rows themselves.
  IF v_any_collective THEN
    -- Migration 061: collective-event routing (inbox path). See _emit_event_store_chain above.
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id,
      partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
    )
    SELECT DISTINCT
      gen_random_uuid(),
      es.stream_id,
      c_collective_sink,
      es.event_id,
      __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
      1,                  -- Stored flag
      0,
      p_now,
      -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
      -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
      CASE WHEN p_lease_expiry IS NULL THEN NULL
           WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
           ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
      CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
      COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
    LEFT JOIN LATERAL (
      SELECT ast.assigned_instance_id
      FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = es.stream_id
        AND ast.assigned_instance_id IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_service_instances si
          WHERE si.instance_id = ast.assigned_instance_id
        )
        -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
        AND NOT v_map
    ) owner ON TRUE
    WHERE es.event_id = ANY(v_stored_event_ids)
      AND (es.flags & c_flag_collective) = c_flag_collective
      -- 391 (#2145): only a type a handler on this service applies, once every service registered here gates.
      AND __SCHEMA__._wh_collective_row_wanted(es.event_type)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
        WHERE pe_check.stream_id = es.stream_id
          AND pe_check.perspective_name = c_collective_sink
          AND pe_check.event_id = es.event_id
      )
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;
  END IF;

  -- Slice 26.4: wake the commit-order stamper. See _emit_event_store_chain above for
  -- the rationale and dedup semantics. Inbox backfill is generally a smaller fan-in
  -- than outbox-emit, but the NOTIFY is just as cheap and keeps the stamper hot path
  -- responsive whether events arrive locally or via transport.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

-- ── _wh_touch_sealed_epochs_on_insert: last word 592, copied whole; the eligibility rule is version 2 ──
-- The touches of an insert statement, from its transition table: each eligible row's own position, and the slot a
-- superseding row inherited. Rows without a position (an outbox emit's, a received event held unpositioned) touch
-- nothing.
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ABackfilledEvent_StoredByTheInboxChainInsideASealedEpoch_RefoldsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ADirectInsert_IntoASealedEpochOfAReceivedLane_RefoldsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnIneligibleEvent_IntoASealedEpoch_IsNotRecordedAsATouchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_insert() RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  PERFORM __SCHEMA__._wh_touch_sealed_epochs(array_agg(x.lane), array_agg(x.pos))
  FROM (
    SELECT COALESCE(n.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           CASE WHEN n.origin_service_id IS NULL THEN n.commit_sequence ELSE n.origin_commit_sequence END AS pos
    FROM new_rows n
    WHERE __DIGEST_ELIGIBLE_V2__(n)
    UNION ALL
    SELECT COALESCE(n.origin_service_id, __EMPTY_UUID__::uuid), n.supersede_slot_at
    FROM new_rows n
    WHERE n.supersede_slot_at IS NOT NULL AND __DIGEST_ELIGIBLE_V2__(n)
  ) x
  WHERE x.pos IS NOT NULL
  HAVING count(*) > 0;
  RETURN NULL;
END;
$$;

-- ── _wh_touch_sealed_epochs_on_update: last word 592, copied whole; the eligibility rule is version 2 ──
-- The touches of a row whose origin or origin position changed: the epoch it left and the epoch it joined.
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AReattribution_MovingAPositionedEventBetweenLanes_RefoldsTheSealedEpochsOfBothAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:APositionWrittenByAnUpdate_IntoASealedEpoch_RefoldsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnEventMovedIntoASealedLocalEpoch_RefoldsIt_AndAdvancesTheOriginGenerationAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_update() RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  PERFORM __SCHEMA__._wh_touch_sealed_epochs(
    ARRAY[COALESCE(OLD.origin_service_id, __EMPTY_UUID__::uuid), COALESCE(OLD.origin_service_id, __EMPTY_UUID__::uuid),
          COALESCE(NEW.origin_service_id, __EMPTY_UUID__::uuid), COALESCE(NEW.origin_service_id, __EMPTY_UUID__::uuid)],
    ARRAY[CASE WHEN __DIGEST_ELIGIBLE_V2__(OLD) THEN
            CASE WHEN OLD.origin_service_id IS NULL THEN OLD.commit_sequence ELSE OLD.origin_commit_sequence END END,
          CASE WHEN __DIGEST_ELIGIBLE_V2__(OLD) THEN OLD.supersede_slot_at END,
          CASE WHEN __DIGEST_ELIGIBLE_V2__(NEW) THEN
            CASE WHEN NEW.origin_service_id IS NULL THEN NEW.commit_sequence ELSE NEW.origin_commit_sequence END END,
          CASE WHEN __DIGEST_ELIGIBLE_V2__(NEW) THEN NEW.supersede_slot_at END]);
  RETURN NULL;
END;
$$;

-- ── wh_integrity_reattribute_origins: last word 594, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#re-attribution</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_ARowAnotherServiceOwns_TakesTheOwnerAsOrigin_AndItsDigestMovesLanesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_AFirstHopWithoutAServiceIdentity_FollowsOwnershipAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_AFirstHopNamingAThirdService_IsLeftAndCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_ATypeWithNoKnownOwner_IsCountedAsUnattributedAndLeftAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_AnEphemeralRow_TakesItsOwnerWithoutTouchingDigestsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_ReArmsTheOwnersLedgerBucketsTheRowsLandIn_AndNoOthersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_IntoASealedLocalEpoch_RefoldsIt_AndAdvancesTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_FromAStaleCursor_MovesNothingAndReportsNotAdvancedAsync</tests>
-- <docs>resilience/stream-integrity#events-written-here-stay-local</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Reattribute_AnEventWrittenHere_WhoseSlimRowRecordsNoService_StaysLocalAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Reattribute_AnEventWrittenHere_WhoseStagedFormNamesThisService_IsLeftAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Reattribute_AnEventWrittenHere_WhoseRouteNamesThisService_IsLeftAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Reattribute_HistoryWhoseRouteNamesTheOwner_TakesTheOwnerAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_reattribute_origins(
  p_after BIGINT,
  p_through BIGINT,
  p_event_types TEXT[],
  p_owner_ids UUID[],
  p_owner_names TEXT[]
) RETURNS TABLE(reattributed_count INTEGER, hop_disagreement_count INTEGER, unattributed_count INTEGER, advanced BOOLEAN) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_field_route_id CONSTANT TEXT := 'rid';    -- 285: the route id every route-stamping write path adds to the row
  c_unknown_service CONSTANT TEXT := 'unknown';  -- ServiceInstanceInfo.Unknown, lower-cased: no service identity
  v_moved INTEGER := 0;
  v_disagreed INTEGER := 0;
  v_unattributed INTEGER := 0;
  v_ids UUID[];
  v_streams UUID[];
  v_tenants TEXT[];
  v_types TEXT[];
  v_owners UUID[];
  v_sequences BIGINT[];
  v_digested BOOLEAN[];
  v_min BIGINT;
  v_max BIGINT;
  v_width BIGINT;
BEGIN
  IF cardinality(p_event_types) IS DISTINCT FROM cardinality(p_owner_ids)
     OR cardinality(p_event_types) IS DISTINCT FROM cardinality(p_owner_names) THEN
    RAISE EXCEPTION 'wh_integrity_reattribute_origins: % event types, % owner ids and % owner names must pair by position',
      cardinality(p_event_types), cardinality(p_owner_ids), cardinality(p_owner_names);
  END IF;

  -- The cursor compare-and-swap first: it also serializes overlapping passes on the state row.
  UPDATE __SCHEMA__.wh_integrity_reattribution r
  SET through_commit_sequence = p_through, updated_at = now()
  WHERE r.singleton AND r.through_commit_sequence = p_after AND p_through >= p_after;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 0, 0, 0, false;
    RETURN;
  END IF;

  -- The batch's candidates, held in parallel arrays rather than a temp table: no catalog churn per call. Only the rows
  -- that move are kept; the left ones are only counted.
  SELECT
    COUNT(*) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here),
    COUNT(*) FILTER (WHERE c.owner_id IS NOT NULL AND c.disagreed),
    COUNT(*) FILTER (WHERE c.owner_id IS NULL),
    array_agg(c.event_id) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here),
    array_agg(c.stream_id) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here),
    array_agg(c.tenant_scope) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here),
    array_agg(c.event_type) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here),
    array_agg(c.owner_id) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here),
    array_agg(c.local_sequence) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here),
    array_agg(c.digested) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed AND NOT c.written_here)
    INTO v_moved, v_disagreed, v_unattributed, v_ids, v_streams, v_tenants, v_types, v_owners, v_sequences, v_digested
  FROM (
    SELECT
      es.event_id,
      es.stream_id,
      COALESCE(es.scope ->> 't', '') AS tenant_scope,
      es.event_type,
      t.owner_id,
      es.commit_sequence AS local_sequence,
      COALESCE(fh.service IS NOT NULL AND fh.service <> c_unknown_service
               AND fh.service <> lower(btrim(t.owner_name)), false) AS disagreed,
      -- 594 (#2573): a row written by a route-stamping path whose first hop records no service anywhere is this
      -- service's own: left in the local lane, as a row of an owned type is.
      COALESCE(fh.service IS NULL AND fh.routed, false) AS written_here,
      __DIGEST_ELIGIBLE_V2__(es) AS digested
    FROM __SCHEMA__.wh_event_store es
    JOIN unnest(p_event_types, p_owner_ids, p_owner_names) AS t(event_type, owner_id, owner_name)
      ON t.event_type = es.event_type
    -- The first hop's service, read only for a row with a known owner to cross-check: a primary-key probe of the body.
    LEFT JOIN LATERAL (
      -- 594 (#2573): the first hop's service wherever it is kept (the row, its staged full form, its route), and whether
      -- the row was written by a path that stamps a route id: such a row recording no service was written here.
      SELECT __SCHEMA__._wh_first_hop_service(eb.event_id, eb.metadata) AS service,
             eb.metadata ? c_field_route_id AS routed
      FROM (SELECT eb0.event_id, eb0.metadata
            FROM __SCHEMA__.wh_event_body eb0
            WHERE eb0.event_id = es.event_id AND t.owner_id IS NOT NULL) eb
    ) fh ON true
    WHERE es.commit_sequence > p_after
      AND es.commit_sequence <= p_through
      AND es.origin_service_id IS NULL
  ) c;

  IF v_moved > 0 THEN
    -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
    PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
    FROM (SELECT DISTINCT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY 1) s;

    UPDATE __SCHEMA__.wh_event_store es
    SET origin_service_id = m.owner_id
    FROM unnest(v_ids, v_owners) AS m(event_id, owner_id)
    WHERE es.event_id = m.event_id;

    -- Digests: XOR is self-inverse, so a moved event's hash leaves the local bucket and joins its owner's.
    -- 336 (#2106): every lane leaves the local bucket and joins the owner's through the one fold helper.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(__EMPTY_UUID__::uuid), array_agg(m.tenant_scope), array_agg(m.event_type), array_agg(m.stream_id),
      array_agg(m.event_id), 'unfold', now())
    FROM unnest(v_ids, v_streams, v_tenants, v_types, v_digested) AS m(event_id, stream_id, tenant_scope, event_type, digested)
    WHERE m.digested;
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.origin_service_id = __EMPTY_UUID__::uuid
      AND d.event_count <= 0
      AND d.stream_id = ANY(v_streams);

    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(m.owner_id), array_agg(m.tenant_scope), array_agg(m.event_type), array_agg(m.stream_id),
      array_agg(m.event_id), 'fold', now())
    FROM unnest(v_ids, v_streams, v_tenants, v_types, v_owners, v_digested)
      AS m(event_id, stream_id, tenant_scope, event_type, owner_id, digested)
    WHERE m.digested;

    -- Sealed local epochs over the moved events, by local sequence. The owner's lane has no sequence for them.
    SELECT MIN(m.local_sequence), MAX(m.local_sequence) INTO v_min, v_max
    FROM unnest(v_sequences, v_digested) AS m(local_sequence, digested)
    WHERE m.digested AND m.local_sequence IS NOT NULL;
    SELECT f.epoch_width INTO v_width
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
    WHERE f.origin_service_id = __EMPTY_UUID__::uuid;
    IF v_min IS NOT NULL AND v_width IS NOT NULL
       AND __SCHEMA__.refold_digest_epochs(__EMPTY_UUID__::uuid, v_min / v_width, v_max / v_width) > 0 THEN
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;

    -- The owner's ledger buckets the moved rows land in changed on this side: their ladder starts afresh.
    UPDATE __SCHEMA__.wh_integrity_ledger l
    SET unhealable_at = NULL, exhausted_at = NULL, answer_pending_until = NULL, answered_at = NULL, repair_attempts = 0
    FROM (
      SELECT m.owner_id, m.tenant_scope, m.event_type, m.stream_id
      FROM unnest(v_owners, v_tenants, v_types, v_streams) AS m(owner_id, tenant_scope, event_type, stream_id)
      UNION
      SELECT m.owner_id, m.tenant_scope, m.event_type, __EMPTY_UUID__::uuid
      FROM unnest(v_owners, v_tenants, v_types) AS m(owner_id, tenant_scope, event_type)
    ) k(owner_id, tenant_scope, event_type, stream_id)
    WHERE l.origin_service_id = k.owner_id AND l.tenant_scope = k.tenant_scope
      AND l.event_type = k.event_type AND l.stream_id = k.stream_id;
  END IF;

  RETURN QUERY SELECT v_moved, v_disagreed, v_unattributed, true;
END;
$$ LANGUAGE plpgsql;

-- ── wh_reconcile_redelivered_bundle_counting_satisfied: last word 594, copied whole; the eligibility rule is version 2 ──
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryReconcileEquivalenceSqlTests.cs:ReconcileBundleCountingSatisfied_GivesWhatTheWorkTableVersionGave_WithNothingSatisfiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerCallFunctionsCreateNoTemporaryTableTests.cs:NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync</tests>
-- <docs>resilience/stream-integrity#events-written-here-stay-local</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Adopt_AnEventWrittenHere_WhoseSlimRowRecordsNoService_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Adopt_AnEventWrittenHere_WhoseStagedFormNamesThisService_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Adopt_HistoryWhoseRouteNamesTheBundlesOrigin_AdoptsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundle_counting_satisfied(
  p_origin_service_id UUID,
  p_origin_service_name TEXT,
  p_event_ids UUID[],
  p_commit_sequences BIGINT[]
) RETURNS TABLE(stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER, adopted_count INTEGER,
                refused_truncated_count INTEGER, refused_foreign_origin_count INTEGER,
                superseded_satisfied_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_field_route_id CONSTANT TEXT := 'rid';    -- 285: the route id every route-stamping write path adds to the row
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
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundle_counting_satisfied: % event ids but % commit sequences',
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
      COALESCE((fh.service IS NOT NULL AND fh.service <> c_unknown_service AND fh.service <> v_origin_name)
               OR (fh.service IS NULL AND fh.routed), false) AS refused,
      es.commit_sequence AS local_sequence,
      COALESCE(es.origin_commit_sequence, NULLIF(m.seq, 0)) AS origin_sequence,
      __DIGEST_ELIGIBLE_V2__(es) AS digested
    FROM (SELECT DISTINCT ON (u.eid) u.eid, u.seq FROM unnest(p_event_ids, p_commit_sequences) AS u(eid, seq) ORDER BY u.eid) m
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
    -- The first hop's service, read only for an event the bundle would give an origin, and only when there is a
    -- name to compare it with: the one-time catch-up, never an event that already carries its origin.
    LEFT JOIN LATERAL (
      -- 594 (#2573): the first hop's service wherever it is kept (the row, its staged full form, its route), and whether
      -- the row was written by a path that stamps a route id: such a row recording no service was written here.
      SELECT __SCHEMA__._wh_first_hop_service(eb.event_id, eb.metadata) AS service,
             eb.metadata ? c_field_route_id AS routed
      FROM (SELECT eb0.event_id, eb0.metadata
            FROM __SCHEMA__.wh_event_body eb0
            WHERE eb0.event_id = es.event_id
              AND v_adoptable AND v_origin_name IS NOT NULL AND es.origin_service_id IS NULL) eb
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
  SELECT b.sid, b.tenant, b.etype, SUM(b.present)::int, SUM(b.adopted)::int, SUM(b.truncated)::int, SUM(b.foreign_origin)::int,
         SUM(b.superseded)::int
  FROM (
    SELECT r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype, 1 AS present,
           r.adopted::int AS adopted, 0 AS truncated, r.refused::int AS foreign_origin, 0 AS superseded
    FROM unnest(v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_left_local, v_w_local_seqs, v_w_origin_seqs,
                  v_w_adopted, v_w_refused, v_w_digested)
           AS r(event_id, stream_id, tenant_scope, event_type, left_local_lane, local_sequence, origin_sequence,
                adopted, refused, digested)
    UNION ALL
    -- 252: a child store_inbox_messages refused to restore, keyed as every digest fold keys it (the stream, the
    -- scope's tenant, the emit chain's normalized type).
    SELECT i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type), 0, 0, 1, 0, 0
    FROM __SCHEMA__.wh_inbox i
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE i.message_id = ANY(p_event_ids)
      AND ist.error = __REPAIR_REFUSED_TRUNCATED__
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id)
    UNION ALL
    -- 454 (#2350): a child a newer held copy of its key supersedes is satisfied: present, and counted apart.
    SELECT i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type), 1, 0, 0, 0, 1
    FROM __SCHEMA__.wh_inbox i
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE i.message_id = ANY(p_event_ids)
      AND ist.error = __REPAIR_SATISFIED_SUPERSEDED__
  ) b
  GROUP BY b.sid, b.tenant, b.etype
  ORDER BY b.sid, b.tenant, b.etype;
END;
$$ LANGUAGE plpgsql;

-- ── wh_reconcile_redelivered_bundles_counting_satisfied: last word 594, copied whole; the eligibility rule is version 2 ──
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryReconcileEquivalenceSqlTests.cs:ReconcileBundlesCountingSatisfied_GivesWhatTheWorkTableVersionGave_WithNothingSatisfiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerCallFunctionsCreateNoTemporaryTableTests.cs:NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync</tests>
-- <docs>resilience/stream-integrity#events-written-here-stay-local</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Batch_AnEventWrittenHere_WhoseSlimRowRecordsNoService_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventsWrittenHereStayLocalSqlTests.cs:Batch_HistoryWhoseStagedFormNamesTheBundlesOrigin_AdoptsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reconcile_redelivered_bundles_counting_satisfied(
  p_bundle_origins   UUID[],     -- per bundle, by position (1-based): the origin it was published by
  p_bundle_names     TEXT[],     -- per bundle: the origin's service name from its first hop, or NULL
  p_event_bundles    INTEGER[],  -- per event: the position of its bundle in p_bundle_origins
  p_event_ids        UUID[],     -- per event: its id
  p_commit_sequences BIGINT[]    -- per event: its origin sequence, or NULL / 0 when the origin did not stamp one
) RETURNS TABLE(bundle INTEGER, stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER,
                adopted_count INTEGER, origins_filled_count INTEGER, refused_truncated_count INTEGER,
                refused_foreign_origin_count INTEGER,
                superseded_satisfied_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;        -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;    -- EventFlags.AtMostOnce (1 << 5), 214
  c_field_route_id CONSTANT TEXT := 'rid';      -- 285: the route id every route-stamping write path adds to the row
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
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundles_counting_satisfied: % bundle origins but % bundle names',
      cardinality(p_bundle_origins), cardinality(p_bundle_names);
  END IF;
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_event_bundles)
     OR cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_reconcile_redelivered_bundles_counting_satisfied: % event ids, % bundle positions and % commit sequences',
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
      COALESCE((fh.service IS NOT NULL AND fh.service <> c_unknown_service AND fh.service <> b.origin_name)
               OR (fh.service IS NULL AND fh.routed), false)
        AS refused,
      __DIGEST_ELIGIBLE_V2__(es) AS digested
    FROM events e
    JOIN bundles b ON b.pos = e.pos
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = e.eid
    -- The first hop's service, read only for an event the bundle would give an origin, and only when there is a
    -- name to compare it with: the one-time catch-up, never an event that already carries its origin.
    LEFT JOIN LATERAL (
      -- 594 (#2573): the first hop's service wherever it is kept (the row, its staged full form, its route), and whether
      -- the row was written by a path that stamps a route id: such a row recording no service was written here.
      SELECT __SCHEMA__._wh_first_hop_service(eb.event_id, eb.metadata) AS service,
             eb.metadata ? c_field_route_id AS routed
      FROM (SELECT eb0.event_id, eb0.metadata
            FROM __SCHEMA__.wh_event_body eb0
            WHERE eb0.event_id = es.event_id
              AND b.adoptable AND b.origin_name IS NOT NULL AND es.origin_service_id IS NULL) eb
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
         SUM(x.truncated)::int, SUM(x.foreign_origin)::int, SUM(x.superseded)::int
  FROM (
    SELECT r.bundle AS pos, r.stream_id AS sid, r.tenant_scope AS tenant, r.event_type AS etype, 1 AS present,
           r.adopted::int AS adopted, r.left_local_lane::int AS filled, 0 AS truncated, r.refused::int AS foreign_origin,
           0 AS superseded
    FROM unnest(v_w_bundles, v_w_ids, v_w_streams, v_w_tenants, v_w_types, v_w_origins, v_w_local_seqs,
                  v_w_origin_seqs, v_w_adopted, v_w_left_local, v_w_refused, v_w_digested)
           AS r(bundle, event_id, stream_id, tenant_scope, event_type, origin_service_id, local_sequence,
                origin_sequence, adopted, left_local_lane, refused, digested)
    UNION ALL
    -- 252: a child store_inbox_messages refused to restore, keyed as every digest fold keys it (the stream, the
    -- scope's tenant, the emit chain's normalized type), for each bundle that named it.
    SELECT u.pos::int, i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type),
           0, 0, 0, 1, 0, 0
    FROM (SELECT DISTINCT p.pos, p.eid FROM unnest(p_event_bundles, p_event_ids) AS p(pos, eid)) u
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = u.eid
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE ist.error = __REPAIR_REFUSED_TRUNCATED__
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id)
    UNION ALL
    -- 454 (#2350): a child a newer held copy of its key supersedes is satisfied: present, and counted apart.
    SELECT u.pos::int, i.stream_id, COALESCE(i.scope ->> 't', ''), __SCHEMA__.normalize_event_type(i.message_type),
           1, 0, 0, 0, 0, 1
    FROM (SELECT DISTINCT p.pos, p.eid FROM unnest(p_event_bundles, p_event_ids) AS p(pos, eid)) u
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = u.eid
    JOIN __SCHEMA__.wh_inbox_state ist ON ist.message_id = i.message_id
    WHERE ist.error = __REPAIR_SATISFIED_SUPERSEDED__
  ) x
  GROUP BY x.pos, x.sid, x.tenant, x.etype
  ORDER BY x.pos, x.sid, x.tenant, x.etype;
END;
$$ LANGUAGE plpgsql;

-- ── wh_restore_written_here_origins: last word 599, copied whole; the eligibility rule is version 2 ──
-- <docs>resilience/stream-integrity#events-written-here-returned</docs>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Restore_AReactionAndAReturn_GoBackToTheLocalLane_AndAReceivedEventStaysAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Restore_CleansTheBookkeepingTheRowsPolluted_AndNoOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Restore_ARowItCannotProve_IsLeftAndCountedAsAmbiguousAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Restore_ReturnsTheSealedEpochItLeft_ToTheStoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Restore_RunTwice_ChangesNothingTheSecondTimeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Restore_FromAStaleCursor_ChangesNothingAndReportsNotAdvancedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:WindowAndRestore_UnderAGenericPlan_RangeScanTheCommitSequenceIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restore_written_here_origins(
  p_after BIGINT,
  p_through BIGINT,
  p_service_name TEXT
) RETURNS TABLE(advanced BOOLEAN, from_origin UUID, restored_reactions INTEGER, restored_returns INTEGER,
                ambiguous_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_field_route_id CONSTANT TEXT := 'rid';       -- 285: the route id every route-stamping write path adds to the row
  c_unknown_service CONSTANT TEXT := 'unknown';  -- ServiceInstanceInfo.Unknown, lower-cased: no service identity
  v_name TEXT := NULLIF(NULLIF(lower(btrim(COALESCE(p_service_name, ''))), ''), c_unknown_service);
  v_local_service_id UUID;
  v_ids UUID[];
  v_streams UUID[];
  v_tenants TEXT[];
  v_types TEXT[];
  v_from UUID[];
  v_positioned BOOLEAN[];
  v_digested BOOLEAN[];
  v_ambiguous_from UUID[];
BEGIN
  -- The cursor compare-and-swap first: it also serializes overlapping passes on the state row.
  UPDATE __SCHEMA__.wh_written_here_restoration r
  SET through_commit_sequence = p_through, updated_at = now()
  WHERE r.singleton AND r.through_commit_sequence = p_after AND p_through >= p_after;
  IF NOT FOUND THEN
    RETURN QUERY SELECT false, NULL::uuid, 0, 0, 0;
    RETURN;
  END IF;

  -- The emit chain's normalization (087): the local service and the empty id are already "locally originated".
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;

  -- The batch's candidates, held in parallel arrays rather than a temp table: no catalog churn per call. Only the rows
  -- that move are kept, and the origins of the ambiguous ones.
  SELECT
    array_agg(c.event_id) FILTER (WHERE c.written_here),
    array_agg(c.stream_id) FILTER (WHERE c.written_here),
    array_agg(c.tenant_scope) FILTER (WHERE c.written_here),
    array_agg(c.event_type) FILTER (WHERE c.written_here),
    array_agg(c.from_origin) FILTER (WHERE c.written_here),
    array_agg(c.positioned) FILTER (WHERE c.written_here),
    array_agg(c.digested) FILTER (WHERE c.written_here),
    array_agg(c.from_origin) FILTER (WHERE NOT c.written_here AND NOT c.positioned
                                       AND (c.disowned OR (c.service IS NULL AND c.routed)))
    INTO v_ids, v_streams, v_tenants, v_types, v_from, v_positioned, v_digested, v_ambiguous_from
  FROM (
    SELECT
      es.event_id,
      es.stream_id,
      COALESCE(es.scope ->> 't', '') AS tenant_scope,
      es.event_type,
      es.origin_service_id AS from_origin,
      es.origin_commit_sequence IS NOT NULL AS positioned,
      fh.service,
      COALESCE(fh.routed, false) AS routed,
      COALESCE(v_name IS NOT NULL AND fh.service = v_name, false) AS written_here,
      EXISTS (SELECT 1 FROM __SCHEMA__.wh_origin_position_unknown u WHERE u.event_id = es.event_id) AS disowned,
      __DIGEST_ELIGIBLE_V2__(es) AS digested
    FROM __SCHEMA__.wh_event_store es
    -- The first hop's service wherever it is kept: a primary-key probe of the body, then at most one route probe and one
    -- staged-form probe (594).
    LEFT JOIN LATERAL (
      SELECT __SCHEMA__._wh_first_hop_service(eb.event_id, eb.metadata) AS service,
             eb.metadata ? c_field_route_id AS routed
      FROM __SCHEMA__.wh_event_body eb
      WHERE eb.event_id = es.event_id
    ) fh ON true
    WHERE es.commit_sequence > p_after
      AND es.commit_sequence <= p_through
      AND es.origin_service_id IS NOT NULL
      AND es.origin_service_id <> __EMPTY_UUID__::uuid
      AND es.origin_service_id IS DISTINCT FROM v_local_service_id
  ) c;

  IF v_ids IS NOT NULL THEN
    -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
    PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
    FROM (SELECT DISTINCT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY 1) s;

    -- The store's triggers follow this update: the sealed epochs it left and joined are refolded at commit (592), the
    -- row's "unknown" answer is forgotten (561) and it leaves its origin's held set (452).
    UPDATE __SCHEMA__.wh_event_store es
    SET origin_service_id = NULL, origin_commit_sequence = NULL
    FROM unnest(v_ids) AS m(event_id)
    WHERE es.event_id = m.event_id;

    -- Digests: every lane leaves its origin's bucket and joins the local lane's, through the one fold helper.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(m.from_origin), array_agg(m.tenant_scope), array_agg(m.event_type), array_agg(m.stream_id),
      array_agg(m.event_id), 'unfold', now())
    FROM unnest(v_ids, v_streams, v_tenants, v_types, v_from, v_digested)
      AS m(event_id, stream_id, tenant_scope, event_type, from_origin, digested)
    WHERE m.digested;
    DELETE FROM __SCHEMA__.wh_stream_digests d
    USING (SELECT DISTINCT u.from_origin FROM unnest(v_from) AS u(from_origin)) f
    WHERE d.origin_service_id = f.from_origin
      AND d.event_count <= 0
      AND d.stream_id = ANY(v_streams);
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(__EMPTY_UUID__::uuid), array_agg(m.tenant_scope), array_agg(m.event_type), array_agg(m.stream_id),
      array_agg(m.event_id), 'fold', now())
    FROM unnest(v_ids, v_streams, v_tenants, v_types, v_digested)
      AS m(event_id, stream_id, tenant_scope, event_type, digested)
    WHERE m.digested;

    -- Both lanes' ledger buckets the moved rows touched changed on this side: their ladders start afresh.
    UPDATE __SCHEMA__.wh_integrity_ledger l
    SET unhealable_at = NULL, exhausted_at = NULL, answer_pending_until = NULL, answered_at = NULL, repair_attempts = 0
    FROM (
      SELECT m.lane, m.tenant_scope, m.event_type, m.stream_id
      FROM unnest(v_from, v_tenants, v_types, v_streams) AS m(lane, tenant_scope, event_type, stream_id)
      UNION
      SELECT __EMPTY_UUID__::uuid, m.tenant_scope, m.event_type, m.stream_id
      FROM unnest(v_tenants, v_types, v_streams) AS m(tenant_scope, event_type, stream_id)
      UNION
      SELECT m.lane, m.tenant_scope, m.event_type, __EMPTY_UUID__::uuid
      FROM unnest(v_from, v_tenants, v_types) AS m(lane, tenant_scope, event_type)
      UNION
      SELECT __EMPTY_UUID__::uuid, m.tenant_scope, m.event_type, __EMPTY_UUID__::uuid
      FROM unnest(v_tenants, v_types) AS m(tenant_scope, event_type)
    ) k(lane, tenant_scope, event_type, stream_id)
    WHERE l.origin_service_id = k.lane AND l.tenant_scope = k.tenant_scope
      AND l.event_type = k.event_type AND l.stream_id = k.stream_id;
  END IF;

  IF v_ids IS NULL AND v_ambiguous_from IS NULL THEN
    RETURN QUERY SELECT true, NULL::uuid, 0, 0, 0;
    RETURN;
  END IF;

  RETURN QUERY
  SELECT true, o.origin,
         COALESCE(SUM(o.reaction), 0)::int, COALESCE(SUM(o.ret), 0)::int, COALESCE(SUM(o.ambiguous), 0)::int
  FROM (
    SELECT m.from_origin AS origin, (NOT m.positioned)::int AS reaction, m.positioned::int AS ret, 0 AS ambiguous
    FROM unnest(v_from, v_positioned) AS m(from_origin, positioned)
    UNION ALL
    SELECT a.from_origin, 0, 0, 1
    FROM unnest(v_ambiguous_from) AS a(from_origin)
  ) o
  GROUP BY o.origin
  ORDER BY o.origin;
END;
$$ LANGUAGE plpgsql;
