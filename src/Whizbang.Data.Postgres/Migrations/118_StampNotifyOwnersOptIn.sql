-- Migration: 118_StampNotifyOwnersOptIn.sql
-- Date: 2026-08-19
-- Description: The post-stamp perspective doorbell (117) becomes OPT-IN per call.
--
--              117 rang notify_instance_owners('perspective', …) on EVERY stamp that
--              affected rows. That was correct for the fenced case it fixed — a batch whose
--              commit-time doorbell was consumed before the rows became visible — but it
--              also rang for every steady-state batch, where the commit doorbell is alive
--              and about to do the same job. Under bulk stamping (startup backlogs, seeded
--              databases, imports) the per-batch ring became a sustained wake storm: every
--              owner's claim and perspective loops re-woke every batch, and hosts with tight
--              connection pools starved their periodic workers — observed as pool-exhaustion
--              bursts and failed wire-route self-tests during warmup.
--
--              The caller (the stamper worker) is the only party that KNOWS whether the
--              doorbell was consumed: it just observed a fenced wake (stamped zero while
--              unstamped rows existed). So the ring is now a parameter. Steady-state stamps
--              call with the default (no ring — exactly the pre-117 doorbell rate); the
--              fenced-retry drain calls with p_notify_owners := TRUE and restores visibility
--              promptly. Horizon math, ordering, batching, and SKIP LOCKED semantics are
--              UNCHANGED from 116/117.
-- Dependencies: 045 (notify_instance_owners), 117 (the always-ring version this supersedes).

SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

CREATE OR REPLACE FUNCTION __SCHEMA__.stamp_pending_commit_sequences(
  p_batch_size INTEGER DEFAULT 1000,
  p_notify_owners BOOLEAN DEFAULT FALSE
) RETURNS INTEGER AS $$
DECLARE
  v_stamped_count INTEGER;
  v_stream_ids UUID[];
  v_horizon xid8;
BEGIN
  -- Per-database ordering fence (see migration 116 header). LEAST of the two same-database
  -- in-flight sources; either sub-select COALESCEs to the snapshot xmax ceiling when its
  -- source is empty.
  --
  -- Type note (unchanged from 047): xmin/backend_xid/transaction are 32-bit `xid`;
  -- comparisons run in 64-bit `xid8` via the text cast. Safe within a wraparound epoch;
  -- autovacuum freezing prevents wrap issues in practice.
  SELECT LEAST(
    COALESCE((SELECT min(sa.backend_xid::text::xid8)
              FROM pg_stat_activity sa
              WHERE sa.datname = current_database()
                AND sa.pid <> pg_backend_pid()
                AND sa.backend_xid IS NOT NULL),
             pg_snapshot_xmax(pg_current_snapshot())),
    COALESCE((SELECT min(px.transaction::text::xid8)
              FROM pg_prepared_xacts px
              WHERE px.database = current_database()),
             pg_snapshot_xmax(pg_current_snapshot()))
  ) INTO v_horizon;

  -- FOR UPDATE SKIP LOCKED + ORDER BY xmin: unchanged from 047/116/117 — concurrent callers
  -- partition the work, and lower xmin gets the lower commit_sequence within a batch,
  -- preserving the monotonic ordering downstream cursors rely on.
  WITH eligible AS (
    SELECT event_id, xmin::text::xid8 AS xmin8
    FROM __SCHEMA__.wh_event_store
    WHERE commit_sequence IS NULL
      AND xmin::text::xid8 < v_horizon
    ORDER BY xmin::text::xid8
    LIMIT p_batch_size
    FOR UPDATE SKIP LOCKED
  ),
  stamped AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET commit_sequence = nextval('__SCHEMA__.wh_commit_seq')
    FROM eligible e
    WHERE es.event_id = e.event_id
    RETURNING es.stream_id
  )
  SELECT COUNT(*)::INTEGER, array_agg(DISTINCT stream_id)
  INTO v_stamped_count, v_stream_ids
  FROM stamped;

  -- Opt-in make-up doorbell: only the caller knows whether these rows' commit-time doorbell
  -- was consumed before they became visible (the fenced case). Steady-state stamps skip the
  -- ring — the commit doorbell is already in flight and per-batch rings herd every owner's
  -- wake loops during bulk stamping.
  IF p_notify_owners AND v_stamped_count > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('perspective', v_stream_ids);
  END IF;

  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences IS
'118 supersedes 117 — identical fence, ordering, and batching; the post-stamp notify_instance_owners(''perspective'', <stamped stream ids>) ring is now opt-in via p_notify_owners (default FALSE). The stamper worker passes TRUE only on the fenced-retry drain, where the rows'' commit-time doorbell was provably consumed before visibility; steady-state stamps keep the pre-117 doorbell rate. Returns count of rows stamped.';
