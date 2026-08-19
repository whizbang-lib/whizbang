-- Migration: 117_StampRingsPerspectiveDoorbells.sql
-- Date: 2026-08-19
-- Description: Stamping is the perspective-visibility event — ring the owners' doorbells.
--
--              Perspective fetch paths (042/058/059) hide event rows until commit_sequence
--              is stamped. The commit itself rings the owning instances' doorbells
--              (notify_instance_owners via store_outbox_messages / commit_handler_*), but
--              when the per-database ordering fence (116) holds a batch past that wake —
--              an older same-database transaction in flight across the commit — the
--              doorbell-triggered claim fetches zero visible rows and is spent. The
--              fenced-retry loop (CommitOrderStamperOptions.FencedRetryInterval) then
--              stamps the rows promptly, but nothing re-rang the claim path: perspective
--              visibility sat quantized to the relaxed notify-healthy poll cadence,
--              measured end-to-end as a rock-steady multi-second stall.
--
--              Fix: the stamp UPDATE collects the affected stream ids and, whenever it
--              stamped at least one row, PERFORMs
--              notify_instance_owners('perspective', <stream ids>) — the same owner-routed
--              doorbell the stores ring, so pinned streams wake their owning instance and
--              unpinned streams wake a deterministic live target. Empty ticks (the
--              steady-state idle poll) stay silent.
--
--              Horizon math, ordering, batching, and SKIP LOCKED semantics are UNCHANGED
--              from 116.
-- Dependencies: 045 (notify_instance_owners), 116 (per-database fence this supersedes).

SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

CREATE OR REPLACE FUNCTION __SCHEMA__.stamp_pending_commit_sequences(
  p_batch_size INTEGER DEFAULT 1000
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

  -- FOR UPDATE SKIP LOCKED + ORDER BY xmin: unchanged from 047/116 — concurrent callers
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

  -- Stamping made these streams' events fetchable — wake their owners now. The commit-time
  -- doorbell cannot cover the fenced case (it fired before the rows were visible).
  IF v_stamped_count > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('perspective', v_stream_ids);
  END IF;

  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences IS
'117 supersedes 116 — identical per-database fence, ordering, and batching, plus: whenever the call stamps at least one row it PERFORMs notify_instance_owners(''perspective'', <stamped stream ids>). Stamping is the perspective-visibility event; the commit-time doorbell is consumed before a FENCED batch becomes visible, so without this wake the apply waits out the relaxed poll cadence. Empty ticks stay silent. Returns count of rows stamped.';
