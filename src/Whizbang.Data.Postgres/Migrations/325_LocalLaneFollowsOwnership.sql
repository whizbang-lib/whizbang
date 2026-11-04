-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 325_LocalLaneFollowsOwnership.sql
-- Date: 2026-11-03
-- Description: Stream integrity's local lane follows registered ownership, and owners are exchanged on the
--              checkpoint (#2008, #2032).
--
--              The local lane was every event with no origin recorded. A store that received other services' events
--              before origins were recorded keeps them there, so it advertised them as its own, counted them as surplus
--              and redelivered them under its own identity, and every receiver refused them. Each service now announces
--              its owned domains on its checkpoint (IntegrityCheckpoint.OwnedDomains), and an event whose type is not in
--              an owned namespace is never local. Consumers keep the announced claims here.
--
--              1. wh_integrity_domain_claims: one row per (domain, owner). Domains key in lower case. An owner's
--                 announcement replaces its claim set; two owners on one domain are both kept, and the map built from
--                 them uses neither.
--              2. wh_integrity_record_domain_claims: replaces an owner's claim set from one announcement and returns
--                 the claims other owners hold on the announced domains (the conflicts). Rows that did not change are
--                 not rewritten, so a checkpoint every minute per origin costs no dead tuples.
--
--              3. wh_integrity_reattribution: the re-attribution walk's durable cursor and the ownership fingerprint it
--                 ran under; a changed fingerprint restarts the walk.
--              4. wh_integrity_reattribution_window: the next batch, the rows after the cursor by commit sequence
--                 (idx_event_store_commit_sequence, 046: a bounded range scan, never the whole store), and the distinct
--                 types among them that carry no origin.
--              5. wh_integrity_reattribute_origins: applies one batch. A row without an origin whose type another
--                 service owns takes that owner as origin, its digest moves from the local lane to the owner's, sealed
--                 local epochs are refolded (bumping the origin generation) and the owner's ledger buckets it lands in
--                 are re-armed. A first hop naming a third service is a disagreement (left, counted); a type with no
--                 known owner is unattributed (left, counted). The cursor moves by compare-and-swap. The batch is held in
--                 arrays, not a temp table.
--
--              Number 325: 321 to 324 are taken by changes in flight.
--
-- Dependencies: 046 (commit_sequence, idx_event_store_commit_sequence), 072 (wh_event_body), 087 (origin columns,
--               wh_stream_digests), 090 (wh_integrity_ledger), 092 and 093 (refold_digest_epochs, the origin generation),
--               214 (the at-most-once flag), 216 (__DIGEST_EXEMPT_TYPES__), 230 (the per-stream append lock key),
--               249 (unhealable_at), 320 (answer_pending_until, answered_at, exhausted_at)
-- Objects: wh_integrity_domain_claims, idx_integrity_domain_claims_owner, wh_integrity_record_domain_claims,
--          wh_integrity_reattribution, wh_integrity_reattribution_window, wh_integrity_reattribute_origins
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_domain_claims (
  domain             TEXT        NOT NULL,   -- the claimed domain, lower case, without a trailing dot
  owner_service_id   UUID        NOT NULL,   -- the claiming service's stable id
  owner_service_name TEXT        NOT NULL,   -- its logical name, the directed-message target
  request_topic      TEXT,                   -- where it consumes directed integrity requests, when announced
  claimed_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (domain, owner_service_id)
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_domain_claims IS
  '325 (#2008): the owned domains other services announce on their integrity checkpoints. Used to re-attribute '
  'pre-origin history to its author, to direct repair and backfill at the owner, and to ignore an origin''s counts for '
  'types another service owns. A domain with two owners is used for neither.';

-- <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_AnAnnouncementReplacesTheOwnersClaimSetAsync</tests>
CREATE INDEX IF NOT EXISTS idx_integrity_domain_claims_owner
  ON __SCHEMA__.wh_integrity_domain_claims (owner_service_id);

COMMENT ON INDEX __SCHEMA__.idx_integrity_domain_claims_owner IS
  '325 (#2008): an announcement replaces one owner''s claim set; this finds that owner''s rows.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Replaces an owner's claim set from one announcement and returns the other owners' claims on the announced domains.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_record_domain_claims');
-- <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_StoresTheOwnersClaims_KeyedWithoutCaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_AnAnnouncementReplacesTheOwnersClaimSetAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_AnEmptyAnnouncement_RemovesEveryClaimOfThatOwnerOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_ADomainAnotherOwnerClaims_ComesBackAsAConflictAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_record_domain_claims(
  p_owner_service_id UUID,
  p_owner_service_name TEXT,
  p_request_topic TEXT,
  p_domains TEXT[]
) RETURNS TABLE(domain TEXT, owner_service_id UUID, owner_service_name TEXT, request_topic TEXT) AS $$
#variable_conflict use_column
DECLARE
  v_domains TEXT[];
BEGIN
  SELECT COALESCE(array_agg(DISTINCT d.normalized), ARRAY[]::TEXT[]) INTO v_domains
  FROM (
    SELECT rtrim(lower(btrim(u.raw)), '.') AS normalized
    FROM unnest(COALESCE(p_domains, ARRAY[]::TEXT[])) AS u(raw)
  ) d
  WHERE d.normalized IS NOT NULL AND d.normalized <> '';

  DELETE FROM __SCHEMA__.wh_integrity_domain_claims c
  WHERE c.owner_service_id = p_owner_service_id
    AND c.domain <> ALL(v_domains);

  INSERT INTO __SCHEMA__.wh_integrity_domain_claims AS c
    (domain, owner_service_id, owner_service_name, request_topic, claimed_at)
  SELECT d, p_owner_service_id, p_owner_service_name, p_request_topic, now()
  FROM unnest(v_domains) AS d
  ORDER BY d
  ON CONFLICT (domain, owner_service_id) DO UPDATE SET
    owner_service_name = EXCLUDED.owner_service_name,
    request_topic = COALESCE(EXCLUDED.request_topic, c.request_topic),
    claimed_at = EXCLUDED.claimed_at
  WHERE c.owner_service_name IS DISTINCT FROM EXCLUDED.owner_service_name
     OR (EXCLUDED.request_topic IS NOT NULL AND c.request_topic IS DISTINCT FROM EXCLUDED.request_topic);

  RETURN QUERY
  SELECT c.domain, c.owner_service_id, c.owner_service_name, c.request_topic
  FROM __SCHEMA__.wh_integrity_domain_claims c
  WHERE c.domain = ANY(v_domains)
    AND c.owner_service_id <> p_owner_service_id
  ORDER BY c.domain, c.owner_service_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_record_domain_claims(UUID, TEXT, TEXT, TEXT[]) IS
  '325 (#2008): replaces an owner''s domain claims with one announcement (lower case, no trailing dot; an absent '
  'request topic keeps the known one; unchanged rows are not rewritten) and returns the claims other owners hold on '
  'the announced domains.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Re-attribution (#2008): the store's own pre-origin history, walked in commit-sequence batches.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_reattribution (
  singleton               BOOLEAN     NOT NULL DEFAULT true PRIMARY KEY CHECK (singleton),
  through_commit_sequence BIGINT      NOT NULL DEFAULT 0,    -- the walk's cursor: every row at or below it was walked
  ownership_fingerprint   TEXT        NOT NULL DEFAULT '',   -- the map and owned domains the walk ran under
  updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_reattribution IS
  '325 (#2008): the re-attribution walk''s cursor and the ownership fingerprint it ran under. A changed fingerprint '
  'restarts the walk, so history left unattributed reaches an owner learned later.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The next batch of the walk: the rows after the cursor by commit sequence, at most p_max_events of them, and the
-- distinct types among those without an origin. Served by idx_event_store_commit_sequence (046) as a bounded range
-- scan; a batch costs the rows it scans, never the store. A fingerprint other than the recorded one restarts the walk.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_reattribution_window');
-- <docs>resilience/stream-integrity#re-attribution</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Window_IsTheNextBatchByCommitSequence_WithTheTypesThatHaveNoOriginAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Window_OnACaughtUpWalk_IsEmptyAtTheCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Window_AfterABatch_StartsWhereTheBatchEndedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Window_WithAChangedFingerprint_RewalksFromTheStartAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_reattribution_window(
  p_fingerprint TEXT,
  p_max_events INTEGER
) RETURNS TABLE(after_commit_sequence BIGINT, through_commit_sequence BIGINT, event_types TEXT[]) AS $$
#variable_conflict use_column
DECLARE
  v_after BIGINT;
  v_through BIGINT;
  v_types TEXT[];
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_reattribution (singleton, through_commit_sequence, ownership_fingerprint)
  VALUES (true, 0, COALESCE(p_fingerprint, ''))
  ON CONFLICT (singleton) DO NOTHING;

  UPDATE __SCHEMA__.wh_integrity_reattribution r
  SET through_commit_sequence = 0, ownership_fingerprint = COALESCE(p_fingerprint, ''), updated_at = now()
  WHERE r.singleton AND r.ownership_fingerprint IS DISTINCT FROM COALESCE(p_fingerprint, '');

  SELECT r.through_commit_sequence INTO v_after FROM __SCHEMA__.wh_integrity_reattribution r WHERE r.singleton;

  SELECT MAX(w.commit_sequence),
         COALESCE(array_agg(DISTINCT w.event_type ORDER BY w.event_type) FILTER (WHERE w.origin_service_id IS NULL),
                  ARRAY[]::TEXT[])
    INTO v_through, v_types
  FROM (
    SELECT es.commit_sequence, es.event_type, es.origin_service_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.commit_sequence > v_after
    ORDER BY es.commit_sequence
    LIMIT GREATEST(p_max_events, 1)
  ) w;

  RETURN QUERY SELECT v_after, COALESCE(v_through, v_after), COALESCE(v_types, ARRAY[]::TEXT[]);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_reattribution_window(TEXT, INTEGER) IS
  '325 (#2008): the next re-attribution batch (the rows after the cursor by commit sequence, at most p_max_events) '
  'and the distinct types among them without an origin. A fingerprint other than the recorded one restarts the walk.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Re-attributes one batch. p_event_types / p_owner_ids / p_owner_names pair by position: each non-owned type of the
-- batch with its owner, or a NULL owner when none is known. For a row without an origin in (p_after, p_through]:
--   * owner known, and the stored first hop names the owner, no service, or is absent: the row takes the owner as its
--     origin (origin_commit_sequence stays empty; adoption fills it from a later redelivery), its digest leaves the
--     local lane for the owner's, sealed local epochs over it are refolded, and the owner's ledger buckets it lands
--     in (the stream and the type lane) are re-armed: unhealable and exhausted marks cleared, attempts and the
--     pending answer reset.
--   * owner known, and the first hop names another service: a disagreement; the row is left and counted.
--   * owner unknown: unattributed; the row is left and counted.
-- Rows of types not listed (owned ones) are not touched. The cursor moves to p_through only if it still reads
-- p_after: a batch overtaken by a rewalk or an overlapping pass is discarded whole, and advanced = false.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_reattribute_origins');
-- <docs>resilience/stream-integrity#re-attribution</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_ARowAnotherServiceOwns_TakesTheOwnerAsOrigin_AndItsDigestMovesLanesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_AFirstHopWithoutAServiceIdentity_FollowsOwnershipAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_AFirstHopNamingAThirdService_IsLeftAndCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_ATypeWithNoKnownOwner_IsCountedAsUnattributedAndLeftAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_AnEphemeralRow_TakesItsOwnerWithoutTouchingDigestsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_ReArmsTheOwnersLedgerBucketsTheRowsLandIn_AndNoOthersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_IntoASealedLocalEpoch_RefoldsIt_AndAdvancesTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs:Reattribute_FromAStaleCursor_MovesNothingAndReportsNotAdvancedAsync</tests>
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
  c_field_hops CONSTANT TEXT := 'Hops';        -- EnvelopeMetadata.Hops, as the emit chain stores it (230)
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
    COUNT(*) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed),
    COUNT(*) FILTER (WHERE c.owner_id IS NOT NULL AND c.disagreed),
    COUNT(*) FILTER (WHERE c.owner_id IS NULL),
    array_agg(c.event_id) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed),
    array_agg(c.stream_id) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed),
    array_agg(c.tenant_scope) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed),
    array_agg(c.event_type) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed),
    array_agg(c.owner_id) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed),
    array_agg(c.local_sequence) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed),
    array_agg(c.digested) FILTER (WHERE c.owner_id IS NOT NULL AND NOT c.disagreed)
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
      COALESCE(es.flags, 0) & c_flag_ephemeral = 0
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
        AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) AS digested
    FROM __SCHEMA__.wh_event_store es
    JOIN unnest(p_event_types, p_owner_ids, p_owner_names) AS t(event_type, owner_id, owner_name)
      ON t.event_type = es.event_type
    -- The first hop's service, read only for a row with a known owner to cross-check: a primary-key probe of the body.
    LEFT JOIN LATERAL (
      SELECT NULLIF(lower(btrim(COALESCE(
               h.hop -> 'si' ->> 'sn',
               h.hop -> 'ServiceInstance' ->> 'ServiceName'))), '') AS service
      FROM (SELECT (eb.metadata -> c_field_hops) -> 0 AS hop
            FROM __SCHEMA__.wh_event_body eb
            WHERE eb.event_id = es.event_id AND t.owner_id IS NOT NULL) h
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
    WITH leaving AS (
      SELECT m.tenant_scope, m.event_type, m.stream_id,
             bit_xor(hashtextextended(m.event_id::text, 0)) AS lo, bit_xor(hashtextextended(m.event_id::text, 1)) AS hi,
             COUNT(*)::int AS n
      FROM unnest(v_ids, v_streams, v_tenants, v_types, v_digested) AS m(event_id, stream_id, tenant_scope, event_type, digested)
      WHERE m.digested
      GROUP BY 1, 2, 3
    )
    UPDATE __SCHEMA__.wh_stream_digests d
    SET digest_lo = d.digest_lo # l.lo, digest_hi = d.digest_hi # l.hi, event_count = d.event_count - l.n, updated_at = now()
    FROM leaving l
    WHERE d.origin_service_id = __EMPTY_UUID__::uuid AND d.scope_tenant = l.tenant_scope
      AND d.event_type = l.event_type AND d.stream_id = l.stream_id;
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.origin_service_id = __EMPTY_UUID__::uuid
      AND d.event_count <= 0
      AND d.stream_id = ANY(v_streams);

    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT m.owner_id, m.tenant_scope, m.event_type, m.stream_id,
           bit_xor(hashtextextended(m.event_id::text, 0)), bit_xor(hashtextextended(m.event_id::text, 1)),
           COUNT(*)::int, now()
    FROM unnest(v_ids, v_streams, v_tenants, v_types, v_owners, v_digested)
      AS m(event_id, stream_id, tenant_scope, event_type, owner_id, digested)
    WHERE m.digested
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at;

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

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_reattribute_origins(BIGINT, BIGINT, TEXT[], UUID[], TEXT[]) IS
  '325 (#2008): re-attributes one walk batch. A row without an origin whose type has a known owner takes the owner as '
  'origin (unless its first hop names another service: left and counted), its digest moves from the local lane to the '
  'owner''s, sealed local epochs are refolded and the owner''s ledger buckets re-armed; a type with no known owner is '
  'counted as unattributed and left. The cursor moves only from p_after (compare-and-swap).';
