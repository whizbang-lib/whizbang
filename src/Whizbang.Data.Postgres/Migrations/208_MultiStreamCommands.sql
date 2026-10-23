-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 208_MultiStreamCommands.sql
-- Date: 2026-10-13
-- Description: Multi-stream commands (#1358). A command that names a set of streams is recorded once as a run, with one
--              work item per stream. Every instance claims only the run's streams it owns, by the rule the work claim
--              uses: a stream whose lease this instance holds; otherwise, unless a live sibling holds its lease, a stream
--              whose partition belongs to this instance under the published assignment (203), or, with no current
--              assignment, under the ranking of the live instances (204). The partition hash stays here, so nothing in
--              C# has to reproduce it.
--
--              The claim reads wh_active_streams and never writes it: pinning an unowned stream would add a writer to the
--              work tables' lock order, and is not needed, because an unowned stream's live work goes to its partition
--              owner, which this rule also picks. Claimed items are leased, so an instance that dies mid-stream lets the
--              lease lapse and the stream is offered again. An outcome counts only from the instance holding the item.
--              A failure releases the item until it has had its attempts, then is final. A run completes once, when its
--              last stream reports.
--
--              The payload and scope are TEXT, not JSONB: jsonb reorders keys, and a polymorphic payload's type
--              discriminator must stay first.
--
-- Dependencies: 001 (compute_partition), 007 (wh_active_streams), 203 (wh_published_partition_assignment),
--               204 (wh_direct_alive_lock_holders and the liveness rule)
-- Objects: wh_multi_stream_runs, wh_multi_stream_run_streams, wh_record_multi_stream_run, wh_claim_multi_stream_work,
--          wh_complete_multi_stream_work, wh_read_multi_stream_run, wh_purge_multi_stream_runs
-- Constants: none.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_multi_stream_runs (
  run_id UUID PRIMARY KEY,
  command_type TEXT NOT NULL,
  payload TEXT NOT NULL,
  scope TEXT NULL,
  stream_count INTEGER NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
  completed_at TIMESTAMPTZ NULL
);

COMMENT ON TABLE __SCHEMA__.wh_multi_stream_runs IS
  '208 (#1358): one row per multi-stream command run, recorded by whichever instance received the command. '
  'completed_at is set once, when the last of its streams reports.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_multi_stream_run_streams (
  run_id UUID NOT NULL REFERENCES __SCHEMA__.wh_multi_stream_runs (run_id) ON DELETE CASCADE,
  stream_id UUID NOT NULL,
  partition_number INTEGER NOT NULL,
  instance_id UUID NULL,
  lease_expiry TIMESTAMPTZ NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  outcome SMALLINT NULL,
  error TEXT NULL,
  completed_at TIMESTAMPTZ NULL,
  PRIMARY KEY (run_id, stream_id)
);

COMMENT ON TABLE __SCHEMA__.wh_multi_stream_run_streams IS
  '208 (#1358): one work item per stream of a multi-stream run. instance_id and lease_expiry hold a claim; outcome is '
  'NULL while pending, 1 succeeded, 2 failed after its last attempt (error holds the last failure).';

-- The claim's only scan: pending items, oldest run first.
CREATE INDEX IF NOT EXISTS idx_multi_stream_run_streams_pending
ON __SCHEMA__.wh_multi_stream_run_streams (run_id, stream_id)
INCLUDE (partition_number, instance_id, lease_expiry)
WHERE outcome IS NULL;

-- <docs>fundamentals/dispatcher/multi-stream-commands#intake</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_ANewRun_IsPendingForEveryStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_TheSameRunTwice_RecordsItOnceAndSaysSoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_ANewRun_WakesEveryInstanceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_multi_stream_run(
  p_run_id UUID,
  p_command_type TEXT,
  p_payload TEXT,
  p_scope TEXT,
  p_stream_ids UUID[],
  p_partition_count INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_streams UUID[];
BEGIN
  SELECT array_agg(DISTINCT s) INTO v_streams FROM unnest(p_stream_ids) AS s;

  INSERT INTO __SCHEMA__.wh_multi_stream_runs (run_id, command_type, payload, scope, stream_count)
  VALUES (p_run_id, p_command_type, p_payload, p_scope, cardinality(v_streams))
  ON CONFLICT (run_id) DO NOTHING;
  IF NOT FOUND THEN
    -- A repeat delivery: the first recording stands.
    RETURN FALSE;
  END IF;

  INSERT INTO __SCHEMA__.wh_multi_stream_run_streams (run_id, stream_id, partition_number)
  SELECT p_run_id, s, __SCHEMA__.compute_partition(s, p_partition_count)
  FROM unnest(v_streams) AS s;

  -- Delivered at commit, to every instance listening; a poll is the backstop.
  PERFORM pg_notify('wh_multi_stream_work', p_run_id::TEXT);
  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_record_multi_stream_run(UUID, TEXT, TEXT, TEXT, UUID[], INTEGER) IS
  '208 (#1358): records a multi-stream run and one pending item per distinct stream, idempotent on the run id; '
  'returns false for a run already recorded. Notifies wh_multi_stream_work.';

-- <docs>fundamentals/dispatcher/multi-stream-commands#ownership</docs>
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
  SELECT c.run_id, c.stream_id, r.command_type, r.payload, r.scope, c.attempts
  FROM claimed c
  JOIN __SCHEMA__.wh_multi_stream_runs r ON r.run_id = c.run_id
  ORDER BY c.run_id, c.stream_id;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_claim_multi_stream_work(UUID, INTEGER, INTEGER, BIGINT, BIGINT) IS
  '208 (#1358): claims up to p_batch_size pending multi-stream items this instance owns (its stream lease, else its '
  'partition share unless a live sibling holds the lease), leasing each and counting the attempt. Reads '
  'wh_active_streams; never writes it.';

-- One overload only: the arity changed while this migration was unreleased, and a stale overload would make every
-- unqualified call ambiguous (42725).
SELECT __SCHEMA__.drop_all_overloads('wh_complete_multi_stream_work');

-- <docs>fundamentals/dispatcher/multi-stream-commands#outcomes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_TheLastStream_CompletesTheRunOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_AFailureBelowTheAttemptLimit_ReleasesTheStreamForAnotherAttemptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_AFailureAtTheAttemptLimit_RecordsTheStreamFailedWithItsErrorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_AStreamThisInstanceNoLongerHolds_IsNotRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_AReleasedStream_IsOfferedAgainAfterTheDelayWithoutCountingAnAttemptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_HoldsTheRunUntilItCommits_SoTwoLastOutcomesCannotBothMissTheCompletionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandEndToEndTests.cs:ACommandOverManyStreams_IsWorkedOncePerStreamEachOnItsOwnerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_complete_multi_stream_work(
  p_instance_id UUID,
  p_run_ids UUID[],
  p_stream_ids UUID[],
  p_errors TEXT[],
  p_released BOOLEAN[],
  p_max_attempts INTEGER,
  p_release_seconds INTEGER
) RETURNS TABLE (
  run_id UUID,
  command_type TEXT,
  stream_count INTEGER,
  succeeded INTEGER,
  failed INTEGER
)
LANGUAGE plpgsql
AS $$
DECLARE
  v_now TIMESTAMPTZ := clock_timestamp();
BEGIN
  -- Only items this instance still holds: a worker whose claim lapsed and was taken over cannot record over the new one.
  -- A release (no handler on this instance, as in a rolling update) hands the item back without counting the attempt
  -- and keeps it leased to this instance for p_release_seconds, so it is not claimed straight back: after that the
  -- stream's owner at the time, on whatever build it runs, is offered it again.
  UPDATE __SCHEMA__.wh_multi_stream_run_streams rs
  SET outcome = CASE WHEN o.released THEN NULL
                     WHEN o.error IS NULL THEN 1::SMALLINT
                     WHEN rs.attempts >= p_max_attempts THEN 2::SMALLINT END,
      error = o.error,
      attempts = CASE WHEN o.released THEN rs.attempts - 1 ELSE rs.attempts END,
      completed_at = CASE WHEN NOT o.released AND (o.error IS NULL OR rs.attempts >= p_max_attempts) THEN v_now END,
      instance_id = CASE WHEN o.released OR o.error IS NULL OR rs.attempts >= p_max_attempts THEN rs.instance_id END,
      lease_expiry = CASE WHEN o.released THEN v_now + make_interval(secs => p_release_seconds) END
  FROM unnest(p_run_ids, p_stream_ids, p_errors, p_released) AS o (run_id, stream_id, error, released)
  WHERE rs.run_id = o.run_id
    AND rs.stream_id = o.stream_id
    AND rs.instance_id = p_instance_id
    AND rs.outcome IS NULL;

  -- The run row serializes completion. Two instances recording a run's last two outcomes at once would each see the
  -- other's item as still pending (uncommitted) and neither would complete the run. Taking the run row first makes the
  -- second wait for the first to commit; under READ COMMITTED its next statement then sees that outcome. Runs are
  -- locked in id order; items are never locked after a run, so the order cannot invert.
  PERFORM 1
  FROM __SCHEMA__.wh_multi_stream_runs r
  WHERE r.run_id IN (SELECT DISTINCT x FROM unnest(p_run_ids) AS x)
    AND r.completed_at IS NULL
  ORDER BY r.run_id
  FOR UPDATE;

  RETURN QUERY
  UPDATE __SCHEMA__.wh_multi_stream_runs r
  SET completed_at = v_now
  WHERE r.run_id IN (SELECT DISTINCT x FROM unnest(p_run_ids) AS x)
    AND r.completed_at IS NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_multi_stream_run_streams p WHERE p.run_id = r.run_id AND p.outcome IS NULL)
  RETURNING r.run_id, r.command_type, r.stream_count,
    (SELECT count(*)::INTEGER FROM __SCHEMA__.wh_multi_stream_run_streams s WHERE s.run_id = r.run_id AND s.outcome = 1),
    (SELECT count(*)::INTEGER FROM __SCHEMA__.wh_multi_stream_run_streams s WHERE s.run_id = r.run_id AND s.outcome = 2);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_complete_multi_stream_work(UUID, UUID[], UUID[], TEXT[], BOOLEAN[], INTEGER, INTEGER) IS
  '208 (#1358): records outcomes for items this instance holds (a NULL error is success; a failure is final at '
  'p_max_attempts, else the item is released; a release hands the item back uncounted for p_release_seconds) and '
  'completes, once, each run left with nothing pending.';

-- <docs>fundamentals/dispatcher/multi-stream-commands#outcomes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_ANewRun_IsPendingForEveryStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ReadRunAsync_AnUnknownRun_IsNullAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_read_multi_stream_run(p_run_id UUID)
RETURNS TABLE (
  run_id UUID,
  command_type TEXT,
  stream_count INTEGER,
  succeeded INTEGER,
  failed INTEGER,
  pending INTEGER,
  created_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ
)
LANGUAGE sql
STABLE
AS $$
  SELECT r.run_id, r.command_type, r.stream_count,
    count(*) FILTER (WHERE s.outcome = 1)::INTEGER,
    count(*) FILTER (WHERE s.outcome = 2)::INTEGER,
    count(*) FILTER (WHERE s.outcome IS NULL)::INTEGER,
    r.created_at, r.completed_at
  FROM __SCHEMA__.wh_multi_stream_runs r
  JOIN __SCHEMA__.wh_multi_stream_run_streams s ON s.run_id = r.run_id
  WHERE r.run_id = p_run_id
  GROUP BY r.run_id, r.command_type, r.stream_count, r.created_at, r.completed_at;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_read_multi_stream_run(UUID) IS
  '208 (#1358): a multi-stream run''s counts by outcome; no row when the run is unknown or purged.';

-- <docs>fundamentals/dispatcher/multi-stream-commands#retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:PurgeCompletedAsync_DeletesOnlyRunsCompletedBeforeTheCutoffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:PurgeCompletedAsync_ACutoffBeforeTheCompletion_KeepsTheRunAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_purge_multi_stream_runs(p_completed_before TIMESTAMPTZ)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_deleted INTEGER;
BEGIN
  DELETE FROM __SCHEMA__.wh_multi_stream_runs r
  WHERE r.completed_at IS NOT NULL AND r.completed_at < p_completed_before;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  RETURN v_deleted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_purge_multi_stream_runs(TIMESTAMPTZ) IS
  '208 (#1358): deletes completed multi-stream runs (and, by cascade, their items) that completed before the cutoff. '
  'A run with a pending item is never deleted.';
