-- Migration: 031_ClaimingIndexes.sql
-- Date: 2026-03-12
-- Description: Partial indexes to speed up orphan claiming when completed rows exist.
--              Matches the WHERE processed_at IS NULL filter in claim_orphaned_outbox/inbox.
-- Dependencies: 024_ClaimOrphanedOutbox.sql, 025_ClaimOrphanedInbox.sql

CREATE INDEX IF NOT EXISTS idx_outbox_unprocessed_claiming
ON wh_outbox (partition_number, instance_id, lease_expiry)
WHERE processed_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_inbox_unprocessed_claiming
ON wh_inbox (partition_number, instance_id, lease_expiry)
WHERE processed_at IS NULL;
