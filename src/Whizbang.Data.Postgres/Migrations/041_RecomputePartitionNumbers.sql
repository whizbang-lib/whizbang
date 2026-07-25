-- Migration: 041_RecomputePartitionNumbers.sql
-- Date: 2026-04-21
-- Description: Recompute wh_inbox.partition_number, wh_outbox.partition_number, and
--              wh_active_streams.partition_number whenever the stored value disagrees
--              with compute_partition(stream_id, p_partition_count). Idempotent —
--              re-running with the same p_partition_count is a no-op after the first
--              successful pass.
--
--              Called from C# on worker startup so a service that comes up under a
--              new (or first-time-correct) PartitionCount immediately self-heals any
--              rows wedged by the cross-table partition mismatch wedge observed in
--              production. Also useful when a deploy intentionally changes
--              PartitionCount.
--
-- Dependencies: 001 (compute_partition), 007 (wh_active_streams), 020/021 (wh_outbox/wh_inbox)

SELECT __SCHEMA__.drop_all_overloads('recompute_partition_numbers');

CREATE OR REPLACE FUNCTION __SCHEMA__.recompute_partition_numbers(
  p_partition_count INTEGER
) RETURNS TABLE(
  table_name TEXT,
  rows_recomputed BIGINT
) AS $$
DECLARE
  v_inbox_count BIGINT;
  v_outbox_count BIGINT;
  v_active_streams_count BIGINT;
BEGIN
  IF p_partition_count IS NULL OR p_partition_count <= 0 THEN
    RAISE EXCEPTION 'recompute_partition_numbers: p_partition_count must be a positive integer (got %)', p_partition_count;
  END IF;

  -- wh_inbox: only recompute rows that have a stream binding AND whose stored
  -- partition_number disagrees with the canonical value. NULL partition_number
  -- (no stream binding) is the explicitly-tolerated fallback path in claim_orphaned_inbox
  -- and must not be touched.
  WITH updated AS (
    UPDATE __SCHEMA__.wh_inbox
    SET partition_number = __SCHEMA__.compute_partition(stream_id, p_partition_count)
    WHERE stream_id IS NOT NULL
      AND processed_at IS NULL
      AND partition_number IS DISTINCT FROM __SCHEMA__.compute_partition(stream_id, p_partition_count)
    RETURNING 1
  )
  SELECT COUNT(*) INTO v_inbox_count FROM updated;

  WITH updated AS (
    UPDATE __SCHEMA__.wh_outbox
    SET partition_number = __SCHEMA__.compute_partition(stream_id, p_partition_count)
    WHERE stream_id IS NOT NULL
      AND processed_at IS NULL
      AND partition_number IS DISTINCT FROM __SCHEMA__.compute_partition(stream_id, p_partition_count)
    RETURNING 1
  )
  SELECT COUNT(*) INTO v_outbox_count FROM updated;

  -- wh_active_streams: refresh stale partition_numbers. Lease/ownership are untouched —
  -- a recompute does not change who currently owns a stream, only the partition_number
  -- column used by claim_orphaned_inbox/_outbox modulo routing.
  WITH updated AS (
    UPDATE __SCHEMA__.wh_active_streams
    SET partition_number = __SCHEMA__.compute_partition(stream_id, p_partition_count)
    WHERE partition_number IS DISTINCT FROM __SCHEMA__.compute_partition(stream_id, p_partition_count)
    RETURNING 1
  )
  SELECT COUNT(*) INTO v_active_streams_count FROM updated;

  RETURN QUERY VALUES
    ('wh_inbox'::TEXT, v_inbox_count),
    ('wh_outbox'::TEXT, v_outbox_count),
    ('wh_active_streams'::TEXT, v_active_streams_count);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.recompute_partition_numbers IS
'Recomputes partition_number for wh_inbox, wh_outbox, and wh_active_streams against the supplied PartitionCount, fixing rows whose stored value disagrees with compute_partition(stream_id, p_partition_count). Idempotent. Called on worker startup to self-heal the partition-mismatch wedge observed in production. Returns one row per table with the count of recomputed rows.';
