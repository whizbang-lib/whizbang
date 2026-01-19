-- Migration: 009_CreatePerspectiveEventsTable.sql
-- Date: 2025-12-25
-- Description: Creates wh_perspective_events table for ephemeral per-event work tracking.
--              Follows the same inbox/outbox pattern for unified work coordination.
--              Also removes lease columns from wh_perspective_checkpoints (moved to events table).
-- Dependencies: 001-008 (requires wh_event_store and wh_perspective_checkpoints tables)

CREATE TABLE IF NOT EXISTS wh_perspective_events (
  -- Identity
  event_work_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  stream_id UUID NOT NULL,
  perspective_name VARCHAR(200) NOT NULL,

  -- Event reference (UUIDv7 provides temporal ordering)
  event_id UUID NOT NULL,

  -- Lease management (follows inbox/outbox pattern)
  instance_id UUID,
  lease_expiry TIMESTAMPTZ,

  -- Status tracking
  status INTEGER NOT NULL DEFAULT 0,  -- MessageProcessingStatus flags
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT,

  -- Timestamps
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  claimed_at TIMESTAMPTZ,
  processed_at TIMESTAMPTZ,

  -- Retry support
  scheduled_for TIMESTAMPTZ,
  failure_reason INTEGER,  -- FailureReason enum

  -- Constraints
  -- Note: No FK to wh_event_store(stream_id) because stream_id is not unique in that table
  -- Stream ownership is enforced via wh_active_streams table
  CONSTRAINT uq_perspective_event UNIQUE (stream_id, perspective_name, event_id)
);

-- Index for claiming work (used by claim_orphaned_perspective_events)
CREATE INDEX IF NOT EXISTS idx_perspective_event_claim
ON wh_perspective_events (instance_id, lease_expiry, scheduled_for)
WHERE processed_at IS NULL;

-- Index for ordering within stream/perspective (ensures sequential processing via UUIDv7)
CREATE INDEX IF NOT EXISTS idx_perspective_event_order
ON wh_perspective_events (stream_id, perspective_name, event_id);

COMMENT ON TABLE wh_perspective_events IS
'Ephemeral event tracking for perspective processing. Events are created when new events arrive for a perspective, leased by workers, and deleted after processing (unless debug mode).';

COMMENT ON COLUMN wh_perspective_events.event_work_id IS 'Unique identifier for this work item';
COMMENT ON COLUMN wh_perspective_events.stream_id IS 'Stream this event belongs to (for partition-based load balancing)';
COMMENT ON COLUMN wh_perspective_events.perspective_name IS 'Name of the perspective that needs to process this event';
COMMENT ON COLUMN wh_perspective_events.event_id IS 'Event to be processed (references wh_event_store). UUIDv7 provides temporal ordering';
COMMENT ON COLUMN wh_perspective_events.instance_id IS 'Instance that claimed this work (NULL if not claimed)';
COMMENT ON COLUMN wh_perspective_events.lease_expiry IS 'When the lease expires (NULL if not claimed)';
COMMENT ON COLUMN wh_perspective_events.status IS 'Processing status flags (MessageProcessingStatus)';
COMMENT ON COLUMN wh_perspective_events.attempts IS 'Number of processing attempts (for exponential backoff)';
COMMENT ON COLUMN wh_perspective_events.error IS 'Error message if processing failed';
COMMENT ON COLUMN wh_perspective_events.created_at IS 'When this work item was created';
COMMENT ON COLUMN wh_perspective_events.claimed_at IS 'When this work item was claimed by an instance';
COMMENT ON COLUMN wh_perspective_events.processed_at IS 'When this work item was processed (NULL if not processed)';
COMMENT ON COLUMN wh_perspective_events.scheduled_for IS 'When to retry this work item (for failed work with backoff)';
COMMENT ON COLUMN wh_perspective_events.failure_reason IS 'Reason for failure (FailureReason enum)';

-- Remove lease management from wh_perspective_checkpoints
-- (lease management has moved to wh_perspective_events)
ALTER TABLE wh_perspective_checkpoints
  DROP COLUMN IF EXISTS claimed_by_instance_id,
  DROP COLUMN IF EXISTS claimed_at;

-- Drop index that used lease columns
DROP INDEX IF EXISTS idx_perspective_checkpoint_claim;

-- Update table comment to reflect new purpose
COMMENT ON TABLE wh_perspective_checkpoints IS
'Persistent checkpoint tracking for perspectives. Records last processed event ID and completion status. Work item leasing moved to wh_perspective_events table.';
