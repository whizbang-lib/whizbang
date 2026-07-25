-- Migration: 048_AddSnapshotCommitSequence.sql
-- Date: 2026-05-23 (defensive rewrite 2026-05-26)
-- Description: Slice 26.11 — adds snapshot_commit_sequence + supporting partial index to
--              wh_perspective_snapshots so rewinds can locate snapshots by commit_sequence
--              (deterministic across live + replay paths). Idempotent ALTER + CREATE INDEX
--              for both fresh DBs and existing consumer databases whose wh_perspective_snapshots
--              was created before slice 26.
-- Dependencies: wh_perspective_snapshots table (created via PerspectiveSnapshotsSchema
--               by the generated schema-init code at startup).

DO $$
DECLARE
  v_schema TEXT := '__SCHEMA__';
  v_table_exists BOOLEAN;
  v_column_exists BOOLEAN;
BEGIN
  -- 2026-05-26 defensive rewrite: split table-existence + column-existence checks into
  -- explicit booleans, then gate BOTH the ALTER and the CREATE INDEX on column-existence.
  -- The previous version's CREATE INDEX ran unconditionally after the DO block; when the
  -- ALTER inside the IF was skipped for any reason (schema mismatch, transient pgcatalog
  -- visibility), the CREATE INDEX referenced a non-existent column and threw 42703.
  SELECT EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = v_schema AND table_name = 'wh_perspective_snapshots'
  ) INTO v_table_exists;

  IF NOT v_table_exists THEN
    -- Schema-init runs after migrations in some test paths; the table will materialize
    -- later, and slice 26.11's column is created in the generated schema for any DB
    -- where the schema-init code is current. Leave nothing for the migration to do.
    RETURN;
  END IF;

  SELECT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = v_schema
      AND table_name = 'wh_perspective_snapshots'
      AND column_name = 'snapshot_commit_sequence'
  ) INTO v_column_exists;

  IF NOT v_column_exists THEN
    EXECUTE format(
      'ALTER TABLE %I.wh_perspective_snapshots ADD COLUMN snapshot_commit_sequence BIGINT',
      v_schema);
  END IF;

  -- Re-check after the conditional ALTER so the index creation only runs against a
  -- column that's actually present.
  SELECT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = v_schema
      AND table_name = 'wh_perspective_snapshots'
      AND column_name = 'snapshot_commit_sequence'
  ) INTO v_column_exists;

  IF v_column_exists THEN
    EXECUTE format(
      'CREATE INDEX IF NOT EXISTS idx_perspective_snapshots_commit_sequence '
      'ON %I.wh_perspective_snapshots (stream_id, perspective_name, snapshot_commit_sequence) '
      'WHERE snapshot_commit_sequence IS NOT NULL',
      v_schema);

    EXECUTE format(
      'COMMENT ON COLUMN %I.wh_perspective_snapshots.snapshot_commit_sequence IS %L',
      v_schema,
      'Slice 26.11 — commit_sequence as of this snapshot. NULL for snapshots created before slice 26 shipped; rewinds for such snapshots fall back to event_id-anchored lookup. NEW snapshots populated by the runner template via IEventStore.GetCommitSequenceAsync.');
  END IF;
END $$;
