-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 335_RedeliveryReconcilesRunAsABatch.sql
-- Date: 2026-11-05
-- Description: Redelivery bundles are reconciled as a batch, and sealed digest epochs are refolded once per batch
--              (#2099).
--
--              wh_reconcile_redelivered_bundle_by_first_hop (321) reconciles one bundle per call, and each call
--              refolds every sealed epoch between the lowest and highest sequence it moved, in the origin's lane and
--              in the local lane. The maintainer's drain runs one call per recorded reconcile. After history was
--              re-attributed to its owners (325, which can name an event's origin but not its origin sequence: only
--              the origin knows that), every redelivered bundle fills one sequence and refolds the epoch that holds
--              it. A backlog of a hundred thousand one-event bundles is a hundred thousand epoch folds, most of them
--              of the same few epochs.
--
--              wh_reconcile_redelivered_bundles takes many bundles at once and does what the single-bundle function
--              does for each, set-based:
--              1. The streams of every bundle's stored events are locked once, in the emit chain's order, before
--                 anything is read, so two batches (or a batch and an append) queue rather than deadlock.
--              2. Each stored event adopts at most once per batch: the first bundle, in the order given, that can
--                 fill it. Its origin sequence is the stored one, else that bundle's, else another bundle's of the
--                 same origin. A bundle naming an event another bundle of the batch filled counts it present and
--                 not adopted, as a later call would have found it; a bundle before it whose first-hop check refused
--                 the event counts that refusal, as an earlier call would have.
--              3. The origin columns are filled in one update and the digests move lanes in one pass.
--              4. Only the sealed epochs that hold a moved event are refolded, each once: per origin lane by origin
--                 sequence, and the local lane by local sequence (bumping the origin generation once when it
--                 refolded anything). The single-bundle function refolded the whole range between its lowest and
--                 highest sequence.
--              5. Per (bundle, stream, tenant, type) it returns what the single-bundle function returns, plus how many
--                 events had their origin filled (origins_filled_count) as distinct from those that already carried
--                 the origin and took only its sequence, which used to be reported as filling the origin.
--
--              Number 335: 334 is taken by a change in flight.
--
-- Dependencies: 087 (origin columns, wh_stream_digests), 092 (wh_digest_epoch_frontiers, _wh_fold_digest_epoch),
--               093 (_wh_bump_origin_generation), 214 (the at-most-once flag), 216 (__DIGEST_EXEMPT_TYPES__),
--               230 (the per-stream append lock key, the stored hop list), 252 (__REPAIR_REFUSED_TRUNCATED__),
--               253 and 321 (wh_reconcile_redelivered_bundle_by_first_hop, whose semantics this keeps per bundle),
--               301 (idx_stream_digests_stream)
-- Objects: wh_reconcile_redelivered_bundles
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_reconcile_redelivered_bundles');

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

  CREATE TEMP TABLE IF NOT EXISTS _wh_reconcile_batch_work (
    bundle INTEGER NOT NULL,
    event_id UUID NOT NULL,
    stream_id UUID NOT NULL,
    tenant_scope TEXT NOT NULL,
    event_type TEXT NOT NULL,
    origin_service_id UUID,
    local_sequence BIGINT,
    origin_sequence BIGINT,
    adopted BOOLEAN NOT NULL,
    left_local_lane BOOLEAN NOT NULL,
    refused BOOLEAN NOT NULL,
    digested BOOLEAN NOT NULL
  ) ON COMMIT DELETE ROWS;
  DELETE FROM _wh_reconcile_batch_work;

  -- One row per (bundle, stored event). Each event adopts for the first bundle that can fill it; the others count it
  -- present only, as a later single-bundle call would have found it already filled.
  INSERT INTO _wh_reconcile_batch_work
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
  JOIN winners w ON w.event_id = j.event_id;

  -- The fills, as arrays the planner can see, so each stored event is found by its key.
  SELECT array_agg(r.event_id ORDER BY r.event_id), array_agg(r.origin_service_id ORDER BY r.event_id),
         array_agg(r.origin_sequence ORDER BY r.event_id)
  INTO v_ids, v_origins, v_sequences
  FROM _wh_reconcile_batch_work r
  WHERE r.adopted;

  IF v_ids IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_event_store es
    SET origin_service_id = m.origin, origin_commit_sequence = m.seq
    FROM unnest(v_ids, v_origins, v_sequences) AS m(eid, origin, seq)
    WHERE es.event_id = m.eid;
  END IF;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins its origin's.
  SELECT array_agg(DISTINCT r.stream_id) INTO v_leaving_streams
  FROM _wh_reconcile_batch_work r
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  IF v_leaving_streams IS NOT NULL THEN
    WITH leaving AS (
      SELECT r.tenant_scope, r.event_type, r.stream_id,
             bit_xor(hashtextextended(r.event_id::text, 0)) AS lo, bit_xor(hashtextextended(r.event_id::text, 1)) AS hi,
             COUNT(*)::int AS n
      FROM _wh_reconcile_batch_work r
      WHERE r.adopted AND r.digested AND r.left_local_lane
      GROUP BY 1, 2, 3
    )
    UPDATE __SCHEMA__.wh_stream_digests d
    SET digest_lo = d.digest_lo # l.lo, digest_hi = d.digest_hi # l.hi, event_count = d.event_count - l.n,
        updated_at = NOW()
    FROM leaving l
    WHERE d.origin_service_id = __EMPTY_UUID__::uuid AND d.scope_tenant = l.tenant_scope
      AND d.event_type = l.event_type AND d.stream_id = l.stream_id
      AND d.stream_id = ANY(v_leaving_streams);
    DELETE FROM __SCHEMA__.wh_stream_digests d
    USING (SELECT DISTINCT r.tenant_scope, r.event_type, r.stream_id
           FROM _wh_reconcile_batch_work r
           WHERE r.adopted AND r.digested AND r.left_local_lane) l
    WHERE d.origin_service_id = __EMPTY_UUID__::uuid
      AND d.scope_tenant = l.tenant_scope AND d.event_type = l.event_type AND d.stream_id = l.stream_id
      AND d.stream_id = ANY(v_leaving_streams)
      AND d.event_count <= 0;

    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT r.origin_service_id, r.tenant_scope, r.event_type, r.stream_id,
           bit_xor(hashtextextended(r.event_id::text, 0)), bit_xor(hashtextextended(r.event_id::text, 1)),
           COUNT(*)::int, NOW()
    FROM _wh_reconcile_batch_work r
    WHERE r.adopted AND r.digested AND r.left_local_lane
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at;
  END IF;

  -- The sealed epochs that hold a moved event, each once: the origins' lanes by origin sequence.
  FOR r_epoch IN
    SELECT DISTINCT r.origin_service_id AS lane, r.origin_sequence / f.epoch_width AS epoch, f.epoch_width AS width
    FROM _wh_reconcile_batch_work r
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
      FROM _wh_reconcile_batch_work r
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
    FROM _wh_reconcile_batch_work r
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
'335 (#2099): reconciles many redelivery bundles at once, with the per-bundle semantics of wh_reconcile_redelivered_bundle_by_first_hop (253, 321): an empty origin takes its bundle''s origin unless the stored first hop names another service, an empty origin sequence of an event that carries the bundle''s origin takes the bundle''s sequence, nothing is overwritten, the local service adopts nothing, digests move lanes. Each event adopts at most once per batch, for the first bundle that can fill it. Every bundle''s streams are locked once in order, the fills are one update, and each sealed epoch holding a moved event is refolded once per batch rather than every epoch of each bundle''s range. Returns per (bundle, stream, tenant, type) the events present, adopted, whose origin was filled (origins_filled_count; the rest of adopted took only the sequence), refused as truncated and refused by the first-hop check.';
