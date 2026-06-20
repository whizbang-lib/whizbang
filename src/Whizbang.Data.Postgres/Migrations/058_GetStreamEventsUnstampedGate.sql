-- Migration: 058_GetStreamEventsUnstampedGate.sql
-- Date: 2026-06-20
-- Description: Part B — close the stamper-lag inversion at the LIVE drain WITHOUT making the drain
--              depend on the stamper for liveness.
--              get_stream_events (mig 038) returned unstamped rows (commit_sequence IS NULL)
--              NULLS-LAST, so the perspective drain could apply an event via the event_id fallback
--              order before its true commit order was established — a cursor-inversion source that
--              feeds the rewind storm.
--
--              mig 058 gates `es.commit_sequence IS NOT NULL` on BOTH the claim CTE and the
--              return-fetch (so a FRESH unstamped row is neither claimed — no attempts inflation /
--              false dead-lettering — nor returned), BUT with a GRACE WINDOW: a row pending longer
--              than v_stamp_grace is included even while unstamped. This keeps the gate a
--              resilience-preserving optimization, not a hard liveness dependency on the
--              commit-sequence stamper:
--                * healthy stamper (stamps within ms) → grace never triggers → race closed;
--                * lagging/absent stamper (e.g. the ECommerce in-memory sample's per-schema fixture
--                  where stamp_pending_commit_sequences is not present) → after the grace the rows
--                  flow again exactly as pre-058 (NULLS LAST) instead of STALLING the perspective
--                  worker. Never worse than mig 038; never a stall.
--
--              Partial fix (does not cover the window where an event is stamped-low but its
--              perspective_events row does not yet exist); the in-order delivery buffer
--              (Part B full) covers the residual. See plans/perspective-inorder-delivery-buffer.md.
-- Dependencies: 038 (get_stream_events), 046/047 (commit_sequence column + stamper)

SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.get_stream_events(
  p_instance_id UUID,
  p_stream_ids UUID[],
  p_now TIMESTAMPTZ DEFAULT NOW(),
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT,
  out_event_work_id UUID,
  out_perspective_name VARCHAR(200),
  out_commit_sequence BIGINT,
  out_attempts INTEGER
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  -- Grace window: how long an unstamped perspective_events row is held back waiting for the
  -- stamper before we give up and drain it anyway. A healthy stamper completes in milliseconds,
  -- so 5s never triggers in normal operation but bounds worst-case stall to the grace on a
  -- lagging/absent stamper.
  v_stamp_grace CONSTANT INTERVAL := INTERVAL '5 seconds';
  v_stamp_cutoff TIMESTAMPTZ;
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stamp_cutoff := p_now - v_stamp_grace;

  -- Slice 25: atomic claim+fetch — claim/re-lease every eligible row before reading so the
  -- cursor never advances past an orphaned/expired-lease row (see mig 038 for the full history).
  --
  -- Mig 058: gate unstamped rows. A row is eligible only if its event_store row is stamped OR it
  -- has been pending past the grace window. Gating the claim (not just the return) is load-bearing:
  -- claiming bumps attempts, so claiming-but-not-returning a fresh unstamped row would inflate its
  -- attempt count every cycle and could dead-letter a row that was never actually applied. Aged
  -- unstamped rows ARE claimed+returned (degraded fallback) so a stuck stamper can't stall the worker.
  WITH eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM wh_perspective_events pe
    INNER JOIN wh_event_store es
      ON es.stream_id = pe.stream_id
      AND es.event_id = pe.event_id
    WHERE pe.stream_id = ANY(p_stream_ids)
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      )
      AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = v_lease_expiry,
      attempts = pe.attempts + 1
  FROM eligible e
  WHERE pe.event_work_id = e.event_work_id;

  -- Read all rows leased to us. Same gate as the claim CTE. Order by commit_sequence ASC with
  -- NULLS LAST so that, in the degraded (aged-unstamped) case, stamped rows still lead and the
  -- unstamped tail falls back to event_id order — exactly the pre-058 ordering.
  RETURN QUERY
  SELECT
    pe.stream_id,
    es.event_id,
    es.event_type::TEXT,
    es.event_data::TEXT,
    es.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name,
    es.commit_sequence,
    pe.attempts
  FROM wh_perspective_events pe
  INNER JOIN wh_event_store es
    ON pe.stream_id = es.stream_id
    AND pe.event_id = es.event_id
  WHERE pe.instance_id = p_instance_id
    AND pe.lease_expiry > p_now
    AND pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
    AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
  ORDER BY pe.stream_id, es.commit_sequence ASC NULLS LAST, es.event_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events IS
'Mig 058 — atomic per-stream claim+fetch with a grace-windowed unstamped-row gate. Claims (or re-leases) and returns every eligible wh_perspective_events row whose wh_event_store.commit_sequence is stamped OR which has been pending past the 5s grace window, joined with wh_event_store, ordered by commit_sequence ASC NULLS LAST. Fresh unstamped rows are neither claimed nor returned (closing the stamper-lag inversion at the SQL source); they surface once stamped, or after the grace if the stamper is lagging/absent (degrades to pre-058 behavior instead of stalling). Supersedes mig 038.';
