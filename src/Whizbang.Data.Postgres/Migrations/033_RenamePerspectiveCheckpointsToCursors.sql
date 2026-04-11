-- Migration: 033_RenamePerspectiveCheckpointsToCursors.sql
-- Date: 2026-03-17
-- Description: Renames wh_perspective_checkpoints table to wh_perspective_cursors.
--              Temporary compatibility migration — remove after next release.
-- Dependencies: 029

DO $$ BEGIN
  -- Only act if the OLD table exists (upgrade scenario)
  IF EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = '__SCHEMA__' AND table_name = 'wh_perspective_checkpoints'
  ) THEN
    -- If the new table already exists (created by infrastructure schema),
    -- migrate data from old → new, then drop old table
    IF EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = '__SCHEMA__' AND table_name = 'wh_perspective_cursors'
    ) THEN
      INSERT INTO __SCHEMA__.wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at, error)
        SELECT stream_id, perspective_name, last_event_id, status, processed_at, error
        FROM __SCHEMA__.wh_perspective_checkpoints
        ON CONFLICT (stream_id, perspective_name) DO NOTHING;
      DROP TABLE __SCHEMA__.wh_perspective_checkpoints;
    ELSE
      ALTER TABLE __SCHEMA__.wh_perspective_checkpoints RENAME TO wh_perspective_cursors;
    END IF;

    -- Rename indexes if they exist (and new names don't already exist)
    IF EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'idx_perspective_checkpoints_perspective_name')
       AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'idx_perspective_cursors_perspective_name') THEN
      ALTER INDEX idx_perspective_checkpoints_perspective_name RENAME TO idx_perspective_cursors_perspective_name;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'idx_perspective_checkpoints_last_event_id')
       AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'idx_perspective_cursors_last_event_id') THEN
      ALTER INDEX idx_perspective_checkpoints_last_event_id RENAME TO idx_perspective_cursors_last_event_id;
    END IF;
  END IF;
END $$;

-- Add new columns for rewind and locking support (safe on both fresh and upgraded DBs)
-- IF EXISTS handles the case where neither old nor new table exists yet (version mismatch)
ALTER TABLE IF EXISTS __SCHEMA__.wh_perspective_cursors
  ADD COLUMN IF NOT EXISTS rewind_trigger_event_id UUID,
  ADD COLUMN IF NOT EXISTS rewind_flagged_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS stream_lock_instance_id UUID,
  ADD COLUMN IF NOT EXISTS stream_lock_expiry TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS stream_lock_reason VARCHAR(50);
