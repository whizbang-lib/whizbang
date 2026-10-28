-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 253_RedeliveredOriginChecksTheFirstHop.sql
-- Date: 2026-10-28
-- Description: A redelivery fills an empty origin only on an event its origin authored (#1844), through one
--              reconciliation entry point that also counts the truncation refusals of 252 (#1843).
--
--              249 lets a redelivery bundle fill a stored event's empty origin columns with the bundle's origin.
--              Empty origin columns cannot tell an event received before origins were recorded from one this
--              service authored. When an origin's own store had filed events it received from this service in
--              its local lane (before it recorded origins), its redelivery names itself as their origin, and
--              249 would stamp that origin on events this service authored.
--
--              wh_reconcile_redelivered_bundle_by_first_hop is the one function the work coordinator calls per
--              redelivery bundle. It is 249's adoption with one check, plus the truncation refusals 252 counts:
--
--              The check: before an empty origin takes the bundle's origin, it reads the stored event's hops
--              (wh_event_body.metadata Hops, the service under si.sn, or the legacy ServiceInstance.ServiceName)
--              and adopts only when the first hop names the bundle's origin service (p_origin_service_name, the
--              service of the bundle's own publish hop). Names compare without case. A first hop that names
--              another service refuses the adoption: the event keeps its empty origin, its digest stays in the
--              local lane, and it is counted per bucket in refused_foreign_origin_count, which the caller reports
--              once per bundle. A first hop with no service identity (the slim stored form leaves it out; the Unknown sentinel says the same), no
--              hops, no stored body, or a bundle that names no service leave nothing to compare, so the event
--              adopts as in 249 and that history still converges. The check guards only an empty origin: an
--              event that already carries an origin is handled exactly as in 249.
--
--              The truncation refusals: per bucket, the bundle's children store_inbox_messages (252) refused to
--              restore past deduplication because the stream's earlier history was truncated here, read from
--              wh_inbox_state.error (__REPAIR_REFUSED_TRUNCATED__) for children the event store does not hold. They
--              are returned in refused_truncated_count, apart from the first-hop refusals in
--              refused_foreign_origin_count, because the caller reports each with its own reason.
--
--              252's wh_reconcile_redelivered_bundle (249's adoption plus the truncation refusals, without the
--              origin's name) is superseded here and dropped: nothing calls it. The 249 function keeps its
--              signature and body for versions that still call it. Both are separately named rather than
--              redefined, because changing a function's arity or result in place would leave the earlier
--              definition site adding a second overload, or failing, whenever the ledger replays it.
--
--              Cost: the hop read is one primary-key probe of wh_event_body per present event whose origin is
--              empty, which is the one-time catch-up after an upgrade from a store older than 087. An event that
--              already carries an origin reads no body.
--
-- Dependencies: 072 (wh_event_body), 087 (origin columns, wh_stream_digests), 092 and 093 (the refolds), 214 (the
--               at-most-once flag), 216 (__DIGEST_EXEMPT_TYPES__), 230 (the emit chain's per-stream lock key),
--               249 (wh_adopt_redelivered_origin, copied with the check above), 252 (store_inbox_messages' refusal
--               reason, __REPAIR_REFUSED_TRUNCATED__; wh_reconcile_redelivered_bundle, superseded)
-- Objects: wh_reconcile_redelivered_bundle, wh_reconcile_redelivered_bundle_by_first_hop
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- 252's entry point, superseded by the one below; nothing calls it.
DROP FUNCTION IF EXISTS __SCHEMA__.wh_reconcile_redelivered_bundle(UUID, UUID[], BIGINT[]);

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
    AND d.event_type = l.event_type AND d.stream_id = l.stream_id;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid
    AND d.event_count <= 0
    AND d.stream_id IN (SELECT r.stream_id FROM _wh_adopt_first_hop_rows r WHERE r.adopted AND r.digested AND r.left_local_lane);

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
'253 (#1843, #1844): reconciles a redelivery bundle with the store; the one entry point per bundle. wh_adopt_redelivered_origin (249) with a first-hop check, plus the children store_inbox_messages (252) refused to restore past deduplication because the stream had earlier history truncated here (refused_truncated_count). An empty origin takes the bundle''s origin only when the stored event''s first hop names the bundle''s origin service (p_origin_service_name, compared without case) or carries no service identity; a first hop naming another service refuses it (the event keeps its empty origin and its local digest) and is counted in refused_foreign_origin_count. Everything else is 249''s: an empty origin commit sequence takes the event''s sequence, nothing is overwritten or re-attributed, the local service adopts nothing, digests move lanes and sealed epochs are refolded. Returns per (stream, tenant, type) bucket the events present, adopted, refused as truncated and refused by the first-hop check.';
