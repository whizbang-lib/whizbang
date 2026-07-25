-- Migration: 045_NotifyInstanceOwners.sql
-- Date: 2026-05-23 (v0.685 cold-start fix; 2026-06-12 perf rewrite)
-- Description: Slice 27 — helper for instance-routed NOTIFY emission. Replaces the
--              global pg_notify('wh_work', category) pattern that wakes every listening
--              instance. Resolves stream → owner from wh_active_streams and emits one
--              pg_notify('wh_work_i_<assigned_instance_id>', p_payload) per UNIQUE owner
--              across the input stream set. A saga that writes 5 events across 5 streams
--              owned by 2 instances produces exactly 2 NOTIFYs, not N × instance_count.
--              Streams missing from wh_active_streams or with NULL assigned_instance_id
--              contribute zero NOTIFYs — polling backstop is the correctness floor for
--              those.
--
--              2026-06-12 perf rewrite (v0.686 prep): the v0.685 Step 2 paid a constant
--              24 ms per call even when the NOT EXISTS predicate excluded every row,
--              because the UNION ALL across wh_outbox/wh_inbox/wh_perspective_events
--              forced the planner to walk all three tables before discarding the false
--              p_payload = 'X' branches. Two changes:
--                1. v_unclaimed_streams early-return: when every input stream is already
--                   pinned in wh_active_streams (the bulk-import hot path after the
--                   first event per stream), Step 1 covered them and Step 2 has nothing
--                   to do. Skip the table-scan entirely.
--                2. IF/ELSIF dispatch on p_payload: when Step 2 does run, only the
--                   relevant source table is touched, not all three via UNION ALL.
--              A production measurement attributed ~363 s of cumulative DB
--              time to this function across a bulk import of tens of thousands of events. After this rewrite
--              per-call cost on the hot path drops to ~1 ms (Step 1 only) and ~2 ms in
--              the cold-start branch (single-table scan instead of UNION ALL).
-- Dependencies: 007 (wh_active_streams table), slice 6 fix (mig 024/025/027 re-pinning
--               on claim) — without re-pinning, wh_active_streams.assigned_instance_id
--               stays NULL after every instance restart and this helper emits no
--               NOTIFYs in production, making the optimization inert.

SELECT __SCHEMA__.drop_all_overloads('notify_instance_owners');

CREATE OR REPLACE FUNCTION __SCHEMA__.notify_instance_owners(
  p_payload TEXT,
  p_stream_ids UUID[]
) RETURNS VOID AS $$
DECLARE
  v_unclaimed_streams UUID[];
  v_active_count INTEGER;
BEGIN
  -- Step 1 (legacy path, unchanged): per-owner NOTIFY for streams that ARE in
  -- wh_active_streams. Each live owner gets at most one notify per call.
  PERFORM pg_notify('wh_work_i_' || a.assigned_instance_id::text, p_payload)
  FROM (
    SELECT DISTINCT assigned_instance_id
    FROM __SCHEMA__.wh_active_streams
    WHERE stream_id = ANY(p_stream_ids)
      AND assigned_instance_id IS NOT NULL
  ) a;

  -- Step 2 (v0.685, NEW): deterministic-target NOTIFY for streams NOT yet in
  -- wh_active_streams. Hot-path early return — when every input stream is
  -- already pinned to a live owner, Step 1 covered them all and we skip the
  -- per-table scan entirely. This is the 99 % case during bulk activity:
  -- after the first event on a stream the row pins to its owner, and all
  -- subsequent calls hit Step 1 only.
  SELECT ARRAY_AGG(s) INTO v_unclaimed_streams
  FROM unnest(p_stream_ids) AS s
  WHERE NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = s
      AND a.assigned_instance_id IS NOT NULL
  );

  IF v_unclaimed_streams IS NULL OR cardinality(v_unclaimed_streams) = 0 THEN
    RETURN;
  END IF;

  SELECT COUNT(*)::INTEGER INTO v_active_count
  FROM __SCHEMA__.wh_service_instances
  WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds';

  IF v_active_count = 0 THEN
    RETURN;
  END IF;

  -- IF/ELSIF dispatch on p_payload so we scan only the relevant source table.
  -- The previous shape used UNION ALL across all three tables with a
  -- p_payload = 'X' predicate inside each branch — the planner did not
  -- constant-fold those predicates away under the outer v_active_count
  -- IF wrapper, so every call walked all three tables. IF/ELSIF gives each
  -- branch a tight single-table plan.
  --
  -- Each branch shape: src (unclaimed stream partitions, from the relevant
  -- table) JOIN live (live instances by rank) ON rank-modulo. Same formula
  -- used by claim_orphaned_* — the instance notified is the same one that
  -- would claim the row on its next tick. No race, no broadcast.
  IF p_payload = 'outbox' THEN
    PERFORM pg_notify('wh_work_i_' || targets.target_instance_id::text, p_payload)
    FROM (
      WITH src AS (
        SELECT partition_number
        FROM __SCHEMA__.wh_outbox
        WHERE stream_id = ANY(v_unclaimed_streams)
      ),
      live AS (
        SELECT instance_id,
               (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM __SCHEMA__.wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT DISTINCT live.instance_id AS target_instance_id
      FROM src
      JOIN live ON live.rank = (src.partition_number % v_active_count)
      WHERE src.partition_number IS NOT NULL
    ) AS targets;
  ELSIF p_payload = 'inbox' THEN
    PERFORM pg_notify('wh_work_i_' || targets.target_instance_id::text, p_payload)
    FROM (
      WITH src AS (
        SELECT partition_number
        FROM __SCHEMA__.wh_inbox
        WHERE stream_id = ANY(v_unclaimed_streams)
      ),
      live AS (
        SELECT instance_id,
               (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM __SCHEMA__.wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT DISTINCT live.instance_id AS target_instance_id
      FROM src
      JOIN live ON live.rank = (src.partition_number % v_active_count)
      WHERE src.partition_number IS NOT NULL
    ) AS targets;
  ELSIF p_payload = 'perspective' THEN
    PERFORM pg_notify('wh_work_i_' || targets.target_instance_id::text, p_payload)
    FROM (
      WITH src AS (
        SELECT partition_number
        FROM __SCHEMA__.wh_perspective_events
        WHERE stream_id = ANY(v_unclaimed_streams)
      ),
      live AS (
        SELECT instance_id,
               (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM __SCHEMA__.wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT DISTINCT live.instance_id AS target_instance_id
      FROM src
      JOIN live ON live.rank = (src.partition_number % v_active_count)
      WHERE src.partition_number IS NOT NULL
    ) AS targets;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_instance_owners IS
'Slice 27 + v0.685 — instance-routed NOTIFY emission. Step 1: emits one pg_notify(''wh_work_i_<owner_id>'', p_payload) per unique owner in wh_active_streams. Step 2: for unclaimed streams (cold-start), looks up partition_number from the source table per p_payload and emits one NOTIFY to the rank-deterministic owner via the same partition-modulo formula claim_orphaned_* uses. 2026-06-12 perf rewrite: early-return when all streams already pinned (skips Step 2 table scan), IF/ELSIF dispatch so only the relevant source table is scanned. Preserves the algorithmic-assignment design: the instance notified is the same one that would claim the row on its next tick.';
