-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 599_EventsWrittenHereAreReturnedToTheLocalLane.sql
-- Date: 2026-11-25
-- Description: Events this service wrote that were stored under another service's origin before 594 are returned to the
--              local lane, with the integrity bookkeeping they polluted (#2573).
--
--              594 stops new misattributions; it left the rows already moved as they were, because only this service's
--              name tells them apart and the store does not record it (wh_service_config.service_name is never
--              written). The walk here takes the name from the caller, the maintenance step that knows it
--              (WrittenHereOriginRestorationStep), and moves back exactly the rows the stored evidence proves this
--              service wrote. Two shapes:
--
--                a reaction    a receptor's reaction to a received event, moved by the re-attribution walk to the
--                              trigger's owner with no origin position. That owner answers "unknown" when asked for the
--                              position, and the row sits in its held set (an undecided bucket) for good.
--                a return      this service's own event that came back in another service's redelivery bundle and
--                              adopted that bundle's origin, usually with the bundle's position.
--
--              The proof is the first hop's service, read wherever the event keeps it (_wh_first_hop_service, 594: the
--              stored row's first hop, else its route, else its staged full form). A row recorded under an origin whose
--              first hop names this service was written here, whatever origin it carries: a received event's first hop
--              is its writer's. Nothing else counts as proof. In particular a slim row whose first hop records no
--              service anywhere is NOT moved, although 594 keeps such a row local when it has no origin: under the Slim
--              form nothing is staged or distilled, so a genuinely received slim row looks the same, and so does any row
--              whose route aged out (route_idle_days). An unpositioned row left this way that its origin disowned (a
--              recorded "unknown" answer) or that names no service at all is counted as ambiguous, never moved.
--
--              A moved row loses its origin and its origin position, and in the same transaction:
--                digests       its hash leaves its origin's stream-digest bucket and joins the local lane's, through the
--                              one fold helper (_wh_fold_stream_digests, 336); emptied buckets are removed;
--                seals         the sealed epochs it left (its origin's, by its old origin position) and joined (the local
--                              lane's, by its commit sequence) are refolded at commit by the store's own triggers (592),
--                              which also bump the origin generation for a local rewrite;
--                unknown       its recorded "unknown" answer goes (561's update trigger);
--                undecided     it leaves its origin's held set (452's triggers), so the windows it kept undecided can be
--                              decided;
--                ledger        both lanes' repair-ledger buckets it touched, per stream and the type lane, start their
--                              ladder afresh, so a bucket marked unhealable because of it can heal.
--              Nothing is deleted.
--
--              The walk is one-off: its ceiling is the store's highest commit sequence when the walk first runs, since
--              594 writes nothing new of either shape. It walks the store by commit sequence in bounded batches
--              (idx_event_store_commit_sequence, 046), each batch its own transaction, under a compare-and-swap cursor
--              the window holds for the batch's transaction. Re-running it is harmless: a moved row has no origin and is
--              never a candidate again. A changed service name rewalks from the start.
--
--              Why not here, in the migration: the proof needs the service's name, which only the running service
--              knows, and the walk reads every received row once (a body probe each), which on a large store belongs in
--              idle-gated maintenance batches, not in an upgrade transaction.
--
--              Number 599: 590 to 598 are taken by changes merged or in flight.
--
-- Reversal: reversible (the functions and the cursor table go; rows already returned stay local, which is correct)
-- Dependencies: 046 (idx_event_store_commit_sequence), 087 (origin columns, wh_stream_digests), 090 (wh_integrity_ledger),
--               336 (_wh_fold_stream_digests), 452 (the held set's triggers), 561 (wh_origin_position_unknown and its
--               triggers), 592 (the sealed-epoch touch triggers), 594 (_wh_first_hop_service)
-- Objects: wh_written_here_restoration, wh_written_here_restoration_window, wh_restore_written_here_origins
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_written_here_restoration (
  singleton               BOOLEAN     NOT NULL DEFAULT true PRIMARY KEY CHECK (singleton),
  through_commit_sequence BIGINT      NOT NULL DEFAULT 0,    -- the walk's cursor: every row at or below it was walked
  ceiling_commit_sequence BIGINT      NOT NULL DEFAULT 0,    -- the store's highest commit sequence when the walk began
  service_name            TEXT        NOT NULL DEFAULT '',   -- the name the walk proved rows by, lower-cased and trimmed
  updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE __SCHEMA__.wh_written_here_restoration IS
  '599 (#2573): the cursor of the one-off walk that returns events this service wrote, stored under another origin '
  'before 594, to the local lane. The ceiling is the highest commit sequence when the walk began; a changed service name '
  'rewalks from the start.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- The next batch of the walk: the rows after the cursor by commit sequence, at most p_max_events of them, never past the
-- ceiling. A bounded range scan of idx_event_store_commit_sequence (046). The cursor row stays locked for the caller's
-- transaction, so overlapping passes serialize instead of racing. through = after when the walk is done.
SELECT __SCHEMA__.drop_all_overloads('wh_written_here_restoration_window');
-- <docs>resilience/stream-integrity#events-written-here-returned</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Window_WalksUpToTheCeilingTheFirstWindowSet_InBoundedBatchesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:Window_WithAnotherServiceName_RewalksFromTheStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WrittenHereOriginRestorationSqlTests.cs:WindowAndRestore_UnderAGenericPlan_RangeScanTheCommitSequenceIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_written_here_restoration_window(
  p_service_name TEXT,
  p_max_events INTEGER
) RETURNS TABLE(after_commit_sequence BIGINT, through_commit_sequence BIGINT) AS $$
#variable_conflict use_column
DECLARE
  v_name TEXT := lower(btrim(COALESCE(p_service_name, '')));
  v_after BIGINT;
  v_ceiling BIGINT;
  v_through BIGINT;
BEGIN
  INSERT INTO __SCHEMA__.wh_written_here_restoration (singleton, through_commit_sequence, ceiling_commit_sequence, service_name)
  SELECT true, 0, COALESCE(MAX(es.commit_sequence), 0), v_name FROM __SCHEMA__.wh_event_store es
  ON CONFLICT (singleton) DO NOTHING;

  UPDATE __SCHEMA__.wh_written_here_restoration r
  SET through_commit_sequence = 0, service_name = v_name, updated_at = now()
  WHERE r.singleton AND r.service_name IS DISTINCT FROM v_name;

  -- FOR UPDATE: the cursor row is held to the end of the caller's transaction, so a second pass waits here and the
  -- restore the caller applies in the same transaction always finds the cursor where this window read it.
  SELECT r.through_commit_sequence, r.ceiling_commit_sequence INTO v_after, v_ceiling
  FROM __SCHEMA__.wh_written_here_restoration r WHERE r.singleton FOR UPDATE;

  SELECT MAX(w.commit_sequence) INTO v_through
  FROM (
    SELECT es.commit_sequence
    FROM __SCHEMA__.wh_event_store es
    WHERE es.commit_sequence > v_after
      AND es.commit_sequence <= v_ceiling
    ORDER BY es.commit_sequence
    LIMIT GREATEST(p_max_events, 1)
  ) w;

  RETURN QUERY SELECT v_after, COALESCE(v_through, v_after);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_written_here_restoration_window(TEXT, INTEGER) IS
  '599 (#2573): the next batch of the walk that returns events written here to the local lane: the rows after the cursor '
  'by commit sequence, at most p_max_events, never past the ceiling the first window set. A changed service name '
  'restarts the walk.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Restores one batch. A row in (p_after, p_through] recorded under another origin whose first hop names p_service_name
-- loses its origin and origin position; its digest moves from its origin's bucket to the local lane's, both lanes'
-- ledger buckets are re-armed, and the store's triggers refold the sealed epochs it left and joined, forget its
-- "unknown" answer and take it out of its origin's held set. A row the evidence does not prove is left; an unpositioned
-- one its origin disowned, or that names no service anywhere, is counted as ambiguous. The cursor moves to p_through only
-- if it still reads p_after: a batch overtaken by a rewalk or an overlapping pass is discarded whole (one row, advanced =
-- false). Returns one row per origin it restored from or left an ambiguous row under, or one row with a NULL origin.
SELECT __SCHEMA__.drop_all_overloads('wh_restore_written_here_origins');
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
      __DIGEST_ELIGIBLE__(es) AS digested
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

COMMENT ON FUNCTION __SCHEMA__.wh_restore_written_here_origins(BIGINT, BIGINT, TEXT) IS
  '599 (#2573): restores one walk batch. A row recorded under another origin whose first hop names this service (read '
  'from the row, its route or its staged form) loses its origin and origin position; its digest moves to the local lane '
  'through the shared fold, both lanes'' ledger buckets are re-armed, and the store''s triggers refold the sealed epochs, '
  'forget its unknown answer and take it out of the held set. Unproven unpositioned rows that are disowned or name no '
  'service are counted as ambiguous and left. Returns per origin the reactions (unpositioned) and returns (positioned) '
  'restored; the cursor moves only from p_after (compare-and-swap).';
