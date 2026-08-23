-- Migration: 123_CountOutstandingWork.sql
-- Date: 2026-08-23
-- Description: count_outstanding_work — how much leased, unfinished work an instance is holding.
--
--              The claim-outstanding budget bounds work claimed but not yet processed. It cannot
--              size itself from the claim response: claim_work truncates its eligible_* CTEs to
--              LIMIT p_max_streams, and those CTEs match rows already leased to the caller
--              (instance_id = p_instance_id). So the returned count can never exceed the limit the
--              budget itself just produced — the control loop would be reading its own output
--              instead of the system state, seeing abundant headroom no matter how much it held.
--
--              This function answers the question directly, untruncated, in one indexed round trip.
--              It is read-only: no leases, no attempt bumps, nothing to strand. Deliberately NOT a
--              counter maintained in the worker — an in-memory figure stranded by a hung or
--              cancelled task stays wrong until the process restarts, a failure this claim path has
--              already produced in production once.
-- Dependencies: 001-122 (wh_inbox, wh_outbox, wh_perspective_events)

-- ============================================================================
-- Supporting indexes. The count runs once per claim poll, so it has to be
-- index-served: filtering on instance_id among unprocessed rows via the existing
-- idx_inbox_instance_id would also walk that instance's processed history, which
-- on a table retaining history is most of it. Partial keeps the scan proportional
-- to work actually outstanding rather than to table size.
--
-- OPERATOR NOTE — one-time startup cost on first deploy of this version. The
-- migration runner wraps each file in a transaction, and CREATE INDEX CONCURRENTLY
-- cannot run inside one, so these builds take an ACCESS EXCLUSIVE lock for their
-- duration and block reads and writes on the table. The cost is proportional to
-- table size and is paid ONCE (IF NOT EXISTS), on the deploy that first applies
-- this file. On a large wh_inbox that can be minutes, so schedule that deploy
-- accordingly. Same pattern and same trade-off as the index work in 031 and 115.
-- ============================================================================

CREATE INDEX IF NOT EXISTS idx_inbox_outstanding_by_instance
ON __SCHEMA__.wh_inbox (instance_id, lease_expiry)
WHERE processed_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_outbox_outstanding_by_instance
ON __SCHEMA__.wh_outbox (instance_id, lease_expiry)
WHERE processed_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_perspective_outstanding_by_instance
ON __SCHEMA__.wh_perspective_events (instance_id, lease_expiry)
WHERE processed_at IS NULL;

-- ============================================================================
-- count_outstanding_work
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('count_outstanding_work');

CREATE OR REPLACE FUNCTION __SCHEMA__.count_outstanding_work(
  p_instance_id UUID
) RETURNS TABLE(
  inbox_rows BIGINT,
  outbox_rows BIGINT,
  perspective_rows BIGINT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    -- A live lease is the definition of "held": an expired lease is no longer this instance's
    -- work, whoever still has the row stamped. Counting expired leases would hold the budget
    -- closed against work the store has already made available to everyone else.
    (SELECT count(*) FROM __SCHEMA__.wh_inbox i
      WHERE i.instance_id = p_instance_id
        AND i.processed_at IS NULL
        AND i.lease_expiry > NOW()),
    (SELECT count(*) FROM __SCHEMA__.wh_outbox o
      WHERE o.instance_id = p_instance_id
        AND o.processed_at IS NULL
        AND o.lease_expiry > NOW()),
    -- All three kinds count. Each is leased and each charges an attempt, so bounding one column
    -- alone would leave the identical arithmetic free to recur in another — the failure would
    -- move rather than stop.
    (SELECT count(*) FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.instance_id = p_instance_id
        AND pe.processed_at IS NULL
        AND pe.lease_expiry > NOW());
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.count_outstanding_work IS
'Counts leased, unprocessed work held by one instance across inbox, outbox and perspective events. Sizes the claim-outstanding budget, which cannot use the claim response for this: claim_work truncates its eligible_* CTEs to LIMIT p_max_streams, so a count taken from them can never exceed the limit the budget produced. Read-only and untruncated; a live lease (lease_expiry > NOW()) is what counts as held, since an expired lease is already available to other instances.';
