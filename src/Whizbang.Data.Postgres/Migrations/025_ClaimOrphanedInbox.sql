-- Migration: 025_ClaimOrphanedInbox.sql
-- Date: 2026-04-21 (owner-preferring claim — fixes rank-churn wedge, production incident)
-- Description: Creates claim_orphaned_inbox function for claiming orphaned inbox messages.
--              Owner-preferring semantics: a stream's live owner always claims its
--              messages; partition-based load balancing applies only to unowned /
--              abandoned-owner streams.
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
    AND (
      -- OWNER PATH — if this instance already owns the stream (live, non-expired lease
      -- in wh_active_streams), it always claims. Partition modulo is IGNORED. This
      -- preserves per-stream FIFO and prevents the rank-churn wedge observed on dev
      -- production (2026-04-21): when active_instance_count changes, partition-modulo
      -- routing for a partition number can shift to a different rank than the stream's
      -- existing owner's rank. Without this branch, the modulo-matched instance is
      -- blocked by the ownership NOT EXISTS below AND the owner is blocked by the
      -- modulo filter — neither can claim.
      EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_active_streams ast
        WHERE ast.stream_id = i.stream_id
          AND ast.assigned_instance_id = p_instance_id
          AND ast.lease_expiry > p_now
      )
      OR
      -- UNOWNED / ABANDONED PATH — stream has no live owner. Partition-based load
      -- balancing decides which rank picks it up. NULL partition_number (message has
      -- no stream binding) remains claimable by any rank.
      (
        (i.partition_number IS NULL
         OR (i.partition_number % p_active_instance_count) = p_instance_rank)
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = i.stream_id
            AND ast.assigned_instance_id != p_instance_id
            AND ast.lease_expiry > p_now
            AND EXISTS (
              -- A live instance = one whose last heartbeat is within the abandon threshold
              -- (p_stale_cutoff). An instance that stopped heartbeating (SIGKILL, crash,
              -- container replaced) holds no meaningful ownership: its wh_active_streams
              -- lease reflects intent that will never be realised. Without the heartbeat-
              -- recency clause the dead lease remains "live" for up to lease_duration_seconds
              -- (300 s by default), blocking fresh cross-instance claims for minutes.
              -- See migration 029 (process_work_batch) for where v_stale_cutoff is computed
              -- and 011 (cleanup_stale_instances) for the eventual DELETE once the row is
              -- past the threshold; this claim-time check is what makes the system actually
              -- recover at the threshold boundary rather than at the lease-expiry boundary.
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
                AND si.last_heartbeat_at >= p_stale_cutoff
            )
        )
      )
    )
  RETURNING i.message_id AS message_id, i.stream_id AS stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_inbox IS
'Claims orphaned inbox messages with expired or null leases. Owner-preferring: a stream''s live owner always claims its messages (FIFO per-stream, immune to rank churn from scale events). Partition-modulo load balancing applies only to streams with no live owner. An abandoned (non-heartbeating) instance''s lease does NOT block cross-instance claims, giving SIGKILL-tolerant recovery bounded by the stale threshold rather than the 300 s active-streams lease. Returns claimed message IDs for Orphaned flag in orchestrator response.';
