-- =============================================
-- Create wh_receptor_processing table
-- Tracks which receptors have processed which events (log-style)
-- =============================================

CREATE TABLE IF NOT EXISTS wh_receptor_processing (
  id UUID PRIMARY KEY,
  event_id UUID NOT NULL,
  receptor_name TEXT NOT NULL,
  status SMALLINT NOT NULL DEFAULT 0,
  attempts INT NOT NULL DEFAULT 0,
  error TEXT,
  started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  processed_at TIMESTAMPTZ,

  -- Foreign key to event store
  CONSTRAINT fk_receptor_processing_event FOREIGN KEY (event_id) REFERENCES wh_event_store(id) ON DELETE CASCADE,

  -- Unique constraint: each receptor can only process an event once
  CONSTRAINT uq_receptor_processing_event_receptor UNIQUE (event_id, receptor_name)
);

-- Index for querying by event
CREATE INDEX IF NOT EXISTS idx_receptor_processing_event_id ON wh_receptor_processing(event_id);

-- Index for querying by receptor
CREATE INDEX IF NOT EXISTS idx_receptor_processing_receptor_name ON wh_receptor_processing(receptor_name);

-- Index for finding failed processing
CREATE INDEX IF NOT EXISTS idx_receptor_processing_status ON wh_receptor_processing(status) WHERE status & 4 = 4; -- Failed flag
