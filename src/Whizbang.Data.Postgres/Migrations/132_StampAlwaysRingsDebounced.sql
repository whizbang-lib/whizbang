-- Migration: 132_StampAlwaysRingsDebounced
-- Date: 2026-09-05
-- Description: Every stamp that affects rows rings the owners; the debounce owns redundancy
--   (issue #677, part 2). 118 made the post-stamp ring opt-in via p_notify_owners because the
--   pre-debounce always-ring (117) herded every owner's wake loops during bulk stamping
--   (#665). But the caller cannot actually compute the opt-in: the stamper worker passes TRUE
--   only from its fenced-retry drain, and a stamper whose FIRST look at a row lands after the
--   fence already cleared stamps on the steady-state path, never observes the fence, and
--   skips the ring — while the commit-time doorbell was already consumed by a pre-visibility
--   claim. The row then sits stamped-but-unannounced until the claim loop's adaptive poll cap
--   (forensic: commit at t=0, doorbell consumed by an empty claim at ~20 ms, fence cleared at
--   ~300 ms, first stamp at ~520 ms with p_notify_owners = FALSE, visibility at ~10.5 s).
--
--   The right owner of the redundancy judgment is the DEBOUNCE (130), now that it arms only
--   on found work (131): a target actively draining has a fresh found-work watermark and the
--   ring toward it is suppressed (the #665 storm case); an idle or woken-but-empty target has
--   none and the ring fires (the stranded case). So the ring becomes unconditional on
--   "stamped > 0" and p_notify_owners is retained for signature compatibility but ignored —
--   in-flight processes built against 118 keep calling with the flag and now get the
--   debounced-always-ring behavior.
--
--   Horizon math, ordering, batching, and SKIP LOCKED semantics are UNCHANGED from 116-118.
-- Dependencies: 118 (signature), 130 (debounce), 131 (debounce arms on found work only)
-- Objects: stamp_pending_commit_sequences

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

  -- 132: unconditional debounced ring — stamping IS the visibility event, and only the
  -- debounce (found-work watermark, 130/131) knows whether the target is already draining.
  -- p_notify_owners is deliberately ignored (see migration header).
  IF v_stamped_count > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('perspective', v_stream_ids);
  END IF;

  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences IS
'132 supersedes 118 — identical fence, ordering, and batching; the post-stamp notify_instance_owners(''perspective'', <stamped stream ids>) ring now fires on EVERY stamp that affected rows, with redundancy absorbed by the found-work doorbell debounce (130/131). p_notify_owners is retained for signature compatibility and ignored: the caller''s fenced-drain heuristic under-detected (a stamper first seeing a row after the fence cleared skipped the ring and stranded visibility on the adaptive poll cap, issue #677). Returns count of rows stamped.';
