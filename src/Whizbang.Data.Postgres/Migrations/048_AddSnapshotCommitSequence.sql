-- Migration: 048_AddSnapshotCommitSequence.sql
-- Date: 2026-05-23
-- Description: Slice 26.11 — adds snapshot_commit_sequence + supporting partial index to
--              wh_perspective_snapshots so rewinds can locate snapshots by commit_sequence
--              (deterministic across live + replay paths). Idempotent ALTER for existing
--              JDX databases whose wh_perspective_snapshots was created before slice 26.
-- Dependencies: wh_perspective_snapshots table (created via PerspectiveSnapshotsSchema
--               by the generated schema-init code at startup).

DO $$
BEGIN
  -- Use '__SCHEMA__' literal in the existence checks so the EXISTS/NOT EXISTS branches
  -- target the SAME schema as the ALTER and CREATE INDEX below. Earlier versions used
  -- current_schema() in the checks but __SCHEMA__ in the DDL — when the runtime schema
  -- differs from the migration's target schema, the IF returned false (column "missing"
  -- from current_schema()), the ALTER was skipped, then the CREATE INDEX below failed
  -- with 42703 because the column was never added to __SCHEMA__.wh_perspective_snapshots.
  IF EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = '__SCHEMA__' AND table_name = 'wh_perspective_snapshots'
  ) AND NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = '__SCHEMA__'
      AND table_name = 'wh_perspective_snapshots'
      AND column_name = 'snapshot_commit_sequence'
  ) THEN
    ALTER TABLE __SCHEMA__.wh_perspective_snapshots
      ADD COLUMN snapshot_commit_sequence BIGINT;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_perspective_snapshots_commit_sequence
  ON __SCHEMA__.wh_perspective_snapshots (stream_id, perspective_name, snapshot_commit_sequence)
  WHERE snapshot_commit_sequence IS NOT NULL;

COMMENT ON COLUMN __SCHEMA__.wh_perspective_snapshots.snapshot_commit_sequence IS
'Slice 26.11 — commit_sequence as of this snapshot. NULL for snapshots created before slice 26 shipped; rewinds for such snapshots fall back to event_id-anchored lookup. NEW snapshots populated by the runner template via IEventStore.GetCommitSequenceAsync.';
