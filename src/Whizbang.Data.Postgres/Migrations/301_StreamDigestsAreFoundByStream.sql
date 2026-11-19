-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 301_StreamDigestsAreFoundByStream.sql
-- Date: 2026-11-02
-- Description: Stream-keyed reads and deletes of wh_stream_digests use an index, and reconciling a redelivery bundle
--              removes the local buckets it emptied by their full key (#2000).
--
--              wh_stream_digests holds one row per (lane, tenant, type, stream), so a mature store has millions of
--              rows. Its only index was the primary key (origin_service_id, scope_tenant, event_type, stream_id).
--              Every digest-maintenance path that names a stream without its tenant and type (stream close, prefix
--              truncation, supersession, fan-out filing, and the redelivery reconcile below) could use only the
--              leading column, which matches a whole lane, and read every bucket of that lane.
--
--              The redelivery reconcile ran that read once per bundle. During the catch-up that fills origins on
--              history stored before origins were recorded, every consumer receives a steady stream of bundles, and
--              the per-bundle lane read held a shared database server's CPU at its ceiling.
--
--              1. idx_stream_digests_stream on (stream_id): every stream-keyed digest read and delete becomes an
--                 index lookup. stream_id never changes after insert, so digest updates (one per stored event)
--                 remain heap-only and pay nothing for the index; a new bucket pays one more index entry.
--              2. wh_reconcile_redelivered_bundle_by_first_hop, redefined whole from 253 with one change: the
--                 statements that move digests out of the local lane (the subtracting update and the delete of
--                 emptied buckets) name the streams the bundle moved, as an array (stream_id = ANY(...)). Before,
--                 the update joined the digests only to the function's temporary rows, which carry no statistics,
--                 and the delete named only the lane and stream; measured plans reached the digests through the
--                 primary key's leading column and read every bucket of the local lane, once per bundle. With the
--                 streams named, idx_stream_digests_stream finds only the buckets the bundle changes, and the delete
--                 also matches the full key.
--
--              The index is built inside the runner's transaction, which holds writes to wh_stream_digests for the
--              build (seconds for a few million rows). It runs at the Migrate stage, before workers start.
--
--              Number 301: 297 to 300 are taken by changes in flight.
--
-- Dependencies: 087 (wh_stream_digests), 253 (wh_reconcile_redelivered_bundle_by_first_hop, copied with the change above)
-- Objects: idx_stream_digests_stream, wh_reconcile_redelivered_bundle_by_first_hop
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- <docs>resilience/stream-integrity#pre-stamp-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_EmptyingALocalBucket_ReadsOnlyTheBucketsItChanges_NotTheWholeLocalLaneAsync</tests>
CREATE INDEX IF NOT EXISTS idx_stream_digests_stream ON __SCHEMA__.wh_stream_digests (stream_id);

COMMENT ON INDEX __SCHEMA__.idx_stream_digests_stream IS
'301 (#2000): stream-keyed digest reads and deletes (stream close, truncation, supersession, fan-out filing, redelivery reconcile) are index lookups instead of a read of the whole lane.';

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

  CREATE TEMP TABLE IF NOT EXISTS _wh_adopt_first_hop_rows (
    event_id UUID PRIMARY KEY,
    stream_id UUID NOT NULL,
    tenant_scope TEXT NOT NULL,
    event_type TEXT NOT NULL,
    left_local_lane BOOLEAN NOT NULL,
    local_sequence BIGINT,
    origin_sequence BIGINT,
    adopted BOOLEAN NOT NULL,
    refused BOOLEAN NOT NULL,
    digested BOOLEAN NOT NULL
  ) ON COMMIT DROP;
  TRUNCATE _wh_adopt_first_hop_rows;

  INSERT INTO _wh_adopt_first_hop_rows
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
  ) c;

  UPDATE __SCHEMA__.wh_event_store es
  SET origin_service_id = p_origin_service_id, origin_commit_sequence = r.origin_sequence
  FROM _wh_adopt_first_hop_rows r
  WHERE es.event_id = r.event_id AND r.adopted;

  -- 301 (#2000): the local-lane statements below are driven by the streams the bundle moved, as an array the planner
  -- can see. Joined only against the temporary rows (which carry no statistics), the planner reached the digests
  -- through the primary key's leading column, which matches the whole local lane, and read every local bucket on
  -- every bundle. With the streams named, idx_stream_digests_stream finds the handful of buckets the bundle changes.
  SELECT array_agg(DISTINCT r.stream_id) INTO v_leaving_streams
  FROM _wh_adopt_first_hop_rows r
  WHERE r.adopted AND r.digested AND r.left_local_lane;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins the origin's.
  WITH leaving AS (
    SELECT r.tenant_scope, r.event_type, r.stream_id,
           bit_xor(hashtextextended(r.event_id::text, 0)) AS lo, bit_xor(hashtextextended(r.event_id::text, 1)) AS hi,
           COUNT(*)::int AS n
    FROM _wh_adopt_first_hop_rows r
    WHERE r.adopted AND r.digested AND r.left_local_lane
    GROUP BY 1, 2, 3
  )
  UPDATE __SCHEMA__.wh_stream_digests d
  SET digest_lo = d.digest_lo # l.lo, digest_hi = d.digest_hi # l.hi, event_count = d.event_count - l.n, updated_at = NOW()
  FROM leaving l
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid AND d.scope_tenant = l.tenant_scope
    AND d.event_type = l.event_type AND d.stream_id = l.stream_id
    AND d.stream_id = ANY(v_leaving_streams);
  DELETE FROM __SCHEMA__.wh_stream_digests d
  USING (SELECT DISTINCT r.tenant_scope, r.event_type, r.stream_id
         FROM _wh_adopt_first_hop_rows r
         WHERE r.adopted AND r.digested AND r.left_local_lane) l
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid
    AND d.scope_tenant = l.tenant_scope AND d.event_type = l.event_type AND d.stream_id = l.stream_id
    AND d.stream_id = ANY(v_leaving_streams)
    AND d.event_count <= 0;

  INSERT INTO __SCHEMA__.wh_stream_digests AS d
    (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
  SELECT p_origin_service_id, r.tenant_scope, r.event_type, r.stream_id,
         bit_xor(hashtextextended(r.event_id::text, 0)), bit_xor(hashtextextended(r.event_id::text, 1)),
         COUNT(*)::int, NOW()
  FROM _wh_adopt_first_hop_rows r
  WHERE r.adopted AND r.digested AND r.left_local_lane
  GROUP BY 1, 2, 3, 4
  ORDER BY 1, 2, 3, 4
  ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
    digest_lo = d.digest_lo # EXCLUDED.digest_lo,
    digest_hi = d.digest_hi # EXCLUDED.digest_hi,
    event_count = d.event_count + EXCLUDED.event_count,
    updated_at = EXCLUDED.updated_at;

  -- Sealed epochs over the moved events: the origin's lane by origin sequence, the local lane by local sequence.
  SELECT MIN(r.origin_sequence), MAX(r.origin_sequence) INTO v_min, v_max
  FROM _wh_adopt_first_hop_rows r
  WHERE r.adopted AND r.digested AND r.origin_sequence IS NOT NULL;
  IF v_min IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refold_epochs_covering(p_origin_service_id, v_min, v_max);
  END IF;

  SELECT MIN(r.local_sequence), MAX(r.local_sequence) INTO v_min, v_max
  FROM _wh_adopt_first_hop_rows r
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
    FROM _wh_adopt_first_hop_rows r
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
'301 (#2000): the local-lane digest update and delete are driven by the moved streams through idx_stream_digests_stream. 253 (#1843, #1844): reconciles a redelivery bundle with the store; the one entry point per bundle. wh_adopt_redelivered_origin (249) with a first-hop check, plus the children store_inbox_messages (252) refused to restore past deduplication because the stream had earlier history truncated here (refused_truncated_count). An empty origin takes the bundle''s origin only when the stored event''s first hop names the bundle''s origin service (p_origin_service_name, compared without case) or carries no service identity; a first hop naming another service refuses it (the event keeps its empty origin and its local digest) and is counted in refused_foreign_origin_count. Everything else is 249''s: an empty origin commit sequence takes the event''s sequence, nothing is overwritten or re-attributed, the local service adopts nothing, digests move lanes and sealed epochs are refolded. Returns per (stream, tenant, type) bucket the events present, adopted, refused as truncated and refused by the first-hop check.';
