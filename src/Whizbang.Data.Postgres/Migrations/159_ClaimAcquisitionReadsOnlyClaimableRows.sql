-- Migration: 159_ClaimAcquisitionReadsOnlyClaimableRows.sql
-- Date: 2026-09-17
-- Description: The inbox acquisition reads the rows it may take, not every row it may not.
--
--              157 bounded the outbox and perspective acquisitions and 158 bounded both re-offers,
--              and the poll was still the top statement on a fleet under a bulk import. Measured as
--              blocks per ROW RETURNED -- the only ratio that survived contact with production,
--              because a poll returning twelve rows and one returning a hundred cost nearly the
--              same -- a deployed fleet reported 229 to 247 on a consumer-shaped service and 321 to
--              411 on a producer-shaped one, stable over four runs.
--
--              What remained is claim_orphaned_inbox, and the reason the earlier rounds did not
--              find it is that they measured a poll that acquires nothing. Between loads every
--              pending row is either leased by a live peer or held by the poller, so no acquisition
--              runs; during an import there is always unowned work, so all three run on every poll
--              of every instance, several times a second, and they are what the poll costs. The
--              re-offers that produce the rows the ratio counts came to 84 blocks of a 12,499-block
--              poll.
--
--              Inside claim_orphaned_inbox, five lane CTEs (the interactive streams, the commands,
--              the background reservation probe, and the standard and background event lanes) each
--              select from wh_inbox under
--
--                WHERE processed_at IS NULL AND (instance_id IS NULL OR lease_expiry < p_now)
--
--              A disjunction has no index order, so each of the five walked the whole pending inbox
--              and discarded what it could not take: one plan showed 4,716 rows removed by filter
--              out of 4,800 to yield seven streams. Each walk fetched a heap page per row, because
--              the stream-ordered index carries neither is_event nor priority, and because a queue
--              table under load is never all-visible, so nothing there is ever really index-only.
--              Doubling the backlog doubled the poll: 204, 330 and 565 blocks per row returned at
--              2,400, 4,800 and 9,600 rows per table.
--
--              Two partial indexes give the orphan predicate a key order. Everything claimable is
--              either unowned or holds an expired lease, and each half is a partial predicate a
--              planner can prove, so the acquisition walks the claimable rows in the order it wants
--              them and stops, instead of walking the pending rows and discarding. Both carry the
--              priority bucket as their leading key, so a band's candidates are drawn from that
--              band's own range and a large background backlog can never crowd out an interactive
--              row; both cover every column the lanes filter or return, so within the window the
--              walk stays off the heap. 150 carries the rewrite that reads them.
--
--              The bucket expression is the one 150 and 158 write. Keep the three identical.
--
-- Dependencies: 149 (priority columns), 150 (claim_orphaned_inbox), 158 (the held-lane indexes this mirrors for acquisition)
-- Objects: idx_inbox_unowned_bucket_arrival, idx_inbox_expired_bucket_arrival

CREATE INDEX IF NOT EXISTS idx_inbox_unowned_bucket_arrival
  ON __SCHEMA__.wh_inbox (
    (CASE WHEN priority <= 99 THEN 0 WHEN priority <= 199 THEN 1 ELSE 2 END),
    received_at,
    message_id)
  INCLUDE (stream_id, is_event, priority, scheduled_for, partition_number)
  WHERE processed_at IS NULL AND instance_id IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_inbox_unowned_bucket_arrival IS
  'Pending inbox rows nobody holds, by priority bucket and arrival (159). claim_orphaned_inbox draws its '
  'candidates for a band from this band''s own range and stops at its window, instead of walking every pending '
  'row of every band to discard the ones a live peer holds.';

CREATE INDEX IF NOT EXISTS idx_inbox_expired_bucket_arrival
  ON __SCHEMA__.wh_inbox (
    (CASE WHEN priority <= 99 THEN 0 WHEN priority <= 199 THEN 1 ELSE 2 END),
    lease_expiry,
    received_at,
    message_id)
  INCLUDE (stream_id, is_event, priority, scheduled_for, partition_number)
  WHERE processed_at IS NULL AND instance_id IS NOT NULL;
COMMENT ON INDEX __SCHEMA__.idx_inbox_expired_bucket_arrival IS
  'Pending inbox rows somebody holds, by priority bucket and lease expiry (159). The other half of the orphan '
  'predicate: claim_orphaned_inbox finds the longest-expired leases of a band at the head of its range, and an '
  'instance whose peers'' leases are all live proves it at the first entry instead of by walking the holdings.';
