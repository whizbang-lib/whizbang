-- Migration: 033_RenamePerspectiveCheckpointsToCursors.sql
-- Date: 2026-03-17
-- Description: Renames wh_perspective_checkpoints table to wh_perspective_cursors.
--              Temporary compatibility migration — remove after next release.
-- Dependencies: 029

ALTER TABLE IF EXISTS __SCHEMA__.wh_perspective_checkpoints
  RENAME TO wh_perspective_cursors;

-- Rename indexes
ALTER INDEX IF EXISTS idx_perspective_checkpoints_perspective_name
  RENAME TO idx_perspective_cursors_perspective_name;
ALTER INDEX IF EXISTS idx_perspective_checkpoints_last_event_id
  RENAME TO idx_perspective_cursors_last_event_id;

-- Add new columns for rewind and locking support
ALTER TABLE __SCHEMA__.wh_perspective_cursors
  ADD COLUMN IF NOT EXISTS rewind_trigger_event_id UUID,
  ADD COLUMN IF NOT EXISTS stream_lock_instance_id UUID,
  ADD COLUMN IF NOT EXISTS stream_lock_expiry TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS stream_lock_reason VARCHAR(50);
