-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 558_StreamCleanupFindsPendingOutboxWorkByIndex.sql
-- Date: 2026-11-23
-- Description: Stream cleanup finds a stream's pending outbox work through an index, so an eviction reads what its
--              batch holds whatever the outbox holds (#2538).
--
--              cleanup_completed_streams evicts a stream's ledger row only when no work table holds pending work for
--              it. Its outbox probe asked for stream_id = $1 AND processed_at IS NULL, and no outbox index answers
--              that predicate, so for each candidate stream with no pending inbox or perspective work it read the
--              whole outbox. Measured with EXPLAIN (ANALYZE, BUFFERS) over 12,500 pending outbox rows (753 blocks):
--              a batch of 100 streams, 50 of them evictable, read 38,109 blocks (37,650 in the outbox); a batch of
--              1,000 read 381,890 planned for its arguments and 381,015 planned generically.
--
--              The probe now also asks published_at IS NULL, the predicate of idx_outbox_stream_run (171), so it is
--              one descent of that index. The rows it finds are the same for every row the framework writes:
--              published_at is set only together with processed_at (completion in debug mode retains the row with
--              both stamped; otherwise the row is deleted). A published row is no work the claim can take either:
--              the claim's run walk and the store's emptiness probe already skip it. The same batches now read 510
--              blocks (100) and 5,891 and 5,016 blocks (1,000, custom and generic plans). No index is added, so the
--              outbox's write path is unchanged. cleanup_completed_streams is copied whole from its last word (536)
--              with that delta; nothing else changes.
--
-- Reversal: reversible (536's definition restores the function; a stream whose only outbox row is published but not
--           processed, a state no framework path writes, would be held in the ledger again)
-- Dependencies: 536 (cleanup_completed_streams' last word), 171 (idx_outbox_stream_run)
-- Objects: cleanup_completed_streams

-- ---------------------------------------------------------------------------------------------
-- cleanup_completed_streams: copied from its last word (536), with the outbox probe answered by idx_outbox_stream_run.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('cleanup_completed_streams');

-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorDeepPathTests.cs:CleanupCompletedStreamsAsync_NullStreamList_ReturnsZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CleanupCompletedStreams_ReadsWhatItsBatchHolds_NotTheBacklogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CleanupCompletedStreams_EvictsOnlyStreamsWithNoPendingWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CleanupCompletedStreams_WithAFullOutbox_ReadsWhatItsBatchHolds_NotTheOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CleanupCompletedStreams_AnOutboxRowAlreadyPublished_DoesNotHoldTheStreamAsync</tests>
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
        -- 558 (#2538): pending is what the claim can still take, unprocessed and unpublished, the predicate of
        -- idx_outbox_stream_run, so the probe is one descent of that index. processed_at IS NULL alone matched no
        -- outbox index and read the whole outbox per candidate stream. The framework sets published_at only together
        -- with processed_at, and the claim and the store's emptiness probe already skip a published row.
        SELECT 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = a.stream_id
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
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
  'batch holds, never the queue. 558: the outbox probe asks for unprocessed, unpublished rows, the work the claim can '
  'still take, which idx_outbox_stream_run answers in one descent.';
