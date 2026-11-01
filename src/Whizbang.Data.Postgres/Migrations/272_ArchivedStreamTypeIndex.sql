-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 272_ArchivedStreamTypeIndex.sql
-- Date: 2026-11-01
-- Description: The archived-stream index on the stream lifecycle ledger (#1932): the archived streams, keyed by stream
--              type and then tenant.
--
--              A collective replay folds a tenant's collective events into each stream it replays. A collective stream
--              archived whole has no row in the hot store, so the replay lists the tenant's archived streams of the
--              collective types from the ledger (state Archived, stream_type = ANY(types), tenant = the tenant or IS
--              NULL) and reads them through the archive. A rebuild asks once per stream it replays, and the ledger has
--              one row per stream, so the read must not scan it.
--
--              Partial on state = 'Archived': only an archive and a restore change a row's state, and no append
--              changes its type or tenant, so an append's update of the ledger stays heap-only (the table is kept at
--              fillfactor 45 for that) and the index holds only the archived rows the read asks for.
--
--              Number 272: reserved for this change.
--
-- Dependencies: 260 (wh_stream_lifecycle)
-- Objects: idx_stream_lifecycle_archived_type
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE INDEX IF NOT EXISTS idx_stream_lifecycle_archived_type
  ON __SCHEMA__.wh_stream_lifecycle (stream_type, tenant)
  WHERE state = 'Archived';

COMMENT ON INDEX __SCHEMA__.idx_stream_lifecycle_archived_type IS
  '272 (#1932): the archived streams by stream type and tenant, for the collective replay''s lookup of the collective '
  'streams archived whole. Partial on Archived, so an append''s ledger update stays heap-only.';
