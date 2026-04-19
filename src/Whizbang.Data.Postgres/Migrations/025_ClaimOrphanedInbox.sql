-- Migration: 025_ClaimOrphanedInbox.sql
-- Date: 2025-12-25
-- Description: Creates claim_orphaned_inbox function for claiming orphaned inbox messages.
--              Uses partition-based load balancing to distribute work across instances.
-- Dependencies: 001-024 (requires wh_inbox, wh_active_streams tables, compute_partition function)

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_inbox');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
BEGIN
  RETURN QUERY
  UPDATE wh_inbox i
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry
  WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
    AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
    AND i.processed_at IS NULL
    -- Partition-based load balancing: claim only messages for this instance's partitions
    AND (
      i.partition_number IS NULL
      OR (i.partition_number % p_active_instance_count) = p_instance_rank
    )
    -- STREAM OWNERSHIP CHECK: Only claim if stream is not owned by another *live* instance.
    -- A live instance = one whose last heartbeat is within the abandon threshold (p_stale_cutoff).
    -- An instance that stopped heartbeating (SIGKILL, crash, container replaced) holds no
    -- meaningful ownership: its wh_active_streams lease reflects intent that will never be
    -- realised. Without the heartbeat-recency clause the dead lease remains "live" for up to
    -- lease_duration_seconds (300 s by default), blocking fresh cross-instance claims for
    -- minutes. See migration 029 (process_work_batch) for where v_stale_cutoff is computed
    -- and 011 (cleanup_stale_instances) for the eventual DELETE once the row is past the
    -- threshold; this claim-time check is what makes the system actually recover at the
    -- threshold boundary rather than at the lease-expiry boundary.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = i.stream_id
        AND ast.assigned_instance_id != p_instance_id
        AND ast.lease_expiry > p_now
        AND EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_service_instances si
          WHERE si.instance_id = ast.assigned_instance_id
            AND si.last_heartbeat_at >= p_stale_cutoff
        )
    )
  RETURNING i.message_id AS message_id, i.stream_id AS stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_inbox IS
'Claims orphaned inbox messages with expired or null leases. Uses partition-based load balancing to distribute work. Respects stream ownership for stream-based messages — but only when the owning instance is still heartbeating (ast.assigned_instance_id present in wh_service_instances with last_heartbeat_at >= p_stale_cutoff). An abandoned (non-heartbeating) instance''s lease does NOT block cross-instance claims, giving SIGKILL-tolerant recovery bounded by the stale threshold rather than the 300 s active-streams lease. Returns claimed message IDs for Orphaned flag in orchestrator response.';
