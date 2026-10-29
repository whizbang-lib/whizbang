-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 262_ArchiveStoreKeepsATombstone.sql
-- Date: 2026-10-29
-- Description: The Postgres-table archive store (#1736, WS-3 S7). IArchiveStore archives a stream's prefix or the
--              whole stream into wh_event_archive, reads it back byte for byte, restores it and deletes it; this file
--              holds the SQL behind it.
--
--              wh_event_archive keeps every pointer column a restore needs. Migration 085 copied the identity, type,
--              body and scope; flags, the commit sequence, the origin lane and the insert order are added here, so a
--              restored event goes back to the position it held, not to the tail of its lane. Rows archived before
--              this file have them NULL: a restore stores such a row unflagged and leaves it for the commit-order
--              stamper, which gives it a new position.
--
--              wh_archive_stream is the write. Both forms copy then truncate in this one call, so a failure anywhere
--              rolls both back and the stream stays fully hot.
--                a prefix      (p_through_version given) copies the range, then runs wh_truncate_stream_prefix with
--                              every guard it has (consumption, carry-forward, hold, cursor). A refusal raises inside
--                              a subtransaction that also undoes the copy, and its status is returned unchanged.
--                a whole stream (p_through_version NULL) has no survivor for cursors to move onto, so it keeps the
--                              consumption and hold guards and parks the stream's cursors in wh_event_archive_cursors
--                              (the EF Core schema's fk_perspective_cursors_event would refuse the delete otherwise);
--                              a restore puts them back. Snapshots stay: they still describe the stream.
--              Every archive bumps the origin generation, so stream integrity re-verifies the moved history instead
--              of alarming on it, and records it on the stream's lifecycle row (wh_stream_lifecycle, migration 260).
--              A whole-stream archive leaves that row as the tombstone: state Archived, the last version, the
--              locator and the generation.
--
--              The tombstone blocks version reuse. An emit chain or a direct append numbers a stream from its newest
--              hot version, so an append to a stream with nothing hot would start again at the bottom and reuse a
--              version the archive holds. A statement-level trigger on wh_event_store refuses any insert at or below
--              an archived stream's last version (SQLSTATE WHA01), one probe per statement over the inserted rows.
--              It is the table's backstop, like the (stream_id, version) unique index: the per-type policy for
--              appends to an archived stream (refuse, restore, or start a new generation) belongs to the write paths
--              (WS-3 S9). The trigger is installed once wh_stream_lifecycle exists.
--
--              Number 262: reserved for this change.
--
-- Dependencies: 085 (wh_event_archive), 093 (_wh_bump_origin_generation, _wh_refold_epochs_covering), 197 (insert_order),
--               216 (the digest exemption by type), 240 (wh_truncate_stream_prefix), 260 (wh_stream_lifecycle)
-- Objects: wh_event_archive, wh_event_archive_cursors, wh_archive_stream, wh_restore_archived_stream,
--          wh_delete_stream_archive, _wh_refuse_archived_versions, _wh_install_archived_version_guard
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Additive, nullable, no default: no row is rewritten.
ALTER TABLE __SCHEMA__.wh_event_archive
  ADD COLUMN IF NOT EXISTS flags INTEGER,
  ADD COLUMN IF NOT EXISTS commit_sequence BIGINT,
  ADD COLUMN IF NOT EXISTS origin_service_id UUID,
  ADD COLUMN IF NOT EXISTS origin_commit_sequence BIGINT,
  ADD COLUMN IF NOT EXISTS insert_order BIGINT;

-- A whole-stream archive's perspective cursors, as whole rows, until a restore puts them back.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_archive_cursors (
  stream_id        UUID NOT NULL,
  perspective_name TEXT NOT NULL,
  cursor_row       JSONB NOT NULL,
  archived_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (stream_id, perspective_name)
);

COMMENT ON TABLE __SCHEMA__.wh_event_archive_cursors IS
  '262 (#1736): the perspective cursors of a stream archived whole, kept as whole rows (to_jsonb) so a restore '
  'returns each perspective to exactly where it was. A whole-stream archive leaves no event for a cursor to point at.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_archive_stream');

-- <docs>fundamentals/events/archive-store#writing-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_AFailureBetweenCopyAndTruncate_LeavesTheStreamFullyHotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ARefusedPrefix_KeepsTheTruncatesStatus_AndCopiesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_LeavesATombstone_AndBumpsTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_UnprocessedWorkOrAnActiveHold_RefusesAndMovesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_NothingHot_ReportsNothingToArchive_AndBumpsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_DebugMode_KeepsEverythingHotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WaitsForTheStreamsAppendLock_SoNothingIsAppendedWhileItMovesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync</tests>
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
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3), never in a sealed epoch
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), never in a sealed epoch (214)
BEGIN
  -- debug_mode retains forensic history; an archive removes hot detail, so it is skipped like the truncate.
  SELECT (s.setting_value = 'true') INTO v_debug FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode';
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';
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
    -- The tombstone: the stream is archived through v_last, and no append may reuse a version at or below it.
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l (stream_id, state, last_version, archive_locator, origin_generation)
    VALUES (p_stream_id, 'Archived', v_last, p_locator, v_generation)
    ON CONFLICT (stream_id) DO UPDATE SET
      state = 'Archived',
      last_version = GREATEST(COALESCE(l.last_version, EXCLUDED.last_version), EXCLUDED.last_version),
      archive_locator = EXCLUDED.archive_locator,
      origin_generation = EXCLUDED.origin_generation;
  ELSE
    -- A prefix: the stream stays as it was; its row records where the prefix went.
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l (stream_id, state, last_version, archive_locator, origin_generation)
    VALUES (p_stream_id, 'Active', v_last, p_locator, v_generation)
    ON CONFLICT (stream_id) DO UPDATE SET
      archive_locator = EXCLUDED.archive_locator,
      origin_generation = EXCLUDED.origin_generation;
  END IF;

  RETURN QUERY SELECT 'archived'::TEXT, v_count, CASE WHEN p_through_version IS NULL THEN v_last END, v_generation;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_archive_stream(UUID, BIGINT, TEXT) IS
'Archives a stream into wh_event_archive and removes it from the hot store in one transaction (#1736). A prefix (p_through_version given) runs wh_truncate_stream_prefix with all its guards and returns its refusal unchanged; the whole stream (NULL) refuses with blocked or held, parks its cursors in wh_event_archive_cursors and leaves a tombstone (wh_stream_lifecycle state Archived, last_version, archive_locator). Every archive bumps the origin generation. Returns nothing_to_archive when the batch names no hot event, debug_skipped under debug_mode.';

SELECT __SCHEMA__.drop_all_overloads('wh_restore_archived_stream');

-- <docs>fundamentals/events/archive-store#restoring-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_PutsTheStreamBackExactly_CursorsDigestsAndPositionsIncludedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_APrefix_UnderItsSurvivingTailAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_IsIdempotent_TheSecondCallFindsNothingArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_AHotEventAtAnArchivedVersion_IsAVersionConflict_AndRestoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_ARowArchivedBeforeTheStoreKeptPositions_IsStoredUnflaggedForTheStamperAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restore_archived_stream(p_stream_id UUID)
RETURNS TABLE(restore_status TEXT, events_restored BIGINT, origin_generation BIGINT) AS $$
DECLARE
  v_generation BIGINT;
  v_ids UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id) THEN
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

  -- Active again before the events go back, so the archived-version guard lets them in.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET state = CASE WHEN l.state = 'Archived' THEN 'Active' ELSE l.state END,
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
  DELETE FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id;

  PERFORM __SCHEMA__._wh_bump_origin_generation();
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  UPDATE __SCHEMA__.wh_stream_lifecycle l SET origin_generation = v_generation WHERE l.stream_id = p_stream_id;

  RETURN QUERY SELECT 'restored'::TEXT, cardinality(v_ids)::BIGINT, v_generation;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_restore_archived_stream(UUID) IS
'Puts a stream''s archived events back into the hot store at their original versions and commit positions, refolds their digests and sealed epochs, returns parked cursors, marks the stream Active, deletes the archived copy and bumps the origin generation (#1736). Refuses with version_conflict when a hot event holds an archived version; nothing_archived when there is nothing to restore.';

SELECT __SCHEMA__.drop_all_overloads('wh_delete_stream_archive');

-- <docs>fundamentals/events/archive-store#deleting-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Delete_RemovesTheArchivedCopy_TheTombstoneStillBlocksReuseAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_delete_stream_archive(p_stream_id UUID)
RETURNS BIGINT AS $$
DECLARE
  v_deleted BIGINT;
BEGIN
  DELETE FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  DELETE FROM __SCHEMA__.wh_event_archive_cursors p WHERE p.stream_id = p_stream_id;
  -- The tombstone stays, without a locator: the versions were seen downstream and are never reused.
  UPDATE __SCHEMA__.wh_stream_lifecycle l SET archive_locator = NULL WHERE l.stream_id = p_stream_id;
  RETURN v_deleted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_delete_stream_archive(UUID) IS
'Deletes a stream''s archived events and parked cursors for good and clears the tombstone''s locator; the tombstone itself stays, so the stream''s versions are never reused (#1736). Returns the archived events deleted.';

-- <docs>fundamentals/events/archive-store#version-reuse-is-refused</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Tombstone_AnAppendAtOrBelowTheArchivedVersion_IsRefused_AboveItIsStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Tombstone_TheEmitChainCannotReuseAnArchivedVersion_EitherAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refuse_archived_versions() RETURNS TRIGGER AS $$
DECLARE
  v_refused RECORD;
BEGIN
  SELECT n.stream_id, n.version, l.last_version INTO v_refused
  FROM new_rows n
  JOIN __SCHEMA__.wh_stream_lifecycle l ON l.stream_id = n.stream_id
  WHERE l.state = 'Archived' AND n.version <= l.last_version
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION USING
      ERRCODE = 'WHA01',
      MESSAGE = format('Stream %s is archived through version %s; an append at version %s would reuse an archived version.',
                       v_refused.stream_id, v_refused.last_version, v_refused.version),
      HINT = 'Restore the stream (IArchiveStore.RestoreAsync) before appending to it.';
  END IF;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_refuse_archived_versions() IS
'Statement trigger on wh_event_store (#1736): refuses (SQLSTATE WHA01) an insert at or below the last version of a stream whose tombstone says Archived, so an archived version is never reused.';

-- <docs>fundamentals/events/archive-store#version-reuse-is-refused</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Tombstone_AnAppendAtOrBelowTheArchivedVersion_IsRefused_AboveItIsStoredAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_install_archived_version_guard() RETURNS BOOLEAN AS $$
BEGIN
  -- The guard reads the tombstones, so it waits for their table (migration 260).
  IF to_regclass('__SCHEMA__.wh_stream_lifecycle') IS NULL THEN
    RETURN FALSE;
  END IF;
  DROP TRIGGER IF EXISTS wh_event_store_refuses_archived_versions_trg ON __SCHEMA__.wh_event_store;
  CREATE TRIGGER wh_event_store_refuses_archived_versions_trg
    AFTER INSERT ON __SCHEMA__.wh_event_store
    REFERENCING NEW TABLE AS new_rows
    FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__._wh_refuse_archived_versions();
  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_install_archived_version_guard() IS
'Installs the archived-version guard trigger on wh_event_store once wh_stream_lifecycle exists (#1736); returns whether it did.';

SELECT __SCHEMA__._wh_install_archived_version_guard();
