-- Migration: 083_DestructionHoldFailureCount.sql
-- Date: 2026-07-17
-- Description: E2-5 — destruction failure retry policy. A throwing PreDestruction hook no longer fails open;
--              the batch is held for a backoff (retried next cycle) up to a cap, then force-deleted. This adds
--              the attempt counter to the E2-3 hold table (wh_event_destruction_hold). RecordDestructionFailure
--              (coordinator, inline SQL) increments it per event and, once it exceeds the cap, sets hold_until
--              to '-infinity' so Task 8's `hold_until > NOW()` gate lets the reaper force-delete the batch.
-- Dependencies: 079 (wh_event_destruction_hold), 081 (Task 8 hold gate)

ALTER TABLE __SCHEMA__.wh_event_destruction_hold
  ADD COLUMN IF NOT EXISTS failure_count INTEGER NOT NULL DEFAULT 0;
