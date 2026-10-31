-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 269_ArchiveObjectDeletionQueue.sql
-- Date: 2026-10-29
-- Description: The archive object deletion queue, wh_archive_object_deletions (#1737, WS-3 S8): every archive object
--              a manifest row stops naming is queued for deletion in the same transaction, so no object is ever left
--              unreachable in blob storage.
--
--              A manifest row (268) goes away three ways: a restore and a delete of the store remove it, and a stream
--              purge removes it by the cascade from the stream's lifecycle row. A statement trigger on
--              wh_archive_manifest queues each removed row's locator (store, container and object name) in the
--              transaction that removed it, so a purge that rolls back queues nothing and one that commits always
--              has its objects queued. The trigger, rather than a copy of wh_purge_streams, covers every way a row
--              can go, today's and any later one, without redefining the purge.
--
--              The Azure Blob store drains the queue: right after its own restore or delete, and from its
--              maintenance step under the maintainer duty. A row is removed only once storage confirms the object
--              is gone (a missing object counts); a failed delete keeps the row, counts the attempt, records the error
--              and waits before the next try (one minute, doubling, at most an hour). A write that uploads an
--              object of the same name again (the same events archived again: names follow content) takes the row
--              back out of the queue, under the stream's lock, so a live object is never deleted.
--
--              A service with only the Postgres-table store never has a manifest row, so nothing is ever queued.
--
--              Number 269: reserved for this change.
--
-- Dependencies: 268 (wh_archive_manifest)
-- Objects: wh_archive_object_deletions, idx_archive_object_deletions_due, _wh_queue_archive_object_deletions,
--          wh_archive_manifest_queues_object_deletions_trg
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_archive_object_deletions (
  locator         TEXT NOT NULL PRIMARY KEY,
  stream_id       UUID NOT NULL,
  queued_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  attempts        INTEGER NOT NULL DEFAULT 0,
  next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_error      TEXT NULL
);

-- The due rows, in the order a drain takes them.
CREATE INDEX IF NOT EXISTS idx_archive_object_deletions_due
  ON __SCHEMA__.wh_archive_object_deletions (next_attempt_at);

COMMENT ON TABLE __SCHEMA__.wh_archive_object_deletions IS
  '269 (#1737): archive objects no manifest row names any more, queued for deletion from blob storage in the '
  'transaction that removed the row (a restore, a delete, or a purge by cascade). A row goes once storage confirms '
  'the object is gone; a failed delete counts the attempt, keeps the error and waits (1 minute doubling, at most 1 hour).';

-- <docs>fundamentals/events/archive-store#purging-a-stream-archived-to-blob</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Purge_QueuesTheObjectsItsManifestRowsNamed_InItsOwnTransactionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Purge_OfATableStoreArchive_QueuesNothingAsync</tests>
-- <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Purge_QueuesTheObjects_AndTheStepDeletesThemAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_queue_archive_object_deletions() RETURNS TRIGGER AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_archive_object_deletions (locator, stream_id)
  SELECT g.locator, g.stream_id FROM gone g
  ON CONFLICT (locator) DO NOTHING;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_queue_archive_object_deletions() IS
'Statement trigger on wh_archive_manifest (#1737): queues the locator of every removed manifest row in wh_archive_object_deletions, in the removing transaction, so the object is deleted from blob storage and never left unreachable.';

DROP TRIGGER IF EXISTS wh_archive_manifest_queues_object_deletions_trg ON __SCHEMA__.wh_archive_manifest;
CREATE TRIGGER wh_archive_manifest_queues_object_deletions_trg
  AFTER DELETE ON __SCHEMA__.wh_archive_manifest
  REFERENCING OLD TABLE AS gone
  FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__._wh_queue_archive_object_deletions();
