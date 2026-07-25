-- Migration: 042_FetchPendingPerspectiveEvents.sql
-- Date: 2026-05-02
-- Description: Cheap ID-only prefetch for the perspective drainer (Phase H step 7 slice 1).
--              Returns (event_work_id, event_id) tuples for unprocessed rows leased to the
--              caller, ordered by event_id ASC. The drainer uses this BEFORE pulling event
--              bodies so it can filter against RecentlyProcessedEventCache (cooldown) and
--              the cached cursor (already-applied / inversion-anchor) without paying the
--              body-fetch + JSON-deserialize cost when no actual apply work is needed.
--              This closes the cursor-flush race window where claim_work re-issues a stream
--              before PerspectiveCompletionFlushWorker has DELETEd the wh_perspective_events
--              row (the ~25 ms coalesce window).
-- Dependencies: 009 (wh_perspective_events table)

SELECT __SCHEMA__.drop_all_overloads('fetch_pending_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_pending_perspective_events(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_instance_id UUID
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID,
  -- INNER JOIN wh_event_store + commit_sequence IS NOT NULL gate: rows whose post-commit
  -- stamper hasn't caught up are invisible to the drainer until they're stamped. Closes
  -- the stamper-lag race at the SQL source — downstream filters can rely on every row
  -- coming through here having an authoritative commit_sequence.
  out_commit_sequence BIGINT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    pe.event_work_id,
    pe.event_id,
    es.commit_sequence
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = p_perspective_name
    AND pe.instance_id = p_instance_id
    AND pe.processed_at IS NULL
    AND es.commit_sequence IS NOT NULL
  ORDER BY pe.event_id ASC;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_pending_perspective_events IS
'Returns (event_work_id, event_id) tuples for unprocessed wh_perspective_events rows leased to the caller, scoped to a single (stream_id, perspective_name) and ordered by event_id ASC. Cheap ID-only prefetch used by the perspective drainer to filter against the in-memory cooldown cache and the cached cursor before deciding whether to pull event bodies. Replaces the in-memory cursor-inversion check from Phase H step 6 slice 4 with a SQL-side filter; closes the cursor-flush race window between drain finish and PerspectiveCompletionFlushWorker DELETE landing.';

-- ===============================================================================
-- Slice 25: per-stream fetch atomicity. claim_and_fetch_pending_perspective_events
-- ===============================================================================
-- The plain fetch above only returns rows already leased to the caller. Under
-- contention, a stream can have unprocessed rows with instance_id = NULL or with
-- an expired lease — those rows are INVISIBLE to fetch_pending_perspective_events.
-- The perspective worker would advance its cursor past lower-event_id rows that
-- existed in the table but weren't claimed yet, producing the post-slice-23/24c
-- residual cursor inversions (38-second deltas with NO source-side delay).
--
-- This atomic variant performs claim + fetch in a single transaction:
--   1. Claims (or re-leases) every eligible row for (stream_id, perspective_name)
--      to the caller's instance — including orphans, expired-lease rows, and
--      already-claimed-by-caller rows whose lease we want to extend.
--   2. Returns the post-claim set in event_id ASC order.
--
-- Caller invariant: after this returns, NO further unprocessed rows can exist
-- for this (stream, perspective) pair unleased to anyone else. Either we own
-- them (returned in the result set) or they are still scheduled for the future
-- (scheduled_for > p_now).
--
-- Ownership: this function intentionally SKIPS the wh_active_streams ownership
-- check that claim_orphaned_perspective_events enforces. The caller is the
-- perspective worker that's already processing this stream — claiming was
-- gated upstream by ClaimWorker / drain channel routing. Adding the check here
-- would deadlock the normal drain path against the orphan-reclaim path.
--
-- Attempts accounting: matches claim_orphaned_perspective_events (slice 8 of
-- Phase H step 8). attempts bumps only when instance_id changes (NULL → us,
-- or other instance → us). Re-leasing our own rows does NOT bump attempts —
-- that would inflate the counter every drain cycle.

SELECT __SCHEMA__.drop_all_overloads('claim_and_fetch_pending_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_and_fetch_pending_perspective_events(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID,
  -- INNER JOIN wh_event_store + commit_sequence IS NOT NULL gate on both the claim CTE
  -- and the return-fetch: rows whose post-commit stamper hasn't caught up are neither
  -- leased nor returned. Closes the stamper-lag race at the SQL source — downstream
  -- filters can rely on every row coming through here having an authoritative
  -- commit_sequence. Unstamped rows stay unowned until a later cycle (post-stamping).
  out_commit_sequence BIGINT
) AS $$
BEGIN
  -- Step 1: atomic claim. Lease every eligible row (orphaned, expired, or already
  -- ours) to this instance. Same instance gets lease-extension; new claims bump
  -- attempts to match the claim_orphaned_perspective_events convention.
  --
  -- Deadlock prevention (slice 25 hotfix): inner CTE acquires row locks in
  -- deterministic event_work_id order via FOR UPDATE SKIP LOCKED. Concurrent
  -- callers can't lock rows in conflicting orders — same-row contention turns
  -- into "the loser skips this row this cycle" instead of a 40P01 deadlock.
  -- Slice 28: only UPDATE rows that actually NEED claiming — unowned or with expired
  -- lease. Rows already leased to us are returned by step 2's SELECT without re-extending
  -- the lease here; LeaseRenewalWorker handles in-flight renewals separately. Skipping the
  -- redundant own-row UPDATEs eliminates ~7x WAL write volume on wh_perspective_events
  -- (pg_stat_user_tables in a production run showed a large multiple of UPDATEs versus inserts).
  WITH eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
    WHERE pe.stream_id = p_stream_id
      AND pe.perspective_name = p_perspective_name
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      )
      AND es.commit_sequence IS NOT NULL
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry,
      attempts = pe.attempts + 1
  FROM eligible e
  WHERE pe.event_work_id = e.event_work_id;

  -- Step 2: return all rows now claimed by us, in event_id ASC order.
  -- Within the same transaction so we see our own UPDATE writes.
  -- Same stamper-lag gate as the claim CTE — defense in depth in case a row was
  -- already leased to us before its commit_sequence got stamped.
  RETURN QUERY
  SELECT
    pe.event_work_id,
    pe.event_id,
    es.commit_sequence
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = p_perspective_name
    AND pe.instance_id = p_instance_id
    AND pe.processed_at IS NULL
    AND es.commit_sequence IS NOT NULL
  ORDER BY pe.event_id ASC;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_and_fetch_pending_perspective_events IS
'Slice 25: per-stream fetch atomicity. Atomically claims (or re-leases) every eligible wh_perspective_events row for (stream_id, perspective_name) to p_instance_id, then returns the post-claim set in event_id ASC order. Eliminates the cursor-advances-past-orphaned-rows race that produced residual cursor inversions after slices 23 + 24c shipped. Caller invariant: after this returns, no unprocessed rows for the pair exist unleased to anyone else.';
