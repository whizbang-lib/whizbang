-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 213_MultiStreamClaimSendsEachPayloadOnce.sql
-- Date: 2026-10-19
-- Description: The multi-stream claim returns a run's payload on the run's first claimed row only; its other rows carry
--              NULL, and the store reuses the first row's payload for them. The claim returned the payload on every
--              row, so a run whose payload was large (a run recorded with all its stream ids, about 5 MB at 127,288
--              streams) sent it once per claimed stream, about 630 GB over one run, and ran its host out of memory.
--              Runs recorded from this release on store no stream ids in their payload; this keeps runs recorded
--              before it cheap to claim as well. The function is otherwise 208's, unchanged.
--
-- Dependencies: 208 (wh_claim_multi_stream_work and its tables)
-- Objects: wh_claim_multi_stream_work
-- Constants: none.

-- <docs>fundamentals/dispatcher/multi-stream-commands#ownership</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:TheClaimFunction_SendsEachRunsPayloadOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_UnderAPublishedAssignment_EachInstanceClaimsOnlyItsPartitionShareAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_WithNoAssignment_RanksTheLiveInstancesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_AStreamLeasedToThisInstance_IsClaimedWhateverItsPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_AStreamLeasedToADeadInstance_GoesToItsPartitionOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_AStreamWhoseClaimLapsed_IsOfferedAgainAsTheNextAttemptAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_claim_multi_stream_work(
  p_instance_id UUID,
  p_batch_size INTEGER,
  p_lease_seconds INTEGER,
  p_assignment_epoch BIGINT DEFAULT NULL,
  p_assignment_revision BIGINT DEFAULT NULL
) RETURNS TABLE (
  run_id UUID,
  stream_id UUID,
  command_type TEXT,
  payload TEXT,
  scope TEXT,
  attempt INTEGER
)
LANGUAGE plpgsql
AS $$
DECLARE
  v_now TIMESTAMPTZ := clock_timestamp();
  v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
  v_rank INTEGER;
  v_count INTEGER;
BEGIN
  -- The rank, as claim_work takes it (203, 204): the caller's cached assignment when it is still the published one,
  -- unexpired, and names this instance; otherwise this instance's place among the live instances, counting itself.
  IF p_assignment_epoch IS NOT NULL THEN
    SELECT array_position(pa.members, p_instance_id) - 1, cardinality(pa.members)
    INTO v_rank, v_count
    FROM __SCHEMA__.wh_published_partition_assignment pa
    WHERE pa.epoch = p_assignment_epoch
      AND pa.revision = p_assignment_revision
      AND pa.lease_expires_at > v_now;
  END IF;

  IF v_rank IS NULL THEN
    SELECT ranked.instance_rank, ranked.active_instance_count INTO v_rank, v_count
    FROM (
      SELECT live.instance_id,
             (ROW_NUMBER() OVER (ORDER BY live.instance_id) - 1)::INTEGER AS instance_rank,
             COUNT(*) OVER ()::INTEGER AS active_instance_count
      FROM (
        SELECT si.instance_id
        FROM __SCHEMA__.wh_service_instances si
        WHERE si.last_heartbeat_at >= v_stale_cutoff
           OR (si.connection_mode = 'direct'
               AND si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
        UNION
        SELECT p_instance_id
      ) live
    ) ranked
    WHERE ranked.instance_id = p_instance_id;
  END IF;

  RETURN QUERY
  WITH candidates AS (
    SELECT rs.run_id, rs.stream_id
    FROM __SCHEMA__.wh_multi_stream_run_streams rs
    LEFT JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = rs.stream_id
    WHERE rs.outcome IS NULL
      AND (rs.instance_id IS NULL OR rs.lease_expiry <= v_now)
      AND (
        -- Mine: this instance holds the stream's lease.
        (ast.assigned_instance_id = p_instance_id AND ast.lease_expiry > v_now)
        OR (
          -- Otherwise the partition decides, unless a live sibling holds the stream's lease.
          rs.partition_number % v_count = v_rank
          AND NOT (
            ast.assigned_instance_id IS NOT NULL
            AND ast.assigned_instance_id <> p_instance_id
            AND ast.lease_expiry > v_now
            AND EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
                AND (si.last_heartbeat_at >= v_stale_cutoff
                     OR (si.connection_mode = 'direct'
                         AND si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))))
          )
        )
      )
    ORDER BY rs.run_id, rs.stream_id
    LIMIT p_batch_size
    FOR UPDATE OF rs SKIP LOCKED
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_multi_stream_run_streams rs
    SET instance_id = p_instance_id,
        lease_expiry = v_now + make_interval(secs => p_lease_seconds),
        attempts = rs.attempts + 1
    FROM candidates c
    WHERE rs.run_id = c.run_id AND rs.stream_id = c.stream_id
    RETURNING rs.run_id, rs.stream_id, rs.attempts
  )
  -- A run's payload is the same for every one of its streams, so it is sent on the run's first row only (213): sent on
  -- every row, a large payload crossed the wire once per claimed stream. The CASE keeps it from being read for the rest.
  SELECT c.run_id, c.stream_id, r.command_type,
         CASE WHEN ROW_NUMBER() OVER (PARTITION BY c.run_id ORDER BY c.stream_id) = 1 THEN r.payload END,
         r.scope, c.attempts
  FROM claimed c
  JOIN __SCHEMA__.wh_multi_stream_runs r ON r.run_id = c.run_id
  ORDER BY c.run_id, c.stream_id;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_claim_multi_stream_work(UUID, INTEGER, INTEGER, BIGINT, BIGINT) IS
  '213: claims up to p_batch_size pending multi-stream items this instance owns (its stream lease, else its '
  'partition share unless a live sibling holds the lease), leasing each and counting the attempt. Reads '
  'wh_active_streams; never writes it. A run''s payload is returned on its first claimed row only; later rows carry NULL.';
