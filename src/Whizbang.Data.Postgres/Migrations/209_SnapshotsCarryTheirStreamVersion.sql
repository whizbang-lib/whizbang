-- Migration: 209_SnapshotsCarryTheirStreamVersion.sql
-- Date: 2026-10-13
-- Description: A perspective snapshot records the stream version it was taken at, and the two places that
--              ask "is this snapshot before/past that event" compare versions instead of commit sequences.
--
--              Commit sequence is assignment order across streams, not order within one. A row stamped
--              after the fact carries a sequence far below rows written long before it, so within a single
--              stream the two orders can run opposite to each other. Both callers read that as stream
--              order and both get it wrong in the direction that loses data:
--
--              Rewind selection picked "the latest snapshot below the triggering event's commit sequence",
--              which can name a snapshot LATER in the stream than the event. Restoring there and replaying
--              forward never applies the events in between, and the row stays permanently short of them.
--
--              The reap's snapshot-coverage gate asked whether a consuming perspective had a snapshot at
--              or past the event's commit sequence before releasing its body. A snapshot EARLIER in the
--              stream could satisfy that, so the body went while it was still the only thing a rewind
--              through that point could replay.
--
--              Version is the stream's own counter, unique per stream by index, and cannot invert. Rows
--              written before this migration record no version; both callers treat that as "cannot be
--              placed in the stream" and fall toward more work (a fuller replay, a body kept) rather than
--              guessing. Re-applying an event is recoverable; a read model short of events is not, because
--              nothing is left pending to repair it.
-- Dependencies: 048 (snapshot_commit_sequence + its index), 190 (last word of perform_maintenance)
-- Objects: wh_perspective_snapshots, idx_perspective_snapshots_version, perform_maintenance
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ============================================================================
-- Part 1: wh_perspective_snapshots.snapshot_version
-- ============================================================================
-- Mirrors 048's defensive shape: the table is created by the generated schema-init pass, which may not have
-- run yet on some paths, so do nothing rather than fail, and gate the index on the column actually existing.
DO $do$
DECLARE
  v_schema TEXT := '__SCHEMA__';
  v_table_exists BOOLEAN;
  v_column_exists BOOLEAN;
BEGIN
  SELECT EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = v_schema AND table_name = 'wh_perspective_snapshots'
  ) INTO v_table_exists;

  IF NOT v_table_exists THEN
    RETURN;
  END IF;

  SELECT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = v_schema
      AND table_name = 'wh_perspective_snapshots'
      AND column_name = 'snapshot_version'
  ) INTO v_column_exists;

  IF NOT v_column_exists THEN
    EXECUTE format(
      'ALTER TABLE %I.wh_perspective_snapshots ADD COLUMN snapshot_version INTEGER',
      v_schema);
  END IF;

  SELECT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = v_schema
      AND table_name = 'wh_perspective_snapshots'
      AND column_name = 'snapshot_version'
  ) INTO v_column_exists;

  IF v_column_exists THEN
    EXECUTE format(
      'CREATE INDEX IF NOT EXISTS idx_perspective_snapshots_version '
      'ON %I.wh_perspective_snapshots (stream_id, perspective_name, snapshot_version) '
      'WHERE snapshot_version IS NOT NULL',
      v_schema);

    EXECUTE format(
      'COMMENT ON COLUMN %I.wh_perspective_snapshots.snapshot_version IS %L',
      v_schema,
      'The stream version this snapshot was taken at, which is what orders it against the stream. Commit sequence is assignment order and inverts against the stream for retroactively stamped rows, so it cannot answer whether a snapshot precedes an event. NULL on rows written before this migration; a NULL is never treated as placeable, so selection replays further and the reap keeps the body.');
  END IF;
END $do$;

-- Existing rows are left NULL rather than backfilled. The version is a property of the event the snapshot
-- was taken at, so a backfill is a join against wh_event_store over every snapshot row on every consumer's
-- database at boot, to repopulate rows that the next snapshot on that stream replaces anyway. The NULL path
-- is already the safe one on both sides, so the cost buys nothing but a slower startup.

