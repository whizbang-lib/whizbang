-- 122_InboxGracefulRelease.sql
--
-- Lets a worker hand back inbox rows it claimed but never dispatched, WITHOUT spending their retry
-- budget.
--
-- claim_orphaned_inbox charges an attempt on every claim, and that must stay: it is the only
-- fail-safe that survives a process vanishing mid-dispatch, because a dead process reports nothing.
-- The cost is that a worker claiming more rows than it can dispatch inside the lease window pays an
-- attempt for every untouched row, every cycle. A backlog larger than one worker's throughput
-- therefore burns its own retry budget and dead-letters healthy messages as MaxAttemptsExceeded
-- having never been handed to a receptor — with no failure recorded anywhere, because none occurred.
--
-- The resolution is a REFUND rather than a smaller charge. The claim stays optimistic; a worker that
-- ends a cycle still holding rows it never touched says so explicitly and gets those attempts back.
-- Only an UNGRACEFUL exit — where nothing is released because the process is gone — leaves the
-- charge standing, which is exactly the case the counter exists to bound.
--
-- The database cannot distinguish "never dispatched" from "dispatched and died"; only the worker
-- can. That is why this is a separate call rather than logic inside claim_orphaned_inbox.

CREATE OR REPLACE FUNCTION __SCHEMA__.release_unprocessed_inbox(
  p_instance_id UUID,
  p_message_ids UUID[]
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_released INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_inbox i
  SET
      -- GREATEST(...,0) keeps the budget from going negative under a duplicated release (retry,
      -- at-least-once flush, a shutdown path that runs twice). A negative budget would make the row
      -- effectively un-dead-letterable, trading this bug for an unbounded-retry one.
      attempts = GREATEST(i.attempts - 1, 0),
      -- Clear the lease so the row is immediately claimable again rather than invisible until it
      -- would have expired. Handing work back is the whole point; making the caller wait out a lease
      -- it explicitly relinquished would just reintroduce the stall.
      instance_id = NULL,
      lease_expiry = NULL
  WHERE i.message_id = ANY(p_message_ids)
    -- Scoped to the caller's OWN claim. A release from an instance that does not hold the row is a
    -- no-op: without this, one worker could unlock a row another instance is actively dispatching
    -- and two workers would handle the same message concurrently. This predicate is also what makes
    -- the call idempotent — after the first release instance_id is NULL, so a repeat matches nothing
    -- and cannot refund twice.
    AND i.instance_id = p_instance_id
    -- Never disturb rows that already completed.
    AND i.processed_at IS NULL;

  GET DIAGNOSTICS v_released = ROW_COUNT;
  RETURN v_released;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.release_unprocessed_inbox(UUID, UUID[]) IS
  'Hands back inbox rows claimed but never dispatched, refunding the optimistic claim attempt and '
  'clearing the lease. Scoped to the caller''s own claim and idempotent. An ungraceful exit releases '
  'nothing, so its attempt charge correctly stands.';
