-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 268_ArchiveManifest.sql
-- Date: 2026-10-29
-- Description: The archive manifest, wh_archive_manifest (#1737, WS-3 S8): one row per archive object a store
--              outside the database holds (the Azure Blob store's chunks), written in the same transaction that
--              removes the events from the hot store.
--
--              The manifest is what makes an object visible. A blob store uploads a stream's chunks while the
--              database transaction that moved the events into wh_event_archive is still open, writes one row per
--              chunk, removes the staged rows and commits. A failure before the commit rolls everything back, so the
--              stream stays fully hot and any chunk already uploaded is referenced by nothing; a retry writes the same
--              bytes under the same name (the name carries the content hash), so it can never be listed twice.
--              Readers, restores and deletes go through these rows only, never through a listing of the container.
--
--              Each row records the chunk's own locator (store, address, codec, format), its version range and event
--              count, its byte length and SHA-256 (checked on every read, so a corrupted or truncated object is
--              detected rather than decoded), and the origin generation of the archive that wrote it.
--
--              The stream's lifecycle row (wh_stream_lifecycle, 260) is the parent: every archive writes it first
--              (262's wh_archive_stream), and a stream purge deletes it, which takes the manifest rows with it
--              (ON DELETE CASCADE), so a purged stream can never be restored from an object that outlived it. The key
--              of a lifecycle row never changes, so the hot path's updates to it queue no check for this key.
--
--              Number 268: reserved for this change.
--
-- Dependencies: 260 (wh_stream_lifecycle), 262 (wh_archive_stream, the tombstone)
-- Objects: wh_archive_manifest, fk_archive_manifest_stream, ck_archive_manifest_range
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_archive_manifest (
  stream_id         UUID NOT NULL,
  first_version     BIGINT NOT NULL,
  last_version      BIGINT NOT NULL,
  event_count       INTEGER NOT NULL,
  locator           TEXT NOT NULL,
  byte_length       BIGINT NOT NULL,
  sha256            TEXT NOT NULL,
  origin_generation BIGINT NOT NULL,
  archived_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (stream_id, first_version),
  CONSTRAINT fk_archive_manifest_stream FOREIGN KEY (stream_id)
    REFERENCES __SCHEMA__.wh_stream_lifecycle (stream_id) ON DELETE CASCADE,
  CONSTRAINT ck_archive_manifest_range CHECK (last_version >= first_version AND event_count > 0 AND byte_length > 0)
);

COMMENT ON TABLE __SCHEMA__.wh_archive_manifest IS
  '268 (#1737): one row per archive object a store outside the database holds (the Azure Blob store''s chunks), '
  'written in the transaction that removes the events from the hot store, so an object is visible only once its '
  'stream''s archive committed. Each row carries the chunk''s locator (store, address, codec, format), version range, '
  'event count, byte length and SHA-256 (checked on every read). A purge deletes the stream''s lifecycle row and, by '
  'ON DELETE CASCADE, these rows.';
