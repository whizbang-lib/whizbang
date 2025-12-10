-- =============================================
-- Create wh_perspective_checkpoints table
-- Tracks last processed event per stream per perspective (checkpoint-style)
-- Enables time-travel scenarios where perspectives catch up independently
-- =============================================

CREATE TABLE IF NOT EXISTS wh_perspective_checkpoints (
  stream_id UUID NOT NULL,
  perspective_name TEXT NOT NULL,
  last_event_id UUID NOT NULL,
  status SMALLINT NOT NULL DEFAULT 0,
  processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  error TEXT,

  -- Composite primary key: one checkpoint per stream per perspective
  CONSTRAINT pk_perspective_checkpoints PRIMARY KEY (stream_id, perspective_name),

  -- Foreign key to event store (last processed event)
  CONSTRAINT fk_perspective_checkpoints_event FOREIGN KEY (last_event_id) REFERENCES wh_event_store(id) ON DELETE RESTRICT
);

-- Index for querying by perspective
CREATE INDEX IF NOT EXISTS idx_perspective_checkpoints_perspective_name ON wh_perspective_checkpoints(perspective_name);

-- Index for finding perspectives that need catching up
CREATE INDEX IF NOT EXISTS idx_perspective_checkpoints_catching_up ON wh_perspective_checkpoints(status) WHERE status & 8 = 8; -- CatchingUp flag

-- Index for finding failed perspectives
CREATE INDEX IF NOT EXISTS idx_perspective_checkpoints_failed ON wh_perspective_checkpoints(status) WHERE status & 4 = 4; -- Failed flag

-- Index for querying by last event (UUIDv7 - naturally time-ordered)
CREATE INDEX IF NOT EXISTS idx_perspective_checkpoints_last_event_id ON wh_perspective_checkpoints(last_event_id);
