-- Migration: 031_ClaimingIndexes.sql
-- Date: 2026-03-12
-- Description: Partial indexes to speed up orphan claiming and process_work_batch queries.
--              Covers claiming (processed_at IS NULL), stream-pending subqueries,
--              instance_id return queries, receptor claiming, and perspective cleanup.
-- Dependencies: 024_ClaimOrphanedOutbox.sql, 025_ClaimOrphanedInbox.sql, 026_ClaimOrphanedReceptorWork.sql

-- Claiming indexes: match WHERE processed_at IS NULL filter in claim_orphaned_outbox/inbox
CREATE INDEX IF NOT EXISTS idx_outbox_unprocessed_claiming
ON wh_outbox (partition_number, instance_id, lease_expiry)
WHERE processed_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_inbox_unprocessed_claiming
ON wh_inbox (partition_number, instance_id, lease_expiry)
WHERE processed_at IS NULL;

-- Stream-pending indexes: support NOT EXISTS ordering subqueries in claim functions
CREATE INDEX IF NOT EXISTS idx_outbox_stream_pending
ON wh_outbox (stream_id)
WHERE (status & 4) != 4;

CREATE INDEX IF NOT EXISTS idx_inbox_stream_pending
ON wh_inbox (stream_id)
WHERE (status & 2) != 2;

-- Instance ID indexes: support Phase 7 return queries filtering on instance_id
CREATE INDEX IF NOT EXISTS idx_outbox_instance_id
ON wh_outbox (instance_id)
WHERE instance_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_inbox_instance_id
ON wh_inbox (instance_id)
WHERE instance_id IS NOT NULL;

-- Receptor claiming index: support receptor return + claim_orphaned_receptor_work queries
CREATE INDEX IF NOT EXISTS idx_receptor_processing_claim
ON wh_receptor_processing (instance_id, lease_expiry)
WHERE completed_at IS NULL;

-- Stream-blocking indexes: support NOT EXISTS subquery in Phase 7 return queries
-- Covers the "is there an earlier unprocessed message with scheduled_for > now?" check
CREATE INDEX IF NOT EXISTS idx_outbox_stream_blocked
ON wh_outbox (stream_id, created_at)
WHERE processed_at IS NULL AND scheduled_for IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_inbox_stream_blocked
ON wh_inbox (stream_id, received_at)
WHERE processed_at IS NULL AND scheduled_for IS NOT NULL;

-- Perspective events processed index: support cleanup/anti-join queries
CREATE INDEX IF NOT EXISTS idx_perspective_events_processed
ON wh_perspective_events (stream_id, perspective_name)
WHERE processed_at IS NOT NULL;

-- Stream lock index: support Phase 7 perspective work query filtering on stream_lock columns
CREATE INDEX IF NOT EXISTS idx_perspective_cursors_stream_lock
ON wh_perspective_cursors (stream_lock_instance_id, stream_lock_expiry)
WHERE stream_lock_instance_id IS NOT NULL;
