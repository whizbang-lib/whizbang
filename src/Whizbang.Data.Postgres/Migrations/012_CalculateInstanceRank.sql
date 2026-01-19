-- Migration: 012_CalculateInstanceRank.sql
-- Date: 2025-12-25
-- Description: Creates calculate_instance_rank function for partition-based load balancing.
--              Returns instance rank and active count for use in claim_orphaned_* functions.
-- Dependencies: 001-011 (requires wh_service_instances table)

CREATE OR REPLACE FUNCTION __SCHEMA__.calculate_instance_rank(
  p_instance_id UUID,
  p_stale_cutoff TIMESTAMPTZ
) RETURNS TABLE(
  instance_rank INTEGER,
  active_instance_count INTEGER
) AS $$
BEGIN
  RETURN QUERY
  WITH instance_ranks AS (
    SELECT
      si.instance_id,
      (ROW_NUMBER() OVER (ORDER BY si.instance_id) - 1)::INTEGER as rank,
      COUNT(*) OVER ()::INTEGER as total_count
    FROM wh_service_instances si
    WHERE si.last_heartbeat_at >= p_stale_cutoff
  )
  SELECT
    COALESCE(ir.rank, 0),
    COALESCE(GREATEST(ir.total_count, 1), 1)
  FROM instance_ranks ir
  WHERE ir.instance_id = p_instance_id;

  -- Raise error if instance not found (indicates stale instance calling)
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Failed to calculate rank for instance %. Instance not found in active instances.', p_instance_id;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.calculate_instance_rank IS
'Calculates partition rank for an instance based on active instances. Used for partition-based load balancing in orphaned work claiming. Raises exception if instance not found.';
