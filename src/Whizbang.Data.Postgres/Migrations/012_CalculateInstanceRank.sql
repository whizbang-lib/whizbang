-- Migration: 012_CalculateInstanceRank.sql
-- Date: 2025-12-25
-- Description: Creates calculate_instance_rank function for partition-based load balancing.
--              Returns instance rank and active count for use in claim_orphaned_* functions.
-- Dependencies: 001-011 (requires wh_service_instances table)

SELECT __SCHEMA__.drop_all_overloads('calculate_instance_rank');

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
    FROM __SCHEMA__.wh_service_instances si
    WHERE si.last_heartbeat_at >= p_stale_cutoff
  )
  SELECT
    COALESCE(ir.rank, 0),
    COALESCE(GREATEST(ir.total_count, 1), 1)
  FROM instance_ranks ir
  WHERE ir.instance_id = p_instance_id;

  -- A caller absent from the active set is not an error condition, and raising on it was actively
  -- harmful. The caller is demonstrably alive -- it is executing this function -- and a RAISE
  -- aborts the entire enclosing statement, including whatever work would have repaired the
  -- registration. That converts a transient heartbeat lapse into a permanent outage: the instance
  -- can no longer claim, so it can no longer recover, so it stays absent.
  --
  -- Re-registration belongs to callers that carry the full instance identity (service name, host,
  -- process id) -- claim_work repairs its own row before ranking. This function receives only an
  -- id, so it degrades instead. Solo rank is the safe degradation: partition rank only widens or
  -- narrows which streams an instance *attempts*, and every claim is guarded by FOR UPDATE SKIP
  -- LOCKED plus a lease, so a briefly over-broad rank costs contention for one interval and never
  -- correctness.
  IF NOT FOUND THEN
    RETURN QUERY SELECT 0::INTEGER, 1::INTEGER;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.calculate_instance_rank IS
'Calculates partition rank for an instance based on active instances. Used for partition-based load balancing in orphaned work claiming. An instance missing from the active set degrades to a solo rank (0 of 1) rather than raising, so a lapsed heartbeat can never lock an instance out of claiming.';
