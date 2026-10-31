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
--              (WS-3 S9).
--
--              The archive keeps the ledger honest: a write builds the stream's row from its events by 260's rules when
--              the ledger has none yet (the backfill has not reached it), a prefix stops counting the bytes it moved and a
--              whole stream counts none, and a restore counts them again and leaves the row Active. A purge also removes
--              the cursors a whole-stream archive parked (wh_purge_streams, copied from 260 with that one table added).
--
--              Number 262: reserved for this change.
--
-- Dependencies: 085 (wh_event_archive), 093 (_wh_bump_origin_generation, _wh_refold_epochs_covering), 197 (insert_order),
--               216 (the digest exemption by type), 240 (wh_truncate_stream_prefix), 260 (wh_stream_lifecycle, and the
--               last word of wh_purge_streams)
-- Objects: wh_event_archive, wh_event_archive_cursors, wh_archive_stream, wh_restore_archived_stream,
--          wh_delete_stream_archive, _wh_refuse_archived_versions, wh_event_store_refuses_archived_versions_trg,
--          wh_purge_streams
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_WithNoLedgerRowYet_BuildsTheTombstoneFromTheEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync</tests>
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync</tests>
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
    state = CASE WHEN l.state = 'Archived' THEN 'Active' ELSE l.state END,
    last_version = GREATEST(l.last_version, EXCLUDED.last_version),
    last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
    approx_bytes = l.approx_bytes + v_bytes,
    archive_locator = NULL,
    origin_generation = EXCLUDED.origin_generation;

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

-- The guard: once per insert statement on wh_event_store, over the rows it inserted. 260 precedes this file, so the
-- tombstone table always exists here; the ledger's redefinition closure re-runs only later files, never this one first.
DROP TRIGGER IF EXISTS wh_event_store_refuses_archived_versions_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_refuses_archived_versions_trg
  AFTER INSERT ON __SCHEMA__.wh_event_store
  REFERENCING NEW TABLE AS new_rows
  FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__._wh_refuse_archived_versions();

-- wh_purge_streams: last word 260, with the cursors a whole-stream archive parked removed. Nothing else changes.

-- <docs>operations/infrastructure/purging-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:Purge_RemovesEveryRowOfTheStreams_AndLeavesOthersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:DryRun_CountsWhatWouldGo_AndChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Purge_AnArchivedStream_TakesItsArchiveParkedCursorsAndTombstoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_purge_streams(
  p_stream_ids UUID[],
  p_dry_run BOOLEAN,
  p_purge_id UUID,
  p_batch_index INTEGER,
  p_requested_by TEXT,
  p_reason TEXT
) RETURNS TABLE(purged_table TEXT, purged_rows BIGINT) AS $$
DECLARE
  v_ids UUID[];
  v_event_ids UUID[];
  v_message_ids UUID[];
  v_schema TEXT;
  v_table TEXT;
  v_n BIGINT;
  v_counts JSONB := '{}'::jsonb;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- The empty stream id is "no stream": purging it would reach every streamless row.
  IF __EMPTY_UUID__::uuid = ANY(p_stream_ids) THEN
    RAISE EXCEPTION 'wh_purge_streams: the empty stream id cannot be purged';
  END IF;

  SELECT COALESCE(array_agg(DISTINCT s), ARRAY[]::uuid[]) INTO v_ids FROM unnest(p_stream_ids) s;

  -- Ids the indirect tables are keyed by, read before anything is deleted.
  SELECT COALESCE(array_agg(es.event_id), ARRAY[]::uuid[]) INTO v_event_ids
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  SELECT v_event_ids || COALESCE(array_agg(i.message_id), ARRAY[]::uuid[]) INTO v_message_ids
  FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);

  -- Work tables, in the canonical lock order.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_outbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Counted before the inbox delete that removes them by cascade.
  SELECT count(*) INTO v_n
  FROM __SCHEMA__.wh_inbox_state st
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = st.message_id
  WHERE i.stream_id = ANY(v_ids);
  purged_table := 'wh_inbox_state'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_inbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_events'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_active_streams'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Deduplication entries of the stream's messages: its events and its inbox messages.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_message_deduplication'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_receptor_processing'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Before the events: fk_perspective_cursors_event is ON DELETE RESTRICT.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_snapshots'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_applied'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Every perspective's row for the streams. The registry names the tables; one it names that does not
  -- exist yet is skipped. Going through regclass resolves this function's own schema whichever way the
  -- runner substituted it (see 105).
  SELECT n.nspname INTO v_schema
  FROM pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
  WHERE c.oid = '__SCHEMA__.wh_event_store'::regclass;

  FOR v_table IN
    SELECT DISTINCT r.table_name FROM __SCHEMA__.wh_perspective_registry r ORDER BY r.table_name
  LOOP
    CONTINUE WHEN to_regclass(format('%I.%I', v_schema, v_table)) IS NULL;
    IF p_dry_run THEN
      EXECUTE format('SELECT count(*) FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) INTO v_n USING v_ids;
    ELSE
      EXECUTE format('DELETE FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) USING v_ids;
      GET DIAGNOSTICS v_n = ROW_COUNT;
    END IF;
    purged_table := v_table; purged_rows := v_n; RETURN NEXT;
    v_counts := v_counts || jsonb_build_object(purged_table, v_n);
  END LOOP;

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_row_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_row_eviction_journal'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- The sealed epochs covering the fold-relevant events refold AFTER the delete, and their ranges are
  -- unknowable once the rows are gone (the same capture close_stream makes).
  IF NOT p_dry_run THEN
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = ANY(v_ids)
        AND COALESCE(es.flags, 0) & 8 = 0
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;
  END IF;

  -- Every event of the streams goes, so every digest bucket and ledger row of the streams goes with them.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_digests'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_integrity_ledger'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_body'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_destruction_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_lifecycle_completions'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- 262 (#1736): the cursors a whole-stream archive parked for its restore; a purged stream is never restored.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive_cursors ac WHERE ac.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive_cursors ac WHERE ac.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_apply_fold_watermarks'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_store'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- 260 (#1732): the stream's lifecycle row goes with its events, after them, in the order the emit chain writes the
  -- two tables. A purged stream is gone, not archived: no row is left for an end-of-life sweep to act on.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_lifecycle'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Refold what the delete touched, then bump the generation ONCE so consumers reset their seals and
  -- re-verify instead of alarming on a deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  -- A purged stream stays purged: mark it for every perspective, so a later event is skipped, not applied.
  v_n := COALESCE(array_length(v_ids, 1), 0);
  IF NOT p_dry_run THEN
    INSERT INTO __SCHEMA__.wh_stream_purge_markers (stream_id, perspective_name, purged_at, purge_event_id, purge_id)
    SELECT s, '*', NOW(), NULL, p_purge_id FROM unnest(v_ids) s
    ON CONFLICT (stream_id, perspective_name) DO UPDATE
      SET purged_at = EXCLUDED.purged_at, purge_event_id = NULL, purge_id = EXCLUDED.purge_id;

    INSERT INTO __SCHEMA__.wh_stream_purge_audit (purge_id, batch_index, requested_by, reason, stream_ids, row_counts)
    VALUES (p_purge_id, p_batch_index, p_requested_by, p_reason, v_ids, v_counts || jsonb_build_object('wh_stream_purge_markers', v_n))
    ON CONFLICT (purge_id, batch_index) DO NOTHING;
  END IF;
  purged_table := 'wh_stream_purge_markers'; purged_rows := v_n; RETURN NEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_purge_streams IS
'Operator stream purge (#1030): removes every row of the listed streams from this service''s store in the caller''s transaction, marks them purged for every perspective, and audits the batch. p_dry_run counts the same rows and changes nothing. Returns one row per table. 214: the refolded epoch range leaves out at-most-once events (flags & 32), which no epoch holds. 260: and the streams'' wh_stream_lifecycle rows (#1732). 262: and the cursors a whole-stream archive parked (#1736).';
