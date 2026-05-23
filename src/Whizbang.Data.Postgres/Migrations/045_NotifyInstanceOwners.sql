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
BEGIN
  PERFORM pg_notify('wh_work_i_' || a.assigned_instance_id::text, p_payload)
  FROM (
    SELECT DISTINCT assigned_instance_id
    FROM __SCHEMA__.wh_active_streams
    WHERE stream_id = ANY(p_stream_ids)
      AND assigned_instance_id IS NOT NULL
  ) a;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_instance_owners IS
'Slice 27: instance-routed NOTIFY emission. Resolves the input stream set to unique owner instances via wh_active_streams, then emits one pg_notify(''wh_work_i_<owner_id>'', p_payload) per owner. Listeners subscribe to a single channel per instance (their own), so non-owners never wake. Streams missing from wh_active_streams or with NULL assigned_instance_id contribute zero NOTIFYs — polling backstop catches those. Replaces the global pg_notify(''wh_work'', category) pattern.';
