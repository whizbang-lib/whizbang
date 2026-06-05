-- Migration: 027_ClaimOrphanedPerspectiveEvents.sql
-- Date: 2025-12-25 (updated 2026-04-13 for full-stream capture, stream count limit)
-- Description: Creates claim_orphaned_perspective_events function for claiming orphaned perspective events.
--              Full-stream capture: once a stream is selected, ALL its unleased messages are leased.
--              p_max_streams limits how many distinct streams are selected (default 500).
--              Ensures sequential ordering within stream/perspective. Respects stream ownership.
-- Dependencies: 001-026 (requires wh_perspective_events, wh_active_streams tables from migration 009)

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_perspective_events(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_max_streams INTEGER DEFAULT 500,
  p_instance_rank INTEGER DEFAULT 0,
  p_active_instance_count INTEGER DEFAULT 1
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200)
) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  WITH claimable_events AS (
    -- Find all events eligible for claiming (orphaned or unleased)
    SELECT
      pe.event_work_id,
      pe.stream_id,
      pe.perspective_name,
      pe.event_id,
      pe.partition_number
    FROM wh_perspective_events pe
    WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND pe.processed_at IS NULL
      -- Phase H step 6 slice 2: stream ownership now combines wh_active_streams pinning
      -- (OWNER PATH — always wins) with partition-modulo load balancing for unowned streams
      -- (UNOWNED PATH — symmetric with claim_orphaned_outbox / _inbox).
      AND (
        -- OWNER PATH — wh_active_streams says this instance owns the stream. Always claim.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = pe.stream_id
            AND ast.assigned_instance_id = p_instance_id
        )
        OR
        -- UNOWNED / ABANDONED PATH — partition-modulo selection for streams with no live owner.
        (
          (pe.partition_number % p_active_instance_count) = p_instance_rank
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = pe.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND (
                -- Existing check: a row in wh_service_instances counts as alive.
                -- This is removed by cleanup_stale_instances at the stale threshold.
                EXISTS (
                  SELECT 1 FROM __SCHEMA__.wh_service_instances si
                  WHERE si.instance_id = ast.assigned_instance_id
                )
                -- Slice 2b of zero-idle-polling — additive defensive predicate:
                -- a pod with a live Whizbang LISTEN connection in pg_stat_activity
                -- counts as alive even if its wh_service_instances row has been
                -- cleaned up (transient state during pod restart, race between
                -- cleanup_stale_instances and the pod opening its LISTEN
                -- connection on next boot). Strictly additive — only adds
                -- protection against premature orphan-claim, never loosens.
                OR EXISTS (
                  SELECT 1 FROM pg_stat_activity sa
                  WHERE sa.application_name = 'whizbang-' || ast.assigned_instance_id::text
                )
              )
          )
        )
      )
      -- Ensure ordering - no earlier uncompleted events in same perspective
      AND NOT EXISTS (
        SELECT 1 FROM wh_perspective_events earlier
        WHERE earlier.stream_id = pe.stream_id
          AND earlier.perspective_name = pe.perspective_name
          AND earlier.event_id < pe.event_id
          AND (
            (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
            OR (earlier.scheduled_for > p_now)
          )
      )
  ),
  -- Select up to p_max_streams distinct streams from claimable events
  selected_streams AS (
    SELECT DISTINCT ce.stream_id
    FROM claimable_events ce
    LIMIT p_max_streams
  ),
  -- Claim ALL events for selected streams (full-stream capture)
  claimed AS (
    UPDATE wh_perspective_events pe
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = pe.attempts + 1
    FROM claimable_events ce
    INNER JOIN selected_streams ss ON ce.stream_id = ss.stream_id
    WHERE pe.event_work_id = ce.event_work_id
    RETURNING pe.event_work_id AS c_event_work_id, pe.stream_id AS c_stream_id, pe.perspective_name AS c_perspective_name, pe.partition_number AS c_partition_number
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into REFRESH (row-only
  -- UPDATE for already-owned-with-live-lease streams) + PIN (INSERT...ON CONFLICT for
  -- the rare ownership-transition case, with ORDER BY stream_id for consistent lock
  -- acquisition). Symmetric with the fix in claim_orphaned_outbox (mig 024); see that
  -- migration for the full rationale. Eliminates the 40P01 deadlock observed on dev
  -- slot 3 (Whizbang PR #227).
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now
    FROM (
      SELECT c.c_stream_id AS stream_id, c.c_partition_number AS partition_number
      FROM claimed c
      WHERE NOT EXISTS (
        SELECT 1 FROM refreshed r WHERE r.refreshed_stream_id = c.c_stream_id
      )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO UPDATE
      SET last_activity_at = EXCLUDED.last_activity_at,
          assigned_instance_id = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.assigned_instance_id
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.assigned_instance_id
            ELSE ast.assigned_instance_id
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_event_work_id AS event_work_id, c.c_stream_id AS stream_id, c.c_perspective_name AS perspective_name FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_perspective_events IS
'Claims orphaned perspective events with full-stream capture: selects up to p_max_streams distinct streams, then leases ALL unleased events for each selected stream. Owner-preferring (wh_active_streams.assigned_instance_id = caller) + partition-modulo load balancing on unowned streams (partition_number % active_count = instance_rank). Symmetric with claim_orphaned_outbox / _inbox. Returns claimed work IDs.';
