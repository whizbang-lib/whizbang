-- Migration: 197_CommitSequenceInsertionOrder.sql
-- Date: 2026-10-08
-- Description: commit_sequence is assigned in insertion order, a stream's versions are never numbered out of
--              order, and a stamp costs what it stamps (#1225, #1062).
--
--              stamp_pending_commit_sequences numbered every row past the visibility horizon in transaction-id
--              (xmin) order. Two defects followed from that key:
--
--                * The order could invert a stream. A transaction's id is assigned at its first write, not when
--                  it takes the stream's lock, so a transaction that wrote something else first carries a lower
--                  id than one that appended to the same stream before it and committed first. Numbered by id,
--                  the later version received the lower sequence, and every reader that walks a stream in
--                  commit_sequence order (get_stream_events, wh_collective_sink_queue, the outbox publish order,
--                  replay) saw it backwards. Reproduced in CommitSequenceStreamOrderSqlTests, in one batch and
--                  split across two.
--                * The key cannot be indexed (xmin is a system column), so each call sorted every unstamped row
--                  before its LIMIT: a drain cost the backlog squared over the batch (#1062).
--
--              The owner's decision (#1225): rows that become eligible together may be numbered in insertion
--              order, an indexed insert-time value, with xmin < horizon kept only as the visibility fence. So:
--
--                * wh_event_store.insert_order, from wh_event_insert_order_seq (CACHE 1, so the order holds
--                  across sessions), assigned by default at insert. Added without a default and then given one,
--                  so existing rows are not rewritten; the unstamped ones are numbered once here in xmin order,
--                  the order they would have been stamped in.
--                * The stamp walks idx_event_store_unstamped_insert_order (insertion order, unstamped rows) past
--                  the horizon and stops at the batch. The walk chooses which streams move; each chosen stream is
--                  stamped from its lowest unstamped version through idx_event_store_unstamped_stream_version, and
--                  stops at the first version that is not yet eligible or that another session holds. A later
--                  version is therefore never numbered while an earlier one waits, in one call or across two.
--                * Sequence values are allocated for the batch and handed out in insertion order across streams,
--                  and in version order within each stream, so the promise does not rest on the order a join
--                  happens to emit rows in.
--
--              What commit_sequence promises after this change: within a stream it follows the versions; across
--              streams, a row stamped later always has a higher value than every row stamped before it (cursor
--              safety; nextval is monotonic and only rows past the horizon are stamped), and rows stamped in one
--              call are numbered in insertion order. Only one stamper runs at a time (the leader lock); the
--              statement stays safe for concurrent callers, which never stamp a row twice and never stamp a
--              version past one another session holds.
--
--              Cost: the walk reads the batch's index entries plus any rows still inside the horizon; each chosen
--              stream reads as many unstamped entries as it contributes. No step reads the backlog.
--
-- Dependencies: 132 (stamp_pending_commit_sequences: horizon and ring unchanged), 046 (commit_sequence,
--               wh_commit_seq, idx_event_store_unstamped)
-- Objects: stamp_pending_commit_sequences, wh_event_insert_order_seq, idx_event_store_unstamped_insert_order,
--          idx_event_store_unstamped_stream_version
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_PERSPECTIVE__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

CREATE SEQUENCE IF NOT EXISTS __SCHEMA__.wh_event_insert_order_seq
  AS BIGINT
  MINVALUE 1
  START 1
  CACHE 1;

COMMENT ON SEQUENCE __SCHEMA__.wh_event_insert_order_seq IS
'197 (#1225): the insert-time order of wh_event_store rows, which orders rows the commit-order stamper numbers together. CACHE 1 so that two sessions'' values follow the order they inserted in.';

-- No default on the ADD: a volatile default would rewrite every existing row under an exclusive lock.
ALTER TABLE __SCHEMA__.wh_event_store ADD COLUMN IF NOT EXISTS insert_order BIGINT;
ALTER TABLE __SCHEMA__.wh_event_store
  ALTER COLUMN insert_order SET DEFAULT nextval('__SCHEMA__.wh_event_insert_order_seq');

-- The rows still waiting for a stamp, numbered in the order the previous stamper would have taken them.
UPDATE __SCHEMA__.wh_event_store es
SET insert_order = numbered.insert_order
FROM (
  SELECT ordered.event_id, nextval('__SCHEMA__.wh_event_insert_order_seq') AS insert_order
  FROM (
    SELECT u.event_id
    FROM __SCHEMA__.wh_event_store u
    WHERE u.commit_sequence IS NULL
      AND u.insert_order IS NULL
    ORDER BY u.xmin::text::xid8, u.event_id
  ) ordered
) numbered
WHERE es.event_id = numbered.event_id;

COMMENT ON COLUMN __SCHEMA__.wh_event_store.insert_order IS
'197 (#1225): insert-time order. The commit-order stamper numbers rows that become eligible together in this order; NULL only on rows stamped before 197.';

-- The stamp's walk: unstamped rows in insertion order.
CREATE INDEX IF NOT EXISTS idx_event_store_unstamped_insert_order
  ON __SCHEMA__.wh_event_store (insert_order)
  WHERE commit_sequence IS NULL;

-- Each chosen stream's unstamped versions, lowest first.
CREATE INDEX IF NOT EXISTS idx_event_store_unstamped_stream_version
  ON __SCHEMA__.wh_event_store (stream_id, version)
  WHERE commit_sequence IS NULL;

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
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
  v_seqscan TEXT := current_setting('enable_seqscan');
  v_bitmapscan TEXT := current_setting('enable_bitmapscan');
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

  -- The statement below is written to be read through its indexes and nothing else. The planner cannot
  -- estimate the fence (xmin is a system column) and guesses a third of the rows pass it, so on a store
  -- of a few tens of thousands of rows it judged a scan of the whole table and a sort cheaper than the
  -- ordered walk: measured at 43 tuples read per stamped row at a 40,000-row backlog, the backlog again.
  -- Scans that read every row are switched off for this statement alone and put back after it.
  PERFORM set_config('enable_seqscan', 'off', true);
  PERFORM set_config('enable_bitmapscan', 'off', true);

  WITH reached AS MATERIALIZED (
    -- The walk: eligible unstamped rows in insertion order, stopped at the batch. It decides which streams
    -- move and how many rows each may stamp; it is not the set stamped.
    SELECT es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.commit_sequence IS NULL
      AND es.xmin::text::xid8 < v_horizon
    ORDER BY es.insert_order
    LIMIT p_batch_size
  ),
  chosen AS MATERIALIZED (
    SELECT r.stream_id, count(*)::INTEGER AS row_budget
    FROM reached r
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
    -- statement's snapshot is not locked. Looked up by key: written as a join, the planner hashed the
    -- chosen rows against a walk of every unstamped row, which is the backlog again.
    SELECT es.event_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY (ARRAY(SELECT pr.event_id FROM prefixes pr WHERE pr.admitted))
      AND es.commit_sequence IS NULL
    ORDER BY es.event_id
    FOR UPDATE OF es SKIP LOCKED
  ),
  held AS MATERIALIZED (
    -- A version another session holds stops its stream here: nothing after it is stamped in this call.
    SELECT h.event_id, h.stream_id, h.version, h.insert_order
    FROM (
      SELECT pr.event_id, pr.stream_id, pr.version, pr.insert_order,
             bool_and(l.event_id IS NOT NULL) OVER (PARTITION BY pr.stream_id ORDER BY pr.version) AS contiguous
      FROM prefixes pr
      LEFT JOIN locked l ON l.event_id = pr.event_id
      WHERE pr.admitted
    ) h
    WHERE h.contiguous
  ),
  ranked AS (
    -- Insertion order across the batch; within a stream the same ranks go to its rows in version order.
    SELECT v.event_id, r.batch_rank
    FROM (
      SELECT event_id, stream_id, row_number() OVER (PARTITION BY stream_id ORDER BY version) AS k
      FROM held
    ) v
    JOIN (
      SELECT stream_id, row_number() OVER (ORDER BY insert_order, event_id) AS batch_rank,
             row_number() OVER (PARTITION BY stream_id ORDER BY insert_order, event_id) AS k
      FROM held
    ) r ON r.stream_id = v.stream_id AND r.k = v.k
  ),
  allocated AS MATERIALIZED (
    SELECT nextval('__SCHEMA__.wh_commit_seq') AS seq
    FROM generate_series(1, (SELECT count(*) FROM held))
  ),
  numbered AS (
    SELECT a.seq, row_number() OVER (ORDER BY a.seq) AS batch_rank
    FROM allocated a
  ),
  stamped AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET commit_sequence = n.seq
    FROM ranked rk
    JOIN numbered n ON n.batch_rank = rk.batch_rank
    WHERE es.event_id = rk.event_id
    RETURNING es.stream_id
  )
  SELECT COUNT(*)::INTEGER, array_agg(DISTINCT stream_id)
  INTO v_stamped_count, v_stream_ids
  FROM stamped;

  PERFORM set_config('enable_seqscan', v_seqscan, true);
  PERFORM set_config('enable_bitmapscan', v_bitmapscan, true);

  -- Unchanged from 132: every stamp that affected rows rings the owners; the debounce owns redundancy.
  IF v_stamped_count > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_stream_ids);
  END IF;

  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences IS
'197 supersedes 132. Stamps rows past the visibility horizon (unchanged): the walk reads unstamped rows in insertion order and stops at the batch; each stream it reaches is stamped from its lowest unstamped version and stops at the first that is not yet eligible or is held elsewhere; values go out in insertion order across streams and version order within each. Within a stream commit_sequence follows the versions. The post-stamp ring is 132''s. p_notify_owners is retained for signature compatibility and ignored. Returns count of rows stamped.';
