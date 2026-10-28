-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 248_StampReadsOnlyTheBatchItStamps.sql
-- Date: 2026-10-27
-- Description: A stamp costs what it stamps whatever the planner believes about the backlog, so a whole-store backlog
--              drains in time proportional to its size (#1786).
--
--              An upgrade from below 046 leaves every historical event unstamped, and the columns the stamp plans on
--              (commit_sequence, insert_order, insert_xid) were all written by the migrations in that same pass, so
--              their statistics describe a table that no longer exists until the next ANALYZE. 205's statement is one
--              query of chained CTEs; with estimates that small, and with a generic plan once the stamper's connection
--              has called it five times, the planner joined the batch's own intermediate sets to each other by nested
--              loop. Each of those joins compared every row of the batch with every other: measured on a test backlog
--              of about 200,000 unstamped rows sharing one insert_xid (the shape 197 and 205 leave), a call of 1,000
--              rows removed 999,000 rows by join filter, twice, and took 0.4 to 1.2 s once generic-planned; the same
--              calls take 0.03 to 0.2 s after this migration. The cost grew with the square of the batch, so halving
--              the batch (#1327) chased a cost whose shape the worker could not see.
--
--              The stamp is now four statements, each of which can be planned only one way:
--
--                * The walk, alone, with sort disabled for it: the only plan that delivers rows in
--                  (insert_xid, insert_order) order without a sort is a range scan of
--                  idx_event_store_unstamped_insert_xid that stops at the batch. A plan that read and sorted the
--                  whole unstamped set through another index is no longer one the planner can prefer.
--                * The prefixes, the lock and the ranking, with no join between the batch's own sets: the rows the
--                  lock took are matched to the prefixes they came from by grouping the two lists together, and the
--                  insertion-order ranks are handed to each stream's rows in version order by sorting both lists by
--                  stream and pairing them by position. Grouping and sorting cost the batch times its logarithm under
--                  any estimate.
--                * The sequence values, allocated for the batch and sorted (unchanged).
--                * The update, by primary key from the paired lists, with hash and merge joins disabled for it, so it
--                  never trades its key lookups for a scan of the table.
--
--              What does not change: the visibility fence (116: the oldest in-flight transaction of this database,
--              prepared transactions included, xmin < horizon for every admitted row), which rows a call takes and in
--              what order (205), that a stream is stamped from its lowest unstamped version and stops at the first
--              version not yet eligible or held by another session (197), that values go out in insertion order
--              across the batch and version order within a stream, that a row another session stamped or holds is
--              never stamped (the lock re-asserts the predicate and skips locked rows; 172's rule 4), the signature,
--              and the post-stamp ring (132). The rows a call locks stay locked until its transaction ends, as they
--              did when the lock and the update were one statement.
--
--              Each scan the function switches off is put back after its statement, as 197 does.
--
-- Dependencies: 205 (stamp_pending_commit_sequences, copied apart from the statement split; insert_xid and
--               idx_event_store_unstamped_insert_xid), 197 (insert_order, idx_event_store_unstamped_stream_version),
--               116 (the per-database horizon), 132 (the ring)
-- Objects: stamp_pending_commit_sequences
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_PERSPECTIVE__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklogTheStatisticsDoNotKnow_IsReadOnceThroughTheInsertXidIndexAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklogTheStatisticsDoNotKnow_NeverComparesTheBatchWithItselfAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklogTheStatisticsDoNotKnow_DrainsInInsertionOrderBehindTheFenceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_RowsBehindAnOpenTransaction_AreNotReadToFindNothingEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_StreamsInDifferentBatches_FollowTheOrderTheirTransactionsBeganInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ALowerIdTransactionAppendsAfterAHigherOne_TheStreamKeepsItsVersionOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_TheEarlierVersionIsNotYetEligible_TheLaterOneWaitsForItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_OneTransactionInsertsAStreamOutOfVersionOrder_TheSequenceStillFollowsTheVersionsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABatchSmallerThanAStream_StampsTheStreamFromItsFirstVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_StreamsEligibleTogether_AreNumberedInInsertionOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitSequenceStreamOrderSqlTests.cs:Stamp_ABacklog_CostsWhatTheBatchStampsNotWhatTheBacklogHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StampPendingCommitSequencesSqlTests.cs:Stamp_DefersWhileOlderTxStillInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StampPendingCommitSequencesSqlTests.cs:Stamp_ConcurrentCallers_NoRowStampedTwiceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StampPendingCommitSequencesSqlTests.cs:Stamp_BatchSizeLimitsPerCallAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.stamp_pending_commit_sequences(
  p_batch_size INTEGER DEFAULT 1000,
  p_notify_owners BOOLEAN DEFAULT FALSE
) RETURNS INTEGER AS $$
DECLARE
  v_stamped_count INTEGER;
  v_stream_ids UUID[];
  v_horizon xid8;
  v_reached UUID[];
  v_event_ids UUID[];
  v_ranks INTEGER[];
  v_sequences BIGINT[];
  v_seqscan TEXT := current_setting('enable_seqscan');
  v_bitmapscan TEXT := current_setting('enable_bitmapscan');
  v_sort TEXT := current_setting('enable_sort');
  v_hashjoin TEXT := current_setting('enable_hashjoin');
  v_mergejoin TEXT := current_setting('enable_mergejoin');
BEGIN
  -- The visibility fence, unchanged from 116: the oldest transaction id still in flight in THIS database
  -- (prepared transactions included), or the snapshot's xmax when none is. Every row whose inserting
  -- transaction is below it has committed or aborted, so nothing below it can still appear.
  SELECT LEAST(
    COALESCE((SELECT min(sa.backend_xid::text::xid8)
              FROM pg_stat_activity sa
              WHERE sa.datname = current_database()
                AND sa.pid <> pg_backend_pid()
                AND sa.backend_xid IS NOT NULL),
             pg_snapshot_xmax(pg_current_snapshot())),
    COALESCE((SELECT min(px.transaction::text::xid8)
              FROM pg_prepared_xacts px
              WHERE px.database = current_database()),
             pg_snapshot_xmax(pg_current_snapshot()))
  ) INTO v_horizon;

  -- The statements below are written to be read through their indexes and nothing else. Scans that read every
  -- row are switched off for them and put back after them (197).
  PERFORM set_config('enable_seqscan', 'off', true);
  PERFORM set_config('enable_bitmapscan', 'off', true);

  -- The walk (205, now its own statement): unstamped rows past the horizon, found through the id of the
  -- transaction that wrote them, earliest transaction first and insertion order within one, stopped at the batch.
  -- With sort off, the range scan of idx_event_store_unstamped_insert_xid is the only plan that yields this order,
  -- so the walk reads the batch and stops, however many rows are unstamped and whatever their statistics say.
  -- It decides which streams move and how many rows each may stamp; it is not the set stamped.
  PERFORM set_config('enable_sort', 'off', true);
  SELECT array_agg(w.stream_id)
  INTO v_reached
  FROM (
    SELECT es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.commit_sequence IS NULL
      AND es.insert_xid < v_horizon
    ORDER BY es.insert_xid, es.insert_order
    LIMIT p_batch_size
  ) w;
  PERFORM set_config('enable_sort', v_sort, true);

  IF v_reached IS NULL THEN
    PERFORM set_config('enable_seqscan', v_seqscan, true);
    PERFORM set_config('enable_bitmapscan', v_bitmapscan, true);
    RETURN 0;
  END IF;

  WITH chosen AS MATERIALIZED (
    SELECT r.stream_id, count(*)::INTEGER AS row_budget
    FROM unnest(v_reached) AS r(stream_id)
    GROUP BY r.stream_id
  ),
  prefixes AS MATERIALIZED (
    -- Each chosen stream from its lowest unstamped version, as many rows as the walk gave it. A row is
    -- admitted only while every lower version of its stream is eligible too.
    SELECT p.event_id, p.stream_id, p.version, p.insert_order,
           bool_and(p.eligible) OVER (PARTITION BY p.stream_id ORDER BY p.version) AS admitted
    FROM chosen c
    CROSS JOIN LATERAL (
      SELECT u.event_id, u.stream_id, u.version, u.insert_order,
             (u.xmin::text::xid8 < v_horizon) AS eligible
      FROM __SCHEMA__.wh_event_store u
      WHERE u.stream_id = c.stream_id
        AND u.commit_sequence IS NULL
      ORDER BY u.version
      LIMIT c.row_budget
    ) p
  ),
  locked AS MATERIALIZED (
    -- Re-asserts the predicate that chose the row (172's rule 4): a row another session stamped since this
    -- statement's snapshot is not locked. Looked up by key, never joined to a walk of the unstamped rows.
    SELECT es.event_id, es.stream_id, es.version, es.insert_order
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY (ARRAY(SELECT pr.event_id FROM prefixes pr WHERE pr.admitted))
      AND es.commit_sequence IS NULL
    ORDER BY es.event_id
    FOR UPDATE OF es SKIP LOCKED
  ),
  marked AS (
    -- Every admitted row once, and once more if the lock took it; grouped below. Grouping two lists of the batch
    -- costs the batch at any estimate, where a join of them could be planned as every row against every row.
    SELECT pr.event_id, pr.stream_id, pr.version, pr.insert_order, FALSE AS is_locked
    FROM prefixes pr
    WHERE pr.admitted
    UNION ALL
    SELECT l.event_id, l.stream_id, l.version, l.insert_order, TRUE
    FROM locked l
  ),
  admitted AS (
    SELECT m.event_id, m.stream_id, m.version, m.insert_order, bool_or(m.is_locked) AS is_locked
    FROM marked m
    GROUP BY m.event_id, m.stream_id, m.version, m.insert_order
  ),
  held AS (
    -- A version another session holds stops its stream here: nothing after it is stamped in this call.
    SELECT h.event_id, h.stream_id, h.version, h.insert_order
    FROM (
      SELECT a.event_id, a.stream_id, a.version, a.insert_order,
             bool_and(a.is_locked) OVER (PARTITION BY a.stream_id ORDER BY a.version) AS contiguous
      FROM admitted a
    ) h
    WHERE h.contiguous
  ),
  ranked AS (
    SELECT hd.event_id, hd.stream_id, hd.version,
           row_number() OVER (ORDER BY hd.insert_order, hd.event_id)::INTEGER AS batch_rank
    FROM held hd
  )
  -- Insertion order across the batch; within a stream the same ranks go to its rows in version order. Both
  -- lists are sorted by stream first and hold the same number of rows per stream, so the n-th row of a stream
  -- in version order sits at the same position as the stream's n-th rank: the arrays pair them by position.
  SELECT array_agg(rk.event_id ORDER BY rk.stream_id, rk.version),
         array_agg(rk.batch_rank ORDER BY rk.stream_id, rk.batch_rank)
  INTO v_event_ids, v_ranks
  FROM ranked rk;

  -- Sequence values for the batch, handed out in rank order so the promise does not rest on the order a
  -- statement happens to emit rows in.
  SELECT array_agg(a.seq ORDER BY a.seq)
  INTO v_sequences
  FROM (
    SELECT nextval('__SCHEMA__.wh_commit_seq') AS seq
    FROM generate_series(1, COALESCE(cardinality(v_event_ids), 0))
  ) a;

  -- The update reaches each row by its key. Hash and merge joins are off for it so that no estimate of the
  -- paired lists can make a scan of the table look cheaper than one lookup per row.
  PERFORM set_config('enable_hashjoin', 'off', true);
  PERFORM set_config('enable_mergejoin', 'off', true);
  WITH stamped AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET commit_sequence = v_sequences[s.batch_rank]
    FROM unnest(v_event_ids, v_ranks) AS s(event_id, batch_rank)
    WHERE es.event_id = s.event_id
    RETURNING es.stream_id
  )
  SELECT COUNT(*)::INTEGER, array_agg(DISTINCT stream_id)
  INTO v_stamped_count, v_stream_ids
  FROM stamped;
  PERFORM set_config('enable_hashjoin', v_hashjoin, true);
  PERFORM set_config('enable_mergejoin', v_mergejoin, true);

  PERFORM set_config('enable_seqscan', v_seqscan, true);
  PERFORM set_config('enable_bitmapscan', v_bitmapscan, true);

  -- Unchanged from 132: every stamp that affected rows rings the owners; the debounce owns redundancy.
  IF v_stamped_count > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_stream_ids);
  END IF;

  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences(INTEGER, BOOLEAN) IS
'248 supersedes 205. Stamps rows past the visibility horizon (unchanged): the walk, its own statement with sort off, range-scans insert_xid past the horizon, earliest transaction first and insertion order within one, and stops at the batch; each stream it reaches is stamped from its lowest unstamped version and stops at the first that is not yet eligible or is held elsewhere; values go out in insertion order across the batch and version order within each stream. No step joins the batch with itself and the update reaches each row by its key, so a call costs what it stamps under any statistics or plan. The post-stamp ring is 132''s. p_notify_owners is retained for signature compatibility and ignored. Returns count of rows stamped.';
