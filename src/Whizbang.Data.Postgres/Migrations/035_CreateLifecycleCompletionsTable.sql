-- Migration 035: Create lifecycle_completions table
--
-- Durable marker recording that PostLifecycle fired for an event.
-- Used by startup reconciliation to detect events where all perspectives
-- completed but PostLifecycle never fired (e.g., due to process crash
-- or stale-tracking cleanup race condition).
--
-- Rows are inserted after PostLifecycleInline fires successfully.
-- Periodic cleanup deletes entries older than 7 days.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_lifecycle_completions (
    event_id UUID NOT NULL PRIMARY KEY,
    instance_id UUID NOT NULL,
    completed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_lifecycle_completions_completed_at
    ON __SCHEMA__.wh_lifecycle_completions (completed_at);
