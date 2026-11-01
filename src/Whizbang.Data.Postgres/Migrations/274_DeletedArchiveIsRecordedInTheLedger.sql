-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 274_DeletedArchiveIsRecordedInTheLedger.sql
-- Date: 2026-11-01
-- Description: The stream lifecycle ledger records that a stream's archive was deleted (#1958), so a rebuild can tell a
--              stream that never had an archive from one whose archived history is gone.
--
--              Deleting an archive (wh_delete_stream_archive, 262) clears the ledger row's archive_locator, which is what
--              says where an archive is. A stream archived through a prefix stays Active, so once its archive was deleted
--              its row read exactly as a stream that was never archived: a rebuild replayed it from its first hot event and
--              folded a partial history as the whole. The new archive_deleted_at column is stamped by the delete whenever
--              the row named an archive, and nothing clears it: the deleted events are gone for good, so a stream archived
--              again afterwards, or restored from that later archive, still lacks them. A delete of a stream with no
--              archive (never archived, or restored in full) deletes nothing and stamps nothing.
--
--              A whole-stream archive leaves its row Archived, so its deletion was already visible as an Archived row with
--              no locator; that row is stamped too from now on. A prefix archive deleted before this migration left no
--              trace in the ledger and is not stamped here.
--
--              No index changes: the rebuild's lookup is by the ledger's key, and an append never writes the column, so
--              the ledger's updates stay heap-only.
--
--              Number 274: reserved for this change.
--
-- Dependencies: 260 (wh_stream_lifecycle), 262 (last word of wh_delete_stream_archive)
-- Objects: archive_deleted_at, wh_delete_stream_archive
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_stream_lifecycle ADD COLUMN IF NOT EXISTS archive_deleted_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.archive_deleted_at IS
  'When the stream''s archive was deleted (#1958); NULL when no archive of it was ever deleted. Never cleared: the deleted history is gone for good, whatever is archived or restored afterwards.';

SELECT __SCHEMA__.drop_all_overloads('wh_delete_stream_archive');

-- <docs>fundamentals/events/archive-store#deleting-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Delete_RemovesTheArchivedCopy_TheTombstoneStillBlocksReuseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Extents_TellAStreamWhoseArchiveWasDeleted_FromOneThatNeverHadAnArchiveAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_delete_stream_archive(p_stream_id UUID)
RETURNS BIGINT AS $$
DECLARE
  v_deleted BIGINT;
BEGIN
  DELETE FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  DELETE FROM __SCHEMA__.wh_event_archive_cursors p WHERE p.stream_id = p_stream_id;
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
'Deletes a stream''s archived events and parked cursors for good and clears the tombstone''s locator; the tombstone itself stays, so the stream''s versions are never reused (#1736). A row that named an archive is stamped archive_deleted_at, so a rebuild can tell its lost history from a stream never archived (#1958). Returns the archived events deleted.';
