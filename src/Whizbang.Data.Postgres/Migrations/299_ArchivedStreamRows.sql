-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 299_ArchivedStreamRows.sql
-- Date: 2026-11-02
-- Description: Each perspective chooses what happens to its rows of a stream archived whole (#1739, WS-3 S10).
--
--              A perspective declares Keep (the default), Drop or Archive ([ArchivedStreamRows], overridden by
--              Whizbang:Perspectives:ArchivedStreamRows:<Perspective>). The maintenance worker publishes the effective
--              policies to wh_settings (archived_stream_rows, one name=policy per line, rewritten every tick), because the
--              archive runs inside the database. A whole-stream archive (wh_archive_stream) then disposes of the stream's
--              row in each perspective table whose perspectives declare a policy, after the tombstone:
--
--                Keep     the row stays in its table, as before this migration.
--                Drop     the row is deleted with the companions a row eviction removes (_wh_evict_perspective_row_
--                         companions, 211): every snapshot but the latest, per perspective of the model, and, because the
--                         archive parked them, the parked cursors of those perspectives. The latest snapshot is the floor
--                         the generated runner rebuilds the row from when the stream comes back.
--                Archive  the row moves into wh_event_archive_rows, beside the archived events, as a whole row (to_jsonb),
--                         and comes back on a restore (wh_restore_archived_stream) or a new generation
--                         (_wh_start_new_generations). A row written to the table since (a rebuild that includes archived
--                         data) is kept over the archived copy, as a cursor made since is kept over its parked copy.
--
--              The policy is the model's: a table's policy is decided by the perspectives that fold it and declare one,
--              the least destructive winning (Keep, then Archive, then Drop); a perspective that declares nothing does not
--              vote. An active perspective row hold keeps the row in its table. A prefix archive leaves every row alone:
--              the stream is still alive. Deleting the archive (wh_delete_stream_archive) deletes the archived rows; a
--              purge removes them with the stream's lifecycle row (ON DELETE CASCADE). A row whose table no longer exists
--              by the restore is discarded, so a removed model never keeps a stream archived.
--
--              Every redefined function is its latest definition copied whole (rule 5) plus the delta:
--              wh_archive_stream and wh_restore_archived_stream (271), wh_delete_stream_archive (274) and
--              _wh_start_new_generations (280).
--
--              Number 299: reserved for this change.
--
-- Dependencies: 111 (wh_perspective_row_hold), 211 (wh_perspective_registry.perspective_names,
--               _wh_evict_perspective_row_companions), 260 (wh_stream_lifecycle), 262 (wh_event_archive_cursors), 271
--               (last word of wh_archive_stream and wh_restore_archived_stream), 274 (last word of wh_delete_stream_archive),
--               280 (last word of _wh_start_new_generations)
-- Objects: wh_event_archive_rows, fk_event_archive_rows_stream, _wh_dispose_archived_stream_rows,
--          _wh_restore_archived_stream_rows, wh_archive_stream, wh_restore_archived_stream, wh_delete_stream_archive,
--          _wh_start_new_generations
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ===========================================================================================
-- Where an archived row waits
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_archive_rows (
  stream_id   UUID NOT NULL,
  table_name  TEXT NOT NULL,
  row_data    JSONB NOT NULL,
  archived_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (stream_id, table_name),
  CONSTRAINT fk_event_archive_rows_stream FOREIGN KEY (stream_id)
    REFERENCES __SCHEMA__.wh_stream_lifecycle (stream_id) ON DELETE CASCADE
);

COMMENT ON TABLE __SCHEMA__.wh_event_archive_rows IS
  '299 (#1739): the perspective rows of a stream archived whole whose perspectives declare Archive, kept as whole rows '
  '(to_jsonb) beside the archived events, one per perspective table. A restore or a new generation puts each back in its '
  'table; deleting the archive deletes them; a purge removes them with the stream''s lifecycle row (ON DELETE CASCADE).';

-- ===========================================================================================
-- The disposal and the return
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('_wh_dispose_archived_stream_rows');

-- The stream's row in each perspective table whose perspectives declare Drop or Archive (wh_settings
-- archived_stream_rows, one name=policy per line; a line that names no policy is ignored, so a hand edit never fails an
-- archive). Per table, the least destructive declared policy wins. A row under an active wh_perspective_row_hold stays.
-- Drop deletes the row with its companions (every snapshot but the latest, and the parked cursors of the model's
-- perspectives); Archive moves it into wh_event_archive_rows. Called by wh_archive_stream after the tombstone. Returns the
-- rows dropped or moved.
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Drop_RemovesTheRow_ItsParkedCursors_AndAllButTheLatestSnapshot_AndLeavesAnotherModelAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_MovesTheRowBesideTheEvents_AndARestoreBringsItBackExactlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:PerspectivesOfOneModelThatDisagree_TheLeastDestructiveWinsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:AHandEditedSetting_ThatNamesNoPolicy_KeepsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:AnActiveRowHold_KeepsTheRowInItsTable_AndALapsedOneDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Keep_TheRowStaysInItsTable_WithItsCursorsAndSnapshotsParkedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRowsTests.cs:Drop_OnADapperMigratedDatabase_RemovesTheRowAndItsParkedCursorAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_dispose_archived_stream_rows(p_stream_id UUID)
RETURNS INTEGER AS $$
DECLARE
  v_schema TEXT := replace('__SCHEMA__', '"', '');
  v_table RECORD;
  v_rows INTEGER;
  v_total INTEGER := 0;
BEGIN
  FOR v_table IN
    WITH declared AS (
      SELECT split_part(d.line, '=', 1) AS perspective_name, split_part(d.line, '=', 2) AS policy
      FROM __SCHEMA__.wh_settings s
      CROSS JOIN LATERAL unnest(string_to_array(s.setting_value, E'\n')) AS d(line)
      WHERE s.setting_key = 'archived_stream_rows'
        AND split_part(d.line, '=', 2) IN ('Keep', 'Drop', 'Archive')
    ), per_table AS (
      SELECT r.table_name,
             CASE WHEN bool_or(dp.policy = 'Keep') THEN 'Keep'
                  WHEN bool_or(dp.policy = 'Archive') THEN 'Archive'
                  ELSE 'Drop' END AS policy
      FROM __SCHEMA__.wh_perspective_registry r
      CROSS JOIN LATERAL unnest(r.perspective_names) AS n(perspective_name)
      JOIN declared dp ON dp.perspective_name = n.perspective_name
      GROUP BY r.table_name
    )
    SELECT pt.table_name, pt.policy,
           (SELECT array_agg(DISTINCT m.perspective_name)
            FROM __SCHEMA__.wh_perspective_registry r2
            CROSS JOIN LATERAL unnest(r2.perspective_names) AS m(perspective_name)
            WHERE r2.table_name = pt.table_name) AS perspective_names
    FROM per_table pt
    WHERE pt.policy <> 'Keep'
    ORDER BY pt.table_name
  LOOP
    CONTINUE WHEN to_regclass(format('%I.%I', v_schema, v_table.table_name)) IS NULL;

    IF v_table.policy = 'Archive' THEN
      EXECUTE format(
        'WITH moved AS (
           DELETE FROM %I.%I t
           WHERE t.id = $1
             AND NOT EXISTS (
               SELECT 1 FROM %I.wh_perspective_row_hold h
               WHERE h.table_name = %L AND h.row_id = t.id AND h.hold_until > NOW())
           RETURNING to_jsonb(t) AS row_data)
         INSERT INTO %I.wh_event_archive_rows (stream_id, table_name, row_data)
         SELECT $1, %L, m.row_data FROM moved m
         ON CONFLICT (stream_id, table_name) DO UPDATE SET row_data = EXCLUDED.row_data, archived_at = NOW()',
        v_schema, v_table.table_name, v_schema, v_table.table_name, v_schema, v_table.table_name)
      USING p_stream_id;
      GET DIAGNOSTICS v_rows = ROW_COUNT;
    ELSE
      EXECUTE format(
        'DELETE FROM %I.%I t
         WHERE t.id = $1
           AND NOT EXISTS (
             SELECT 1 FROM %I.wh_perspective_row_hold h
             WHERE h.table_name = %L AND h.row_id = t.id AND h.hold_until > NOW())',
        v_schema, v_table.table_name, v_schema, v_table.table_name)
      USING p_stream_id;
      GET DIAGNOSTICS v_rows = ROW_COUNT;
      IF v_rows > 0 THEN
        -- The companions a row eviction removes; the live cursors are already parked, so their parked rows go too.
        PERFORM __SCHEMA__._wh_evict_perspective_row_companions(v_table.table_name, ARRAY[p_stream_id]);
        DELETE FROM __SCHEMA__.wh_event_archive_cursors pc
        WHERE pc.stream_id = p_stream_id
          AND pc.perspective_name = ANY(v_table.perspective_names);
      END IF;
    END IF;
    v_total := v_total + v_rows;
  END LOOP;

  RETURN v_total;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_dispose_archived_stream_rows(UUID) IS
  '299 (#1739): drops or archives a stream''s rows in each perspective table whose perspectives declare Drop or Archive '
  '(wh_settings archived_stream_rows; the least destructive declared policy wins per table; an active row hold keeps the '
  'row). Called by wh_archive_stream for a whole-stream archive. Returns the rows dropped or moved.';

SELECT __SCHEMA__.drop_all_overloads('_wh_restore_archived_stream_rows');

-- Puts each archived row of the stream back in its table (jsonb_populate_record over the table's current shape) and
-- removes the archived copies. A row already in the table (written since the archive) is kept; a row whose table no
-- longer exists is discarded. Called by wh_restore_archived_stream and _wh_start_new_generations. Returns the rows put
-- back.
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_MovesTheRowBesideTheEvents_AndARestoreBringsItBackExactlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_ARowWrittenSinceTheArchive_IsKeptByTheRestore_AndTheArchivedCopyGoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_ARowWhoseTableIsGoneByTheRestore_IsDiscarded_AndTheRestoreStillRunsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_ANewGeneration_BringsTheArchivedRowBackAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRowsTests.cs:Archive_OnADapperMigratedDatabase_MovesTheRow_AndARestoreBringsItBackAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_restore_archived_stream_rows(p_stream_id UUID)
RETURNS INTEGER AS $$
DECLARE
  v_schema TEXT := replace('__SCHEMA__', '"', '');
  v_row RECORD;
  v_rows INTEGER;
  v_total INTEGER := 0;
BEGIN
  FOR v_row IN
    DELETE FROM __SCHEMA__.wh_event_archive_rows ar
    WHERE ar.stream_id = p_stream_id
    RETURNING ar.table_name, ar.row_data
  LOOP
    CONTINUE WHEN to_regclass(format('%I.%I', v_schema, v_row.table_name)) IS NULL;
    EXECUTE format(
      'INSERT INTO %I.%I SELECT (jsonb_populate_record(NULL::%I.%I, $1)).* ON CONFLICT DO NOTHING',
      v_schema, v_row.table_name, v_schema, v_row.table_name)
    USING v_row.row_data;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    v_total := v_total + v_rows;
  END LOOP;

  RETURN v_total;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_restore_archived_stream_rows(UUID) IS
  '299 (#1739): puts a stream''s archived perspective rows back in their tables and removes the archived copies; a row '
  'written to the table since is kept, and a row whose table no longer exists is discarded. Returns the rows put back.';

-- ===========================================================================================
-- The archive, the restore, the delete and the new generation
-- ===========================================================================================

-- wh_archive_stream: last word 271, disposing of the stream's perspective rows after the tombstone.
SELECT __SCHEMA__.drop_all_overloads('wh_archive_stream');


-- <docs>fundamentals/events/archive-store#writing-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_AFailureBetweenCopyAndTruncate_LeavesTheStreamFullyHotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ARefusedPrefix_KeepsTheTruncatesStatus_AndCopiesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_LeavesATombstone_AndBumpsTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_UnprocessedWorkOrAnActiveHold_RefusesAndMovesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_NothingHot_ReportsNothingToArchive_AndBumpsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_DebugMode_KeepsEverythingHotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_WithNoLedgerRowYet_BuildsTheTombstoneFromTheEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WaitsForTheStreamsAppendLock_SoNothingIsAppendedWhileItMovesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Prefix_ThroughAVersionBelowOne_IsRefused_ThroughOneIsArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Prefix_ReachingAPerspectivesNewestSnapshot_IsRefused_StoppingBelowItIsArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Prefix_MarksTheSnapshotsAtOrBelowItsCut_AndLeavesTheNewestUnmarkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:WholeStream_MarksEverySnapshotWithItsLastVersionAsync</tests>
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Drop_RemovesTheRow_ItsParkedCursors_AndAllButTheLatestSnapshot_AndLeavesAnotherModelAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_MovesTheRowBesideTheEvents_AndARestoreBringsItBackExactlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:APrefixArchive_LeavesTheRowWhereItIs_WhateverThePolicyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_archive_stream(
  p_stream_id UUID, p_through_version BIGINT, p_locator TEXT)
RETURNS TABLE(archive_status TEXT, events_archived BIGINT, last_version BIGINT, origin_generation BIGINT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_last BIGINT;
  v_status TEXT;
  v_count BIGINT := 0;
  v_blocked BIGINT;
  v_held BIGINT;
  v_generation BIGINT;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_type TEXT;
  v_tenant TEXT;
  v_first_at TIMESTAMPTZ;
  v_bucket TIMESTAMPTZ;
  v_bytes BIGINT;
  v_kept_bytes BIGINT;
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3), never in a sealed epoch
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), never in a sealed epoch (214)
  c_flags_off_the_ledger CONSTANT INTEGER := 24;  -- ephemeral (8) or a compaction origin (16): no ledger row (260)
BEGIN
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  -- 271 (#1738): a prefix always removes at least version 1, so an archived stream always shows a gap at its start
  -- (a stream's first version is 0 or 1), and a read from the start sees it without asking the archive.
  IF p_through_version IS NOT NULL AND p_through_version < 1 THEN
    RETURN QUERY SELECT 'not_past_first_version'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- debug_mode retains forensic history; an archive removes hot detail, so it is skipped like the truncate.
  SELECT (s.setting_value = 'true') INTO v_debug FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode';
  IF COALESCE(v_debug, FALSE) THEN
    RETURN QUERY SELECT 'debug_skipped'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- The emit chains' and the direct append's per-stream key: nothing is appended to the stream while it moves.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  SELECT MAX(es.version) INTO v_last FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;
  IF v_last IS NULL
     OR (p_through_version IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_event_store es
       WHERE es.stream_id = p_stream_id AND es.version <= p_through_version)) THEN
    RETURN QUERY SELECT 'nothing_to_archive'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- 271 (#1738): a prefix never takes the newest snapshot any perspective holds for the stream, so the snapshot's event
  -- and everything after it stay hot and a rewind to it never reads the archive. Refused rather than shortened: the
  -- caller gets exactly the archive it asked for or none, and chooses a cut below the snapshot itself. A snapshot that
  -- records no version cannot be placed in the stream and is not counted.
  IF p_through_version IS NOT NULL AND EXISTS (
    SELECT 1
    FROM (
      SELECT MAX(sn.snapshot_version) AS newest
      FROM __SCHEMA__.wh_perspective_snapshots sn
      WHERE sn.stream_id = p_stream_id
        AND sn.snapshot_version IS NOT NULL
      GROUP BY sn.perspective_name
    ) latest
    WHERE latest.newest <= p_through_version) THEN
    RETURN QUERY SELECT 'snapshot_in_range'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- The stream's ledger identity by 260's rules, read while every event is still hot, for a stream the ledger has no row
  -- for yet (the backfill has not reached it): the first event's type, tenant and time, the UTC hour of the newest event.
  -- The first event that counts for the ledger, or else the first event at all, so a stream of only ephemeral events
  -- still gets a tombstone that refuses reuse of its versions.
  SELECT es.aggregate_type, es.scope ->> 't', es.created_at INTO v_type, v_tenant, v_first_at
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
  ORDER BY (COALESCE(es.flags, 0) & c_flags_off_the_ledger <> 0), es.version
  LIMIT 1;
  SELECT date_trunc('hour', MAX(es.created_at), 'UTC') INTO v_bucket
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;

  -- The body bytes the ledger counts (events on the ledger, as stored), split into what this archive moves and what
  -- stays hot, so approx_bytes keeps measuring the hot store.
  SELECT COALESCE(SUM(b.sz) FILTER (WHERE p_through_version IS NULL OR b.version <= p_through_version), 0),
         COALESCE(SUM(b.sz) FILTER (WHERE p_through_version IS NOT NULL AND b.version > p_through_version), 0)
  INTO v_bytes, v_kept_bytes
  FROM (
    SELECT es.version, COALESCE(pg_column_size(eb.event_data), 0) + COALESCE(pg_column_size(eb.metadata), 0) AS sz
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
  ) b;

  IF p_through_version IS NOT NULL THEN
    -- A prefix: copy, then the gated truncate. A refusal raises WHA00 so the subtransaction undoes the copy.
    BEGIN
      INSERT INTO __SCHEMA__.wh_event_archive
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at,
         flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order)
      SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
             eb.event_data, eb.metadata, es.scope, es.version, es.created_at,
             es.flags, es.commit_sequence, es.origin_service_id, es.origin_commit_sequence, es.insert_order
      FROM __SCHEMA__.wh_event_store es
      LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.stream_id = p_stream_id AND es.version <= p_through_version
      ON CONFLICT (event_id) DO NOTHING;

      SELECT t.truncate_status, t.events_truncated INTO v_status, v_count
      FROM __SCHEMA__.wh_truncate_stream_prefix(p_stream_id, p_through_version, FALSE) t;
      IF v_status <> 'truncated' THEN
        RAISE EXCEPTION USING ERRCODE = 'WHA00', MESSAGE = v_status;
      END IF;
    EXCEPTION WHEN SQLSTATE 'WHA00' THEN
      RETURN QUERY SELECT v_status, 0::BIGINT, NULL::BIGINT, v_generation;
      RETURN;
    END;
  ELSE
    -- The whole stream. Consumption gate: no perspective may still owe any of its events.
    SELECT count(*) INTO v_blocked
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
    WHERE es.stream_id = p_stream_id AND pe.processed_at IS NULL;
    IF v_blocked > 0 THEN
      RETURN QUERY SELECT 'blocked'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
      RETURN;
    END IF;

    -- Hold guard: an active destruction hold is a recorded decision to keep that event hot.
    SELECT count(*) INTO v_held
    FROM __SCHEMA__.wh_event_destruction_hold h
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
    WHERE es.stream_id = p_stream_id AND h.hold_until > NOW();
    IF v_held > 0 THEN
      RETURN QUERY SELECT 'held'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
      RETURN;
    END IF;

    INSERT INTO __SCHEMA__.wh_event_archive
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at,
       flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order)
    SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
           eb.event_data, eb.metadata, es.scope, es.version, es.created_at,
           es.flags, es.commit_sequence, es.origin_service_id, es.origin_commit_sequence, es.insert_order
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id
    ON CONFLICT (event_id) DO NOTHING;

    -- Park the cursors: nothing hot is left for them to point at, and a restore puts them back as they were.
    INSERT INTO __SCHEMA__.wh_event_archive_cursors (stream_id, perspective_name, cursor_row)
    SELECT c.stream_id, c.perspective_name, to_jsonb(c)
    FROM __SCHEMA__.wh_perspective_cursors c
    WHERE c.stream_id = p_stream_id
    ON CONFLICT (stream_id, perspective_name) DO UPDATE SET cursor_row = EXCLUDED.cursor_row, archived_at = NOW();
    DELETE FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = p_stream_id;

    -- The sealed epochs the stream's folded rows sit in, per lane, captured while the rows exist (as 240 does).
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = p_stream_id
        AND COALESCE(es.flags, 0) & c_flag_ephemeral = 0
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;

    -- Every event of the stream leaves, so every digest bucket of the stream is empty: remove them outright rather
    -- than subtract, which would have to repeat each fold predicate exactly (a restore folds them back).
    DELETE FROM __SCHEMA__.wh_stream_digests d WHERE d.stream_id = p_stream_id;

    -- Finished work rows and lapsed holds of the stream's events; the guards above proved nothing else is left.
    DELETE FROM __SCHEMA__.wh_perspective_events pe
    WHERE pe.stream_id = p_stream_id AND pe.processed_at IS NOT NULL;

    DELETE FROM __SCHEMA__.wh_event_destruction_hold h
    USING __SCHEMA__.wh_event_store es
    WHERE h.event_id = es.event_id AND es.stream_id = p_stream_id;

    DELETE FROM __SCHEMA__.wh_event_body eb
    USING __SCHEMA__.wh_event_store es
    WHERE eb.event_id = es.event_id AND es.stream_id = p_stream_id;

    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
    END IF;
  END IF;

  -- Every archive moves history, so every archive bumps the generation (a prefix whose truncate already bumped it
  -- moves it twice; a consumer resets once per change it sees, so the second is harmless).
  PERFORM __SCHEMA__._wh_bump_origin_generation();
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  IF p_through_version IS NULL THEN
    -- The tombstone: the stream is archived through v_last, and no append may reuse a version at or below it. Nothing
    -- of it is hot, so it counts no bytes.
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
       archive_locator, origin_generation)
    VALUES (p_stream_id, v_type, v_tenant, v_first_at, v_bucket, v_last, 0, 'Archived', p_locator, v_generation)
    ON CONFLICT (stream_id) DO UPDATE SET
      state = 'Archived',
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = 0,
      archive_locator = EXCLUDED.archive_locator,
      origin_generation = EXCLUDED.origin_generation;

    -- 299 (#1739): the stream's rows of the perspectives whose policy is Drop or Archive leave with it, after the
    -- tombstone, which an archived row references.
    PERFORM __SCHEMA__._wh_dispose_archived_stream_rows(p_stream_id);
  ELSE
    -- A prefix: the stream stays as it was; its row records where the prefix went and stops counting its bytes.
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
       archive_locator, origin_generation)
    VALUES (p_stream_id, v_type, v_tenant, v_first_at, v_bucket, v_last, v_kept_bytes, 'Active', p_locator, v_generation)
    ON CONFLICT (stream_id) DO UPDATE SET
      approx_bytes = GREATEST(l.approx_bytes - v_bytes, 0),
      archive_locator = EXCLUDED.archive_locator,
      origin_generation = EXCLUDED.origin_generation;
  END IF;

  -- 271 (#1738): the snapshots the archive covers record how far it reaches, so a rewind from one of them, or a full
  -- rebuild, reads through the archive: a prefix marks those at or below its cut, a whole stream marks them all.
  UPDATE __SCHEMA__.wh_perspective_snapshots sn
  SET archived_through_version = GREATEST(COALESCE(sn.archived_through_version, 0), COALESCE(p_through_version, v_last))
  WHERE sn.stream_id = p_stream_id
    AND (p_through_version IS NULL OR sn.snapshot_version <= p_through_version);

  RETURN QUERY SELECT 'archived'::TEXT, v_count, CASE WHEN p_through_version IS NULL THEN v_last END, v_generation;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_archive_stream(UUID, BIGINT, TEXT) IS
'Archives a stream into wh_event_archive and removes it from the hot store in one transaction (#1736). A prefix (p_through_version given) runs wh_truncate_stream_prefix with all its guards and returns its refusal unchanged; the whole stream (NULL) refuses with blocked or held, parks its cursors in wh_event_archive_cursors and leaves a tombstone (wh_stream_lifecycle state Archived, last_version, archive_locator). Every archive bumps the origin generation. Returns nothing_to_archive when the batch names no hot event, debug_skipped under debug_mode. 271: refuses a prefix through a version below 1 (not_past_first_version) and one that reaches the newest snapshot any perspective holds for the stream (snapshot_in_range), and marks the snapshots an archive covers with how far it reaches (archived_through_version) (#1738). 299: a whole-stream archive drops or archives the stream''s perspective rows by each perspective''s policy (_wh_dispose_archived_stream_rows, #1739).';

-- wh_restore_archived_stream: last word 271, returning the archived perspective rows.
SELECT __SCHEMA__.drop_all_overloads('wh_restore_archived_stream');


-- <docs>fundamentals/events/archive-store#restoring-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_PutsTheStreamBackExactly_CursorsDigestsAndPositionsIncludedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_APrefix_UnderItsSurvivingTailAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_IsIdempotent_TheSecondCallFindsNothingArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_AHotEventAtAnArchivedVersion_IsAVersionConflict_AndRestoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_ARowArchivedBeforeTheStoreKeptPositions_IsStoredUnflaggedForTheStamperAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_AnArchivedStream_StaysArchived_AndARestoreReturnsItSealedAsync</tests>
-- <docs>fundamentals/events/archive-store#restore-requests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:Restore_MarksTheRequestRestored_AndMakesItsDeadLettersDueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:Restore_ASecondRestoreOfAStreamAlreadyBack_CompletesItsRequestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Restore_ClearsEverySnapshotsArchiveMarkAsync</tests>
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_MovesTheRowBesideTheEvents_AndARestoreBringsItBackExactlyAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRowsTests.cs:Archive_OnADapperMigratedDatabase_MovesTheRow_AndARestoreBringsItBackAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restore_archived_stream(p_stream_id UUID)
RETURNS TABLE(restore_status TEXT, events_restored BIGINT, origin_generation BIGINT) AS $$
DECLARE
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

  -- Fold the restored rows back in with the chain's own predicates.
  INSERT INTO __SCHEMA__.wh_stream_digests AS d
    (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
  SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid),
         COALESCE(es.scope ->> 't', ''),
         es.event_type,
         es.stream_id,
         bit_xor(hashtextextended(es.event_id::text, 0)),
         bit_xor(hashtextextended(es.event_id::text, 1)),
         COUNT(*)::int,
         NOW()
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_ids)
    AND COALESCE(es.flags, 0) & c_flag_ephemeral = 0
    AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
    AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
  GROUP BY 1, 2, 3, 4
  ORDER BY 1, 2, 3, 4
  ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
    digest_lo = d.digest_lo # EXCLUDED.digest_lo,
    digest_hi = d.digest_hi # EXCLUDED.digest_hi,
    event_count = d.event_count + EXCLUDED.event_count,
    updated_at = EXCLUDED.updated_at;

  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_ids)
      AND COALESCE(es.flags, 0) & c_flag_ephemeral = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
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

  PERFORM __SCHEMA__._wh_bump_origin_generation();
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

COMMENT ON FUNCTION __SCHEMA__.wh_restore_archived_stream(UUID) IS
'Puts a stream''s archived events back into the hot store at their original versions and commit positions, refolds their digests and sealed epochs, returns parked cursors, marks the stream Active (266: Sealed when it was sealed, #1733), deletes the archived copy and bumps the origin generation (#1736). Refuses with version_conflict when a hot event holds an archived version; nothing_archived when there is nothing to restore. 271: completes the stream''s restore request, makes its AwaitingRestore dead letters due and clears its snapshots'' archive marks, on a restore and on a nothing_archived of a stream no longer archived (#1738). 299: returns the perspective rows the archive moved out of their tables (#1739).';

-- wh_delete_stream_archive: last word 274, deleting the archived perspective rows.
SELECT __SCHEMA__.drop_all_overloads('wh_delete_stream_archive');

-- <docs>fundamentals/events/archive-store#deleting-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Delete_RemovesTheArchivedCopy_TheTombstoneStillBlocksReuseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Extents_TellAStreamWhoseArchiveWasDeleted_FromOneThatNeverHadAnArchiveAsync</tests>
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:DeletingTheArchive_DeletesTheArchivedRowWithItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_delete_stream_archive(p_stream_id UUID)
RETURNS BIGINT AS $$
DECLARE
  v_deleted BIGINT;
BEGIN
  DELETE FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  DELETE FROM __SCHEMA__.wh_event_archive_cursors p WHERE p.stream_id = p_stream_id;
  -- 299 (#1739): an archived perspective row is part of the archive; nothing can bring it back without the events.
  DELETE FROM __SCHEMA__.wh_event_archive_rows ar WHERE ar.stream_id = p_stream_id;
  -- The tombstone stays, without a locator: the versions were seen downstream and are never reused. A row that named an
  -- archive records that its archive was deleted (#1958), so a rebuild never reads the stream as one never archived.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET archive_locator = NULL,
      archive_deleted_at = CASE WHEN l.archive_locator IS NOT NULL THEN NOW() ELSE l.archive_deleted_at END
  WHERE l.stream_id = p_stream_id;
  RETURN v_deleted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_delete_stream_archive(UUID) IS
'Deletes a stream''s archived events and parked cursors for good and clears the tombstone''s locator; the tombstone itself stays, so the stream''s versions are never reused (#1736). A row that named an archive is stamped archive_deleted_at, so a rebuild can tell its lost history from a stream never archived (#1958). 299: deletes the stream''s archived perspective rows with it (#1739). Returns the archived events deleted.';

-- _wh_start_new_generations: last word 280, returning the archived perspective rows.
SELECT __SCHEMA__.drop_all_overloads('_wh_start_new_generations');

-- Starts the new generation of each stream among p_stream_ids that _wh_new_generation_streams names: the stream becomes
-- Active again and keeps its locator, so its archive reads as a prefix. The archived events, the parked cursors and the
-- snapshots (with their archive marks) stay as they are; 299 (#1739): the perspective rows the archive moved out of their
-- tables come back. The caller holds the stream locks and numbers the append from the row's last version.
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_EveryParkedCursorHasItsSnapshot_StoresAtTheNextVersion_AndTheArchiveBecomesAPrefixAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_StoresTheEventAtTheNextVersion_AndDeadLettersNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:InboxAppend_StoresTheReceivedEventAtTheNextVersionAsync</tests>
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_ANewGeneration_BringsTheArchivedRowBackAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_start_new_generations(p_stream_ids UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_started UUID[];
  v_stream_id UUID;
BEGIN
  WITH started AS (
    UPDATE __SCHEMA__.wh_stream_lifecycle l
    SET state = 'Active'
    WHERE l.stream_id IN (SELECT g.new_generation_stream_id FROM __SCHEMA__._wh_new_generation_streams(p_stream_ids) g)
    RETURNING l.stream_id)
  SELECT COALESCE(array_agg(s.stream_id), ARRAY[]::UUID[]) INTO v_started FROM started s;

  -- 299 (#1739): a new generation continues the stream from where its perspectives stopped, so the rows the archive moved
  -- out of their tables are live again.
  FOREACH v_stream_id IN ARRAY v_started LOOP
    PERFORM __SCHEMA__._wh_restore_archived_stream_rows(v_stream_id);
  END LOOP;
  RETURN cardinality(v_started);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_start_new_generations(UUID[]) IS
  'Starts the new generation of each NewGeneration stream an append reaches (#1734): Active again, its archive kept as a '
  'prefix. 299: returns the perspective rows the archive moved out of their tables (#1739). Returns how many streams started one.';
