-- Migration: 031_ClaimingIndexes.sql
-- Date: 2026-03-12
-- Description: Partial indexes to speed up orphan claiming and process_work_batch queries.
--              Covers claiming (processed_at IS NULL), stream-pending subqueries,
--              instance_id return queries, receptor claiming, and perspective cleanup.
-- Dependencies: 024_ClaimOrphanedOutbox.sql, 025_ClaimOrphanedInbox.sql, 026_ClaimOrphanedReceptorWork.sql

-- Claiming indexes: match WHERE processed_at IS NULL filter in claim_orphaned_outbox/inbox
CREATE INDEX IF NOT EXISTS idx_outbox_unprocessed_claiming
ON __SCHEMA__.wh_outbox (partition_number, instance_id, lease_expiry)
WHERE processed_at IS NULL;

-- 162 moves instance_id, lease_expiry, partition_number, processed_at to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'processed_at' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_unprocessed_claiming
    ON __SCHEMA__.wh_inbox (partition_number, instance_id, lease_expiry)
    WHERE processed_at IS NULL;
  END IF;
END $$;

-- Stream-pending indexes: support NOT EXISTS ordering subqueries in claim functions
CREATE INDEX IF NOT EXISTS idx_outbox_stream_pending
ON __SCHEMA__.wh_outbox (stream_id)
WHERE (status & 4) != 4;

-- 162 moves status to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'status' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_stream_pending
    ON __SCHEMA__.wh_inbox (stream_id)
    WHERE (status & 2) != 2;
  END IF;
END $$;

-- Instance ID indexes: support Phase 7 return queries filtering on instance_id
CREATE INDEX IF NOT EXISTS idx_outbox_instance_id
ON __SCHEMA__.wh_outbox (instance_id)
WHERE instance_id IS NOT NULL;

-- 162 moves instance_id to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'instance_id' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_instance_id
    ON __SCHEMA__.wh_inbox (instance_id)
    WHERE instance_id IS NOT NULL;
  END IF;
END $$;

-- Receptor claiming index: support receptor return + claim_orphaned_receptor_work queries
CREATE INDEX IF NOT EXISTS idx_receptor_processing_claim
ON __SCHEMA__.wh_receptor_processing (instance_id, lease_expiry)
WHERE completed_at IS NULL;

-- Stream-blocking indexes: support NOT EXISTS subquery in Phase 7 return queries
-- Covers the "is there an earlier unprocessed message with scheduled_for > now?" check
CREATE INDEX IF NOT EXISTS idx_outbox_stream_blocked
ON __SCHEMA__.wh_outbox (stream_id, created_at)
WHERE processed_at IS NULL AND scheduled_for IS NOT NULL;

-- 162 moves processed_at, scheduled_for to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'processed_at' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_stream_blocked
    ON __SCHEMA__.wh_inbox (stream_id, received_at)
    WHERE processed_at IS NULL AND scheduled_for IS NOT NULL;
  END IF;
END $$;

-- Perspective events processed index: support cleanup/anti-join queries
CREATE INDEX IF NOT EXISTS idx_perspective_events_processed
ON __SCHEMA__.wh_perspective_events (stream_id, perspective_name)
WHERE processed_at IS NOT NULL;

-- Stream lock index: support Phase 7 perspective work query filtering on stream_lock columns
CREATE INDEX IF NOT EXISTS idx_perspective_cursors_stream_lock
ON __SCHEMA__.wh_perspective_cursors (stream_lock_instance_id, stream_lock_expiry)
WHERE stream_lock_instance_id IS NOT NULL;
