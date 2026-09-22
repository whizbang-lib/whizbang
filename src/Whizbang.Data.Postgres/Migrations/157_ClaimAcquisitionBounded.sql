-- Migration: 157_ClaimAcquisitionBounded.sql
-- Date: 2026-09-16
-- Description: The outbox and perspective acquisitions read a few batches, not the backlog.
--
--              Measured under a bulk load, one claim poll read tens of thousands of blocks and the
--              queue tables were scanned whole several times per poll on every instance, so the poll
--              alone took most of the database's cores and the backlog grew because of it. The
--              inbox acquisition had been bounded by 138, 145 and 150; the other two had not:
--
--              claim_orphaned_outbox (148) orders its candidates by created_at and stops at
--              p_max_rows, but no index carried that order over the pending rows, so the planner
--              scanned every pending row, sorted them all, and kept the first batch. The
--              arrival-order covering index here lets the same statement walk pending rows in order
--              and stop at the batch, the shape 138 gave the inbox.
--
--              claim_orphaned_perspective_events (150) chose the most urgent streams by aggregating
--              every claimable event. 150 now selects them from a bounded window: the most urgent
--              claimable-looking events in (priority, event_id) order, walked from the urgency index
--              here with an early stop. The two indexes are the substrate; 150 carries the rewrite.
--
-- Dependencies: 148 (claim_orphaned_outbox), 149 (wh_perspective_events.priority), 150 (claim_orphaned_perspective_events)
-- Objects: idx_outbox_pending_arrival, idx_perspective_event_urgency

CREATE INDEX IF NOT EXISTS idx_outbox_pending_arrival
  ON __SCHEMA__.wh_outbox (created_at, message_id)
  INCLUDE (stream_id, instance_id, lease_expiry, scheduled_for, partition_number)
  WHERE processed_at IS NULL AND coalesce_group IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_outbox_pending_arrival IS
  'Arrival-order covering partial index over pending outbox singles (157), so claim_orphaned_outbox walks '
  'candidates in created_at order and stops at its row bound instead of sorting every pending row per poll.';

CREATE INDEX IF NOT EXISTS idx_perspective_event_urgency
  ON __SCHEMA__.wh_perspective_events (priority, event_id)
  INCLUDE (event_work_id, stream_id, perspective_name, instance_id, lease_expiry, scheduled_for, partition_number)
  WHERE processed_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_perspective_event_urgency IS
  'Urgency-order covering partial index over pending perspective events (157), so '
  'claim_orphaned_perspective_events chooses the most urgent streams from a bounded window of events '
  'instead of aggregating every claimable event per poll.';
