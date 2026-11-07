-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 346_MisattributedOriginCorrection.sql
-- Date: 2026-11-07
-- Description: Events recorded under an origin that does not own their type are re-attributed to their owner (#2116).
--
--              A redelivery bundle names the service that answered it as every child's origin. A service that answered
--              with events it had only received (before #2032 limited answers to what the answering service wrote, and
--              from bundles queued at a consumer since) shipped another service's events as its own, and the consumer
--              stored them under the wrong origin. The owner's lane then counts short and the answerer's long, and
--              repair can never heal the bucket: the owner's redelivery is skipped as a duplicate of the stored event.
--              The receive path now stores such a child under its owner (InboxDispatchWorker); this corrects what was
--              stored before.
--
--              1. wh_integrity_origin_correction: the correction walk's durable cursor and the ownership fingerprint it
--                 ran under; a changed fingerprint restarts the walk, so history reaches an owner learned later.
--              2. wh_integrity_origin_correction_window: the next batch, the rows after the cursor by commit sequence
--                 (idx_event_store_commit_sequence, 046: a bounded range scan), and the distinct types among them
--                 recorded under an origin. The caller resolves each type's owner from the ownership map.
--              3. wh_integrity_correct_origins: applies one batch. A row of a listed type recorded under an origin other
--                 than the type's owner takes the owner as origin with no origin sequence (the #2102 rule counts it as
--                 held without a sequence; the owner's next redelivery fills it through the reconcile). Its digest
--                 leaves the wrong origin's bucket for the owner's through the shared fold (_wh_fold_stream_digests,
--                 336), the wrong origin's sealed epochs over its old sequences are refolded, and the ledger buckets
--                 of both lanes it touches are re-armed. Never deletes an event. Returns what it corrected, per type,
--                 wrong origin and owner, for the caller to log at Warning and count. The cursor moves by
--                 compare-and-swap.
--
--              Number 346: 340 and 341 are taken by changes in flight, and 345 by #2110.
--
-- Dependencies: 046 (commit_sequence, idx_event_store_commit_sequence), 087 (origin columns, wh_stream_digests),
--               090 (wh_integrity_ledger), 092 (wh_digest_epoch_frontiers, _wh_fold_digest_epoch), 214 (the
--               at-most-once flag), 216 (__DIGEST_EXEMPT_TYPES__), 230 (the per-stream append lock key), 249 and 320
--               (the ledger's repair marks), 336 (_wh_fold_stream_digests)
-- Objects: wh_integrity_origin_correction, wh_integrity_origin_correction_window, wh_integrity_correct_origins
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_origin_correction (
  singleton               BOOLEAN     NOT NULL DEFAULT true PRIMARY KEY CHECK (singleton),
  through_commit_sequence BIGINT      NOT NULL DEFAULT 0,    -- the walk's cursor: every row at or below it was walked
  ownership_fingerprint   TEXT        NOT NULL DEFAULT '',   -- the ownership map the walk ran under
  updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_origin_correction IS
  '346 (#2116): the misattributed-origin correction walk''s cursor and the ownership fingerprint it ran under. A changed '
  'fingerprint restarts the walk, so history recorded under the wrong origin reaches an owner learned later.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The next batch of the walk: the rows after the cursor by commit sequence, at most p_max_events of them, and the
-- distinct types among those recorded under an origin. A bounded range scan of idx_event_store_commit_sequence (046).
-- The cursor row stays locked for the caller's transaction, so overlapping passes serialize instead of racing.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_origin_correction_window');
-- <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:Window_IsTheNextBatchByCommitSequence_WithTheTypesRecordedUnderAnOriginAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:Window_WithAChangedFingerprint_RewalksFromTheStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:WindowAndCorrection_UnderAGenericPlan_RangeScanTheCommitSequenceIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_origin_correction_window(
  p_fingerprint TEXT,
  p_max_events INTEGER
) RETURNS TABLE(after_commit_sequence BIGINT, through_commit_sequence BIGINT, event_types TEXT[]) AS $$
#variable_conflict use_column
DECLARE
  v_after BIGINT;
  v_through BIGINT;
  v_types TEXT[];
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_origin_correction (singleton, through_commit_sequence, ownership_fingerprint)
  VALUES (true, 0, COALESCE(p_fingerprint, ''))
  ON CONFLICT (singleton) DO NOTHING;

  UPDATE __SCHEMA__.wh_integrity_origin_correction c
  SET through_commit_sequence = 0, ownership_fingerprint = COALESCE(p_fingerprint, ''), updated_at = now()
  WHERE c.singleton AND c.ownership_fingerprint IS DISTINCT FROM COALESCE(p_fingerprint, '');

  -- FOR UPDATE: the cursor row is held to the end of the caller's transaction, so a second pass waits here and the
  -- correction the caller applies in the same transaction always finds the cursor where this window read it.
  SELECT c.through_commit_sequence INTO v_after FROM __SCHEMA__.wh_integrity_origin_correction c WHERE c.singleton FOR UPDATE;

  SELECT MAX(w.commit_sequence),
         COALESCE(array_agg(DISTINCT w.event_type ORDER BY w.event_type) FILTER (WHERE w.origin_service_id IS NOT NULL),
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

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_origin_correction_window(TEXT, INTEGER) IS
  '346 (#2116): the next misattributed-origin correction batch (the rows after the cursor by commit sequence, at most '
  'p_max_events) and the distinct types among them recorded under an origin. A fingerprint other than the recorded one '
  'restarts the walk.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Corrects one batch. p_event_types / p_owner_ids pair by position: each type of the batch whose owner the caller's
-- ownership map knows. A row in (p_after, p_through] of a listed type, recorded under an origin other than its owner (and
-- the owner is not this service), takes the owner as origin with no origin sequence; its digest moves from the wrong
-- origin's bucket to the owner's through the shared fold; the wrong origin's sealed epochs over its old sequences are
-- refolded; the ledger buckets of both lanes (the stream and the type lane) are re-armed. Rows of types not listed are
-- not touched. The cursor moves to p_through only if it still reads p_after: a batch overtaken by a rewalk or an
-- overlapping pass is discarded whole (one row, advanced = false). Returns one row per (type, wrong origin, owner) it
-- corrected, or one row with a NULL type when it corrected nothing.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_correct_origins');
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
           COALESCE(es.flags, 0) & c_flag_ephemeral = 0
             AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
             AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) AS digested
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

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_correct_origins(BIGINT, BIGINT, TEXT[], UUID[]) IS
  '346 (#2116): corrects one walk batch. A row of a listed type recorded under an origin other than its owner takes the '
  'owner as origin with no origin sequence, its digest moves lanes through the shared fold, the wrong origin''s sealed '
  'epochs over it are refolded and both lanes'' ledger buckets re-armed. Never deletes an event. Returns per (type, '
  'wrong origin, owner) the rows corrected; the cursor moves only from p_after (compare-and-swap).';
