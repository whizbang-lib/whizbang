-- Migration: 045_NotifyInstanceOwners.sql
-- Date: 2026-05-23
-- Description: Slice 27 — helper for instance-routed NOTIFY emission. Replaces the
--              global pg_notify('wh_work', category) pattern that wakes every listening
--              instance. Resolves stream → owner from wh_active_streams and emits one
--              pg_notify('wh_work_i_<assigned_instance_id>', p_payload) per UNIQUE owner
--              across the input stream set. A saga that writes 5 events across 5 streams
--              owned by 2 instances produces exactly 2 NOTIFYs, not N × instance_count.
--              Streams missing from wh_active_streams or with NULL assigned_instance_id
--              contribute zero NOTIFYs — polling backstop is the correctness floor for
--              those.
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
  -- wh_active_streams (the cold-start case for a brand-new stream). Each
  -- unclaimed stream's partition_number is looked up from the source table
  -- selected by p_payload, then matched against the rank-deterministic owner
  -- using the SAME partition-modulo formula that claim_orphaned_* uses. This
  -- preserves the algorithmic assignment design: the instance that gets the
  -- notify is the same one that would claim the row on its next ClaimWorker
  -- tick. No race, no broadcast. Streams with no partition_number lookup
  -- (e.g. payloads outside the known set) contribute zero NOTIFYs — polling
  -- backstop catches those.
  SELECT COUNT(*)::INTEGER INTO v_active_count
  FROM __SCHEMA__.wh_service_instances
  WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds';

  IF v_active_count > 0 THEN
    PERFORM pg_notify('wh_work_i_' || targets.target_instance_id::text, p_payload)
    FROM (
      WITH src AS (
        -- Unclaimed streams' partition_numbers, looked up from the source table
        -- selected by p_payload. Each branch is no-op when the payload doesn't
        -- match — keeps the query plan stable. Excludes rows whose stream is
        -- already in wh_active_streams (we notified that owner in Step 1).
        SELECT partition_number FROM __SCHEMA__.wh_outbox
          WHERE p_payload = 'outbox'
            AND stream_id = ANY(p_stream_ids)
            AND NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_active_streams a
              WHERE a.stream_id = wh_outbox.stream_id
                AND a.assigned_instance_id IS NOT NULL
            )
        UNION ALL
        SELECT partition_number FROM __SCHEMA__.wh_inbox
          WHERE p_payload = 'inbox'
            AND stream_id = ANY(p_stream_ids)
            AND NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_active_streams a
              WHERE a.stream_id = wh_inbox.stream_id
                AND a.assigned_instance_id IS NOT NULL
            )
        UNION ALL
        SELECT partition_number FROM __SCHEMA__.wh_perspective_events
          WHERE p_payload = 'perspective'
            AND stream_id = ANY(p_stream_ids)
            AND NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_active_streams a
              WHERE a.stream_id = wh_perspective_events.stream_id
                AND a.assigned_instance_id IS NOT NULL
            )
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
'Slice 27 + v0.685 — instance-routed NOTIFY emission. Step 1 (legacy): emits one pg_notify(''wh_work_i_<owner_id>'', p_payload) per unique owner found in wh_active_streams. Step 2 (v0.685): for streams NOT yet in wh_active_streams (cold-start case), looks up partition_number from the source table per p_payload and emits one NOTIFY to the rank-deterministic owner via the same partition-modulo formula claim_orphaned_* uses. Preserves the algorithmic-assignment design: the instance notified is the same one that would claim the row on its next tick. Polling backstop is still the correctness floor for any stream with no partition_number lookup (unknown payload).';
