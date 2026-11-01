-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 275_SnapshotsRecordTheirCollectives.sql
-- Date: 2026-11-01
-- Description: A perspective snapshot records the commit sequence through which its row's collectives were complete
--              when it was taken (#1963).
--
--              A live snapshot is the row as the live apply read it, and the collective sink applies collectives to
--              rows on its own, a key at a time and a batch at a time. A rewind from the snapshot decided which
--              collectives the snapshot already held by the snapshot's event alone, so a collective committed around
--              that event could be folded twice or not at all. The snapshot now records the mark the runner takes when
--              it writes it: every collective of the row with a commit sequence at or below it was applied in full, and
--              a rewind or a rebuild from the snapshot folds the ones past it. A collective still in flight counts as
--              past it, so the next snapshot, taken once it completes, includes it.
--
--              NULL on every snapshot written before this migration, and on a snapshot a store writes without the
--              mark: those keep the snapshot-event rule. Additive and nullable with no default, so no row is rewritten,
--              and not indexed: the mark is read with the snapshot, never searched by.
--
--              Number 275: reserved for this change.
--
-- Dependencies: 209 (snapshot_version), 271 (archived_through_version)
-- Objects: collectives_through_sequence
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- The snapshots table is created by the schema pass (PerspectiveSnapshotsSchema, which carries the column too), which may
-- not have run yet on a first start; the column is added here only where the table already exists.
DO $do$
BEGIN
  IF to_regclass('__SCHEMA__.wh_perspective_snapshots') IS NOT NULL THEN
    ALTER TABLE __SCHEMA__.wh_perspective_snapshots ADD COLUMN IF NOT EXISTS collectives_through_sequence BIGINT NULL;
    COMMENT ON COLUMN __SCHEMA__.wh_perspective_snapshots.collectives_through_sequence IS
      'The commit sequence through which the row''s collectives were complete when the snapshot was taken (275, #1963): a '
      'rewind or a rebuild from it folds the collectives past it. NULL when the snapshot records none, which keeps the '
      'snapshot-event rule.';
  END IF;
END
$do$;
