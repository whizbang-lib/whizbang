-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 750_StoredVectorHistoryIsPackedAndCanBeUnpackedForADowngrade.sql
-- Date: 2026-11-28
-- Description: The vector bodies stored before the packing gate opened are packed by maintenance, and an operator can
--              unpack every stored body before a planned downgrade (#2627, V4 of the vector storage plan, #2333).
--
--              Packing the history. 720 packs what the write paths store once the gate is open; the bodies stored
--              before stay JSON numbers. wh_vector_history_pack_apply is one batch of the per-type history walker
--              (380, operation pack-vectors) over a vector event type: the type's stamped events in commit order, a
--              window per transaction, resumable, one instance per type. It packs a body in the window at the type's
--              paths (the calling build's, which are the walk's variant, so a changed path set walks again) only when
--              the packed body stores smaller, judged as storage pays for it: each candidate is written to a
--              transaction-local copy of wh_event_body (LIKE ... INCLUDING COMPRESSION, so it is compressed by the
--              same method at the same threshold) and its pg_column_size is compared with the stored body's. Short
--              decimals compress well as numerics while their binary32 bits do not (measured, 2026-11-28: 11,351
--              bytes as numbers against 12,960 packed for 4-decimal vectors; 26,613 against 16,476 at full
--              precision), so a vector that was rounded before it was written stays JSON numbers. A body kept is
--              counted as blocked on the walk, which moves past it and completes; it is read again only when the walk
--              restarts. The paths are recorded in wh_packed_vector_paths only by a batch that packed a body, in the
--              same transaction. A batch packs nothing while the gate is closed. The write paths are unchanged: they
--              pack every vector body once the gate is open. No event id, version or stream changes, and the
--              integrity digests fold ids, never bodies.
--
--              Unpacking for a downgrade. wh_request_vector_unpack(who, why) is the operator's command:
--                the hold      one row in wh_vector_unpack_request. While it exists the gate stays closed
--                              (wh_open_vector_packing_gate answers held), so a running build of this release cannot
--                              pack again before the downgrade. wh_resume_vector_packing(who, why) removes it
--                the switch    the journal row is renamed wh_event_body:packed-vectors-unpacking: the write paths
--                              read the old name (_wh_vector_packing_on, unchanged), so they stop packing, while the
--                              rollback gate still reads a conversion applied by this library and keeps standing an
--                              older library down until nothing is packed. No write path is redefined
--                the fence     the request takes SHARE on wh_event_body before it commits, so every writer that
--                              already holds a body (and may have read the open gate) has committed first; its event
--                              is then read by the walk, which completes only once every event of the type is stamped
--                the walk      wh_vector_history_unpack_apply (operation unpack-vectors) writes each packed vector at
--                              the type's recorded packed paths back as JSON numbers (_wh_unpack_vector, 721: each
--                              float4's shortest text, which is the numeric the C# writer's text stored), and
--                              wh_vector_archive_unpack_batch does the same for wh_event_archive in event id order
--                the finish    wh_vector_unpack_finish, once every recorded type's walk and the archive are done,
--                              deletes the wh_packed_vector_paths rows (nothing is packed, so the StoredVectorReaders
--                              guard of the build being returned to has nothing to refuse) and the renamed journal
--                              row (so the rollback gate no longer stands an older library down for packed vectors),
--                              and marks the request complete. The hold stays until resumed
--                resuming      before the finish, the renamed journal row stays (bodies may still be packed); the
--                              gate renames it back when it next opens, keeping when packing began. After the
--                              finish, the gate journals packing afresh. Either way the history walk starts again
--
--              The ordering that keeps a pack batch from racing an unpack: the request and the resume delete the
--              walks first, which waits for any batch holding a walk row, and then rename the journal row; a batch that
--              starts after finds no walk to hold or finds the gate closed, and packs nothing. The gate, the request
--              and the resume take one transaction-scoped advisory lock, so the gate never reopens over a request
--              committing beside it.
--
--              Hot paths: none. No write path, claim or emit statement changes; the gate is a maintenance call.
--
--              Number 750: reserved for this change (750 to 759).
--
-- Reversal: reversible (the down file restores the gate's 720 definition and drops the functions and the request table;
--           bodies keep whatever form they hold, and the journal row, under either name, keeps standing an older
--           library down)
-- Dependencies: 720 (wh_event_vector_paths, wh_packed_vector_paths, _wh_pack_vectors, _wh_vector_packing_on,
--               wh_open_vector_packing_gate), 721 (_wh_unpack_vector), 380 (the type history walker), 085
--               (wh_event_archive), 316 (wh_live_instance_ids), 176 and 385 (wh_stored_form_migrations)
-- Objects: wh_vector_unpack_request, _wh_unpack_vectors, _wh_vector_packing_lock, wh_vector_conversion_mode,
--          wh_vector_history_pack_apply, wh_vector_history_unpack_apply, wh_vector_archive_unpack_batch,
--          wh_vector_unpack_finish, wh_request_vector_unpack, wh_resume_vector_packing, wh_open_vector_packing_gate
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_unpack_vectors');
SELECT __SCHEMA__.drop_all_overloads('_wh_vector_packing_lock');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_conversion_mode');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_history_pack_apply');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_history_unpack_apply');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_archive_unpack_batch');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_unpack_finish');
SELECT __SCHEMA__.drop_all_overloads('wh_request_vector_unpack');
SELECT __SCHEMA__.drop_all_overloads('wh_resume_vector_packing');

-- ===========================================================================================
-- The request
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_vector_unpack_request (
  singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton),
  requested_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  requested_by TEXT NOT NULL,
  reason TEXT NOT NULL,
  archive_after UUID NULL,
  archive_done BOOLEAN NOT NULL DEFAULT FALSE,
  completed_at TIMESTAMPTZ NULL
);

COMMENT ON TABLE __SCHEMA__.wh_vector_unpack_request IS
  'Issue #2627: an operator''s request to unpack every stored vector body before a planned downgrade (at most one row). '
  'While it exists the packing gate stays closed; the stored-vector-conversion maintenance step unpacks the bodies and '
  'the archive (archive_after is its cursor) and sets completed_at once nothing is packed. wh_resume_vector_packing '
  'removes it.';

-- ===========================================================================================
-- Helpers
-- ===========================================================================================

-- A body with the packed vectors at p_paths ([[segment, ...], ...]) written back as JSON numbers (_wh_unpack_vector).
-- A path that is missing, or whose value is not a string, is passed over. Null paths return the body unchanged.
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_WritesEveryBodyBack_ByteEqualToItsJsonNumbers_AndClearsTheRecordAndTheLatchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_unpack_vectors(p_body JSONB, p_paths JSONB)
RETURNS JSONB AS $$
DECLARE
  v_path TEXT[];
  v_body JSONB := p_body;
BEGIN
  IF p_body IS NULL OR p_paths IS NULL THEN
    RETURN p_body;
  END IF;
  FOR v_path IN SELECT ARRAY(SELECT jsonb_array_elements_text(p.path)) FROM jsonb_array_elements(p_paths) AS p(path) LOOP
    IF jsonb_typeof(v_body #> v_path) = 'string' THEN
      v_body := jsonb_set(v_body, v_path, __SCHEMA__._wh_unpack_vector(v_body #> v_path), false);
    END IF;
  END LOOP;
  RETURN v_body;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_unpack_vectors(JSONB, JSONB) IS
  'Issue #2627: the body with the packed vectors at each path ([[segment, ...], ...]) written back as JSON numbers.';

-- The one lock the gate, the request and the resume take (transaction-scoped), so a gate that is opening and a request
-- that is committing never interleave.
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_HoldsTheGateClosed_UntilPackingIsResumed_ThenTheHistoryIsPackedAgainAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_vector_packing_lock()
RETURNS VOID AS $$
BEGIN
  PERFORM pg_advisory_xact_lock(hashtext('__SCHEMA__.wh_vector_packing'));
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_vector_packing_lock() IS
  'Issue #2627: the transaction-scoped advisory lock the packing gate, the unpack request and the resume share.';

-- What the stored-vector-conversion step does this cycle: unpack (a request is not complete), held (a completed request
-- still holds the gate), pack (the gate is open), or closed.
-- <docs>data/event-store#packing-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Pack_WhileTheGateIsClosed_DoesNothing_AndStartsNoWalkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_HoldsTheGateClosed_UntilPackingIsResumed_ThenTheHistoryIsPackedAgainAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_vector_conversion_mode()
RETURNS TEXT AS $$
  SELECT CASE
    WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_vector_unpack_request r WHERE r.completed_at IS NULL) THEN 'unpack'
    WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_vector_unpack_request) THEN 'held'
    WHEN __SCHEMA__._wh_vector_packing_on() THEN 'pack'
    ELSE 'closed' END;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_vector_conversion_mode() IS
  'Issue #2627: unpack, held, pack or closed: what the stored-vector-conversion maintenance step does this cycle.';

-- ===========================================================================================
-- Packing the history
-- ===========================================================================================

-- One batch of the history pass: the window (p_after, p_through] of the type's commit sequences, each body packed at
-- p_paths when the packed body stores smaller (compressed, as the table stores it) than the body stored now.
-- <docs>data/event-store#packing-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Pack_TheHistoryStoredBeforeTheGateOpened_InBatches_ResumesAfterOneBatch_AndPacksEachBodyOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Pack_ABatchWhoseGateClosedAfterTheStepLooked_IsSkipped_AndPacksNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Pack_KeepsABodyWhosePackedFormWouldStoreLarger_PacksOneThatStoresSmaller_AndFinishesOverTheMixAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/PackedVectorBodySizeScenarioTests.cs:StoredBody_BeforeAndAfterPacking_ReportAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_vector_history_pack_apply(
  p_event_type TEXT,
  p_after BIGINT,
  p_through BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT,
  p_paths JSONB
)
RETURNS TABLE(status TEXT, bodies_changed BIGINT, bodies_kept BIGINT, events_total BIGINT, events_matched BIGINT)
LANGUAGE plpgsql
-- Planned with the real type and window, so the window is read through the type's index (as the other walk applies).
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_operation CONSTANT TEXT := 'pack-vectors';
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_candidates BIGINT := 0;
  v_changed BIGINT := 0;
  v_end RECORD;
BEGIN
  IF NOT __SCHEMA__._wh_type_history_window_is_mine(c_operation, v_type, p_after) THEN
    RETURN QUERY SELECT 'skipped (the window is no longer this call''s)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;
  IF NOT __SCHEMA__._wh_vector_packing_on() THEN
    RETURN QUERY SELECT 'skipped (the packing gate is closed)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  -- Each candidate stored as the table would store it: same columns, same compression, same threshold. Local to this
  -- transaction, so nothing is logged and nothing outlives the batch.
  CREATE TEMP TABLE IF NOT EXISTS pg_temp._wh_vector_pack_probe (LIKE __SCHEMA__.wh_event_body INCLUDING COMPRESSION)
    ON COMMIT DROP;
  TRUNCATE pg_temp._wh_vector_pack_probe;
  INSERT INTO pg_temp._wh_vector_pack_probe (event_id, event_data, metadata)
  SELECT w.event_id, w.packed, w.metadata
  FROM (
    SELECT b2.event_id, b2.event_data AS stored, b2.metadata, __SCHEMA__._wh_pack_vectors(b2.event_data, p_paths) AS packed
    FROM __SCHEMA__.wh_event_store es
    JOIN __SCHEMA__.wh_event_body b2 ON b2.event_id = es.event_id
    WHERE es.event_type = v_type
      AND es.commit_sequence IS NOT NULL
      AND es.commit_sequence > COALESCE(p_after, -1)
      AND es.commit_sequence <= p_through
  ) w
  WHERE w.packed IS DISTINCT FROM w.stored;
  GET DIAGNOSTICS v_candidates = ROW_COUNT;

  UPDATE __SCHEMA__.wh_event_body b
  SET event_data = p.event_data
  FROM pg_temp._wh_vector_pack_probe p
  WHERE b.event_id = p.event_id AND pg_column_size(p.event_data) < pg_column_size(b.event_data);
  GET DIAGNOSTICS v_changed = ROW_COUNT;

  -- A path is recorded as packed by the batch that packs a body at it, before the transaction commits.
  IF v_changed > 0 THEN
    INSERT INTO __SCHEMA__.wh_packed_vector_paths (event_type, path)
    SELECT DISTINCT p_event_type, p.path FROM jsonb_array_elements(p_paths) AS p(path)
    ON CONFLICT DO NOTHING;
  END IF;

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(
    c_operation, v_type, p_after, p_through, p_scan_size, p_rows_scanned, 0, v_changed, 0, v_candidates - v_changed);

  RETURN QUERY SELECT v_end.status, v_changed, v_candidates - v_changed, v_end.events_total, v_end.events_matched;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_vector_history_pack_apply(TEXT, BIGINT, BIGINT, INTEGER, BIGINT, JSONB) IS
  'Issue #2627: one batch of the stored vector history pass (history walk operation pack-vectors): in the window '
  '(p_after, p_through] of the type''s commit sequences, packs each body at p_paths when the packed body stores smaller '
  '(compressed, as the table stores it), keeping the rest as JSON numbers (bodies_kept), and records the paths as '
  'packed only when it packed a body. Packs nothing while the packing gate is closed.';

-- ===========================================================================================
-- Unpacking
-- ===========================================================================================

-- One batch of the unpack walk: the window (p_after, p_through] of the type's commit sequences, unpacked at every path
-- recorded as packed for the type.
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_WritesEveryBodyBack_ByteEqualToItsJsonNumbers_AndClearsTheRecordAndTheLatchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_vector_history_unpack_apply(
  p_event_type TEXT,
  p_after BIGINT,
  p_through BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT
)
RETURNS TABLE(status TEXT, bodies_changed BIGINT, events_total BIGINT, events_matched BIGINT)
LANGUAGE plpgsql
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_operation CONSTANT TEXT := 'unpack-vectors';
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_paths JSONB;
  v_changed BIGINT := 0;
  v_end RECORD;
BEGIN
  IF NOT __SCHEMA__._wh_type_history_window_is_mine(c_operation, v_type, p_after) THEN
    RETURN QUERY SELECT 'skipped (the window is no longer this call''s)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_vector_unpack_request r WHERE r.completed_at IS NULL) THEN
    RETURN QUERY SELECT 'skipped (no unpack is requested)'::TEXT, 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  SELECT jsonb_agg(DISTINCT pv.path) INTO v_paths
  FROM __SCHEMA__.wh_packed_vector_paths pv
  WHERE __SCHEMA__.normalize_event_type(pv.event_type) = v_type;

  UPDATE __SCHEMA__.wh_event_body b
  SET event_data = w.unpacked
  FROM (
    SELECT b2.event_id, b2.event_data AS stored, __SCHEMA__._wh_unpack_vectors(b2.event_data, v_paths) AS unpacked
    FROM __SCHEMA__.wh_event_store es
    JOIN __SCHEMA__.wh_event_body b2 ON b2.event_id = es.event_id
    WHERE es.event_type = v_type
      AND es.commit_sequence IS NOT NULL
      AND es.commit_sequence > COALESCE(p_after, -1)
      AND es.commit_sequence <= p_through
  ) w
  WHERE b.event_id = w.event_id AND w.unpacked IS DISTINCT FROM w.stored;
  GET DIAGNOSTICS v_changed = ROW_COUNT;

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(c_operation, v_type, p_after, p_through, p_scan_size, p_rows_scanned, 0, v_changed, 0, 0);

  RETURN QUERY SELECT v_end.status, v_changed, v_end.events_total, v_end.events_matched;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_vector_history_unpack_apply(TEXT, BIGINT, BIGINT, INTEGER, BIGINT) IS
  'Issue #2627: one batch of the unpack walk (history walk operation unpack-vectors): in the window (p_after, '
  'p_through] of the type''s commit sequences, writes each packed vector at the type''s recorded packed paths back as '
  'JSON numbers. Does nothing unless an unpack is requested and not complete.';

-- One batch of the archive unpack: the next p_batch_size archived events past the request's cursor, in event id order,
-- each of a type with recorded packed paths unpacked at them. One instance at a time (the request row, SKIP LOCKED).
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_ArchivedBodies_InBatches_AndLeavesOtherTypesAsItFoundThemAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_vector_archive_unpack_batch(p_batch_size INTEGER)
RETURNS TABLE(status TEXT, rows_scanned BIGINT, rows_changed BIGINT) AS $$
DECLARE
  v_request __SCHEMA__.wh_vector_unpack_request%ROWTYPE;
  v_batch INTEGER := GREATEST(COALESCE(p_batch_size, 1), 1);
  v_scanned BIGINT;
  v_last UUID;
  v_changed BIGINT := 0;
BEGIN
  SELECT * INTO v_request FROM __SCHEMA__.wh_vector_unpack_request r WHERE r.completed_at IS NULL FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 'skipped (no unpack is requested, or another instance holds it)'::TEXT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;
  IF v_request.archive_done THEN
    RETURN QUERY SELECT 'complete'::TEXT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  SELECT count(*), max(w.event_id::text)::uuid INTO v_scanned, v_last
  FROM (
    SELECT a.event_id FROM __SCHEMA__.wh_event_archive a
    WHERE v_request.archive_after IS NULL OR a.event_id > v_request.archive_after
    ORDER BY a.event_id
    LIMIT v_batch
  ) w;

  UPDATE __SCHEMA__.wh_event_archive a
  SET event_data = w.unpacked
  FROM (
    SELECT a2.event_id, a2.event_data AS stored, __SCHEMA__._wh_unpack_vectors(a2.event_data, lp.paths) AS unpacked
    FROM __SCHEMA__.wh_event_archive a2
    CROSS JOIN LATERAL (
      SELECT jsonb_agg(DISTINCT pv.path) AS paths FROM __SCHEMA__.wh_packed_vector_paths pv
      WHERE __SCHEMA__.normalize_event_type(pv.event_type) = __SCHEMA__.normalize_event_type(a2.event_type)
    ) lp
    WHERE (v_request.archive_after IS NULL OR a2.event_id > v_request.archive_after)
      AND a2.event_id <= v_last
      AND lp.paths IS NOT NULL
  ) w
  WHERE a.event_id = w.event_id AND w.unpacked IS DISTINCT FROM w.stored;
  GET DIAGNOSTICS v_changed = ROW_COUNT;

  UPDATE __SCHEMA__.wh_vector_unpack_request r
  SET archive_after = COALESCE(v_last, r.archive_after), archive_done = v_scanned < v_batch;

  RETURN QUERY SELECT CASE WHEN v_scanned < v_batch THEN 'finished' ELSE 'ok' END::TEXT, v_scanned, v_changed;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_vector_archive_unpack_batch(INTEGER) IS
  'Issue #2627: one batch of the archive unpack: the next p_batch_size wh_event_archive rows past the request''s cursor, '
  'in event id order, each packed vector at its type''s recorded packed paths written back as JSON numbers. Status ok, '
  'finished, complete or skipped.';

-- Completes the unpack once nothing is packed: every type with a recorded packed path has a completed unpack walk and
-- the archive is done. Clears the record of packed paths and the renamed journal row, and keeps the hold.
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_OnceComplete_AnOlderBuildIsNoLongerStoodDown_AndItsReaderGuardPassesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_WritesEveryBodyBack_ByteEqualToItsJsonNumbers_AndClearsTheRecordAndTheLatchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_vector_unpack_finish()
RETURNS TEXT AS $$
DECLARE
  v_request __SCHEMA__.wh_vector_unpack_request%ROWTYPE;
  v_left BIGINT;
BEGIN
  PERFORM __SCHEMA__._wh_vector_packing_lock();
  SELECT * INTO v_request FROM __SCHEMA__.wh_vector_unpack_request FOR UPDATE;
  IF NOT FOUND THEN
    RETURN 'skipped (no unpack is requested)';
  END IF;
  IF v_request.completed_at IS NOT NULL THEN
    RETURN 'unpacked';
  END IF;

  SELECT count(DISTINCT __SCHEMA__.normalize_event_type(pv.event_type)) INTO v_left
  FROM __SCHEMA__.wh_packed_vector_paths pv
  WHERE NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_type_history_walks h
    WHERE h.operation = 'unpack-vectors' AND h.event_type = __SCHEMA__.normalize_event_type(pv.event_type)
      AND h.completed_at IS NOT NULL);
  IF v_left > 0 THEN
    RETURN 'unpacking (' || v_left || ' event type(s) left)';
  END IF;
  IF NOT v_request.archive_done THEN
    RETURN 'unpacking (the archive is left)';
  END IF;

  DELETE FROM __SCHEMA__.wh_packed_vector_paths;
  DELETE FROM __SCHEMA__.wh_stored_form_migrations WHERE name = 'wh_event_body:packed-vectors-unpacking';
  DELETE FROM __SCHEMA__.wh_type_history_walks WHERE operation IN ('pack-vectors', 'unpack-vectors');
  UPDATE __SCHEMA__.wh_vector_unpack_request SET completed_at = NOW();
  RETURN 'unpacked';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_vector_unpack_finish() IS
  'Issue #2627: completes a requested unpack once every type with a recorded packed path has a completed unpack walk '
  'and the archive is done: deletes wh_packed_vector_paths and the renamed journal row (wh_event_body:packed-vectors-'
  'unpacking), and marks the request complete. Returns unpacked, unpacking (...) or skipped (...).';

-- ===========================================================================================
-- The operator's commands
-- ===========================================================================================

-- Requests the unpack of every stored vector body for a planned downgrade. Closes the gate and holds it closed, keeps
-- the latch under the unpacking name, and waits for every writer that holds a body (SHARE on wh_event_body) before it
-- commits. A session with no lock_timeout of its own waits at most 10 seconds for them; a request that times out
-- changes nothing and can be run again. A second request while one exists is absorbed.
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Request_NamesWhoAndWhy_ASecondIsAbsorbed_AndResumeWithoutOneSaysSoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Request_WaitsForEveryWriterThatHoldsABody_SoNoneCanPackAfterItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_OnceComplete_AnOlderBuildIsNoLongerStoodDown_AndItsReaderGuardPassesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_request_vector_unpack(p_requested_by TEXT, p_reason TEXT)
RETURNS TEXT AS $$
DECLARE
  v_existing __SCHEMA__.wh_vector_unpack_request%ROWTYPE;
BEGIN
  IF COALESCE(btrim(p_requested_by), '') = '' OR COALESCE(btrim(p_reason), '') = '' THEN
    RAISE EXCEPTION 'An unpack request names who asks for it and why (requested by %, reason %)', p_requested_by, p_reason;
  END IF;
  IF current_setting('lock_timeout') IN ('0', '') THEN
    PERFORM set_config('lock_timeout', '10s', true);
  END IF;
  PERFORM __SCHEMA__._wh_vector_packing_lock();

  SELECT * INTO v_existing FROM __SCHEMA__.wh_vector_unpack_request;
  IF FOUND THEN
    RETURN 'already requested (by ' || v_existing.requested_by || ' at ' || v_existing.requested_at || ')';
  END IF;
  INSERT INTO __SCHEMA__.wh_vector_unpack_request (requested_by, reason) VALUES (p_requested_by, p_reason);

  -- The walks first: a pack batch holding one has committed before the switch turns.
  DELETE FROM __SCHEMA__.wh_type_history_walks WHERE operation IN ('pack-vectors', 'unpack-vectors');
  UPDATE __SCHEMA__.wh_stored_form_migrations SET name = 'wh_event_body:packed-vectors-unpacking', last_applied_at = NOW()
  WHERE name = 'wh_event_body:packed-vectors';
  -- The fence: every writer that already holds a body has committed before this request does.
  LOCK TABLE __SCHEMA__.wh_event_body IN SHARE MODE;
  RETURN 'requested';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_request_vector_unpack(TEXT, TEXT) IS
  'Issue #2627: the operator''s command before a planned downgrade: closes the packing gate and holds it closed '
  '(wh_vector_unpack_request), keeps the journal row as wh_event_body:packed-vectors-unpacking, and waits for every '
  'body writer in flight. The stored-vector-conversion step then unpacks every body. Returns requested or already '
  'requested (...).';

-- Ends the hold: packing resumes once the gate next opens, and the history walk starts again. Before an unpack has
-- finished, the renamed journal row stays (bodies may still be packed) and the gate renames it back as it opens.
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Request_NamesWhoAndWhy_ASecondIsAbsorbed_AndResumeWithoutOneSaysSoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_HoldsTheGateClosed_UntilPackingIsResumed_ThenTheHistoryIsPackedAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_ResumedBeforeItFinished_KeepsTheLatch_AndTheGateRenamesItBackAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_resume_vector_packing(p_requested_by TEXT, p_reason TEXT)
RETURNS TEXT AS $$
BEGIN
  IF COALESCE(btrim(p_requested_by), '') = '' OR COALESCE(btrim(p_reason), '') = '' THEN
    RAISE EXCEPTION 'Resuming vector packing names who asks for it and why (requested by %, reason %)', p_requested_by, p_reason;
  END IF;
  PERFORM __SCHEMA__._wh_vector_packing_lock();
  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_vector_unpack_request) THEN
    RETURN 'not held';
  END IF;
  DELETE FROM __SCHEMA__.wh_type_history_walks WHERE operation IN ('pack-vectors', 'unpack-vectors');
  DELETE FROM __SCHEMA__.wh_vector_unpack_request;
  RETURN 'resumed';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_resume_vector_packing(TEXT, TEXT) IS
  'Issue #2627: ends an unpack''s hold on the packing gate (deletes wh_vector_unpack_request); the gate opens again '
  'once every live instance reads packed vectors. Returns resumed or not held.';

-- ===========================================================================================
-- The gate: last word 720, copied whole, held while an unpack is requested and renaming an unfinished unpack's latch
-- back when it opens (750)
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('wh_open_vector_packing_gate');
-- <docs>data/event-store#packing-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_AFreshService_OpensOnlyOnceThisInstanceHasRegisteredWithItsMarkerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_StaysClosedWhileALiveInstanceReadsNoPackedVectors_AndOpensOnceItIsGoneStaleOrRaisedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_OnceOpen_StaysOpen_WhenAnInstanceWithoutTheMarkerAppearsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_WithoutALibraryVersion_StaysClosed_SoNothingPacksUnjournaledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Journal_RecordsTheLibraryThatStartedPacking_AndNoApplicationVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Rollback_AnOlderLibrary_StandsDownAtAssess_NamingPackedVectorsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorReaderGuardSqlTests.cs:Ledger_RecordsTheRegisteredPaths_OnlyOnceTheGateOpens_AndKeepsThemWhenARegistrationDropsThemAsync</tests>
-- <docs>data/event-store#unpacking-for-a-downgrade</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_HoldsTheGateClosed_UntilPackingIsResumed_ThenTheHistoryIsPackedAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_ResumedBeforeItFinished_KeepsTheLatch_AndTheGateRenamesItBackAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_open_vector_packing_gate(p_library_version TEXT)
RETURNS TEXT AS $$
DECLARE
  v_live BIGINT;
  v_ready BIGINT;
  v_opened BOOLEAN;
  v_renamed BIGINT;
  v_request __SCHEMA__.wh_vector_unpack_request%ROWTYPE;
BEGIN
  IF __SCHEMA__._wh_vector_packing_on() THEN
    RETURN 'open';
  END IF;
  IF COALESCE(btrim(p_library_version), '') = '' THEN
    RETURN 'closed (this instance''s library version is unknown, so the conversion cannot be journaled)';
  END IF;

  -- 750: never reopens over an unpack request, nor beside one that is committing.
  PERFORM __SCHEMA__._wh_vector_packing_lock();
  SELECT * INTO v_request FROM __SCHEMA__.wh_vector_unpack_request;
  IF FOUND THEN
    RETURN 'held (an unpack was requested by ' || v_request.requested_by || ' at ' || v_request.requested_at
      || '; wh_resume_vector_packing packs again)';
  END IF;

  SELECT count(*),
         count(*) FILTER (WHERE jsonb_typeof(si.metadata -> 'stored_form_level') = 'number'
                            AND (si.metadata ->> 'stored_form_level')::NUMERIC >= 2)
    INTO v_live, v_ready
  FROM __SCHEMA__.wh_service_instances si
  WHERE si.instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l);

  IF v_live = 0 OR v_ready < v_live THEN
    RETURN 'closed (' || v_ready || ' of ' || v_live || ' live instances read packed vectors)';
  END IF;

  -- 750: an unpack resumed before it finished left its latch under the unpacking name; it is the same conversion.
  UPDATE __SCHEMA__.wh_stored_form_migrations SET name = 'wh_event_body:packed-vectors', last_applied_at = NOW()
  WHERE name = 'wh_event_body:packed-vectors-unpacking';
  GET DIAGNOSTICS v_renamed = ROW_COUNT;
  v_opened := v_renamed > 0;

  IF NOT v_opened THEN
    WITH journaled AS (
      INSERT INTO __SCHEMA__.wh_stored_form_migrations
        (name, table_name, kind, declared_at, first_applied_at, last_applied_at, rows_converted, applied_library_version)
      VALUES ('wh_event_body:packed-vectors', 'wh_event_body', 'library', NOW(), NOW(), NOW(), 0, p_library_version)
      ON CONFLICT (name) DO UPDATE
        SET applied_library_version = EXCLUDED.applied_library_version,
            first_applied_at = COALESCE(__SCHEMA__.wh_stored_form_migrations.first_applied_at, NOW()),
            last_applied_at = NOW()
        WHERE __SCHEMA__.wh_stored_form_migrations.applied_library_version IS NULL
      RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM journaled) INTO v_opened;
  END IF;

  IF v_opened THEN
    -- The paths registered so far are recorded as packed in the transaction that opens the gate.
    INSERT INTO __SCHEMA__.wh_packed_vector_paths (event_type, path)
    SELECT vp.event_type, p.path
    FROM __SCHEMA__.wh_event_vector_paths vp
    CROSS JOIN LATERAL jsonb_array_elements(vp.paths) AS p(path)
    ON CONFLICT DO NOTHING;
  END IF;

  RETURN CASE WHEN v_opened THEN 'opened' ELSE 'open' END;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_open_vector_packing_gate(TEXT) IS
  'Issue #2625: journals the packed-vectors conversion (wh_stored_form_migrations wh_event_body:packed-vectors, applied '
  'by p_library_version), which opens the write paths'' packing, once at least one instance is live and every live '
  'instance''s registry metadata says stored_form_level >= 2. One-way, except that an operator''s unpack request '
  '(#2627) holds it closed: held (...) then. Returns open, opened, held (...) or closed (...). Called by the '
  'vector-packing-gate maintenance step.';
