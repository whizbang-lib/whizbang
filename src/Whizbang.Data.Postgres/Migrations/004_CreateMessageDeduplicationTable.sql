-- Migration 006: Create wh_message_deduplication table for permanent deduplication tracking
-- Date: 2025-12-07
-- Description: Tracks all message IDs ever received for idempotent delivery guarantees (never deleted)

CREATE TABLE IF NOT EXISTS wh_message_deduplication (
  message_id UUID NOT NULL PRIMARY KEY,
  first_seen_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_message_dedup_first_seen ON wh_message_deduplication(first_seen_at);
