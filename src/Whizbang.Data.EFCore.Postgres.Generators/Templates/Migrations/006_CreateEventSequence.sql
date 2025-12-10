-- Migration 008: Create wh_event_sequence for global event ordering
-- Date: 2025-12-07
-- Description: Global sequence for event ordering across all streams

CREATE SEQUENCE IF NOT EXISTS wh_event_sequence START WITH 1 INCREMENT BY 1;
