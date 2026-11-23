-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 558_StreamCleanupFindsPendingOutboxWorkByIndex.sql (#2538): cleanup_completed_streams as 536 defined
-- it, copied whole. The outbox probe goes back to processed_at IS NULL alone, which no outbox index answers.

-- ---------------------------------------------------------------------------------------------
-- cleanup_completed_streams: 536's body, copied whole.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('cleanup_completed_streams');

-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorDeepPathTests.cs:CleanupCompletedStreamsAsync_NullStreamList_ReturnsZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CleanupCompletedStreams_ReadsWhatItsBatchHolds_NotTheBacklogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CleanupCompletedStreams_EvictsOnlyStreamsWithNoPendingWorkAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.cleanup_completed_streams(
  p_stream_ids UUID[]
) RETURNS INTEGER AS $$
DECLARE
  v_evicted INTEGER;
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  -- Single set-based DELETE: remove active_streams rows for input stream_ids that
  -- have no pending work across the three work tables. The "no pending" check filters
  -- on processed_at IS NULL (outbox, inbox, perspective_events), same shape used by
  -- claim_orphaned_*. Eviction is safe because the next event-store call for that
  -- stream re-runs the wh_active_streams UPSERT.
  -- 536: each check is a scalar subquery with LIMIT 1, run once per candidate stream as an index probe that stops at
  -- the first pending row. A NOT EXISTS may be planned as an anti join, and for a large batch the perspective one
  -- hashed every pending row of the queue.
  WITH evicted AS (
    DELETE FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = ANY(p_stream_ids)
      AND (
        SELECT 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = a.stream_id
          AND o.processed_at IS NULL
        LIMIT 1
      ) IS NULL
      AND (
        -- 162: stream_id is a write-once copy here and processed_at moved, so the probe reads the
        -- narrow table. This runs per candidate stream, so keeping it off the wide row matters.
        SELECT 1 FROM __SCHEMA__.wh_inbox_state ist
        WHERE ist.stream_id = a.stream_id
          AND ist.processed_at IS NULL
        LIMIT 1
      ) IS NULL
      AND (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = a.stream_id
          AND pe.processed_at IS NULL
        LIMIT 1
      ) IS NULL
    RETURNING a.stream_id
  )
  SELECT COUNT(*)::INTEGER INTO v_evicted FROM evicted;

  RETURN COALESCE(v_evicted, 0);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.cleanup_completed_streams(UUID[]) IS
  'Evicts the stream ledger rows of the given streams that have no pending outbox, inbox or perspective work. 536: '
  'each work table is probed once per stream and the probe stops at the first pending row, so a call reads what its '
  'batch holds, never the queue.';
