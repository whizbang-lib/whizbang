-- Migration: 205_StampWalksOnlyEligibleRows.sql
-- Date: 2026-10-11
-- Description: A stamp reads only rows it may stamp, so an open transaction behind the horizon costs it nothing (#1217).
--
--              197's walk reads unstamped rows in insertion order and filters each on xmin < horizon. xmin is a
--              system column no index can hold, so the walk cannot skip a row that is not yet eligible: it reads it
--              to find out. An import's oldest open transaction holds the horizon while the rest of it commits behind,
--              so every row the import wrote is unstamped and not yet eligible, the walk never reaches its batch, and
--              each call reads all of them and stamps none. The worker retries on its fenced interval while rows are
--              pending, so the read repeats every 250 ms inside the fenced transaction, holding the role row the
--              holder's vote has to write: measured under bulk imports, votes waited a median of 14.5 s behind stamps
--              that ran, unblocked, for up to 47 s.
--
--              The fix records the inserting transaction's id when the row is written and indexes it:
--
--                * wh_event_store.insert_xid, by default the current top-level transaction id, reduced to the 32-bit
--                  form that xmin and pg_stat_activity.backend_xid carry, so it compares with the horizon exactly as
--                  xmin does (the horizon has always been read in that form; see 116). Added without a default and
--                  then given one, so existing rows are not rewritten; the unstamped ones take their xmin here.
--                * The walk range-scans idx_event_store_unstamped_insert_xid for insert_xid < horizon and stops at the
--                  batch, so it reads only rows past the horizon, whatever is behind it.
--
--              What the order becomes (the owner's decision on #1217, refining #1225):
--                * Within a stream, the sequence follows the versions. Unchanged: each chosen stream is stamped from
--                  its lowest unstamped version and stops at the first version not yet eligible or held elsewhere.
--                * Rows stamped in one call are numbered in insertion order. Unchanged.
--                * Which rows a call takes, when more are eligible than one batch holds, follows the order their
--                  transactions began in (insertion order among rows of one transaction), where 197 took them in
--                  insertion order. Across streams that are not numbered together, the earlier transaction goes first.
--                * A row stamped later always has a higher value than every row stamped before it. Unchanged.
--
--              The prefix's eligibility check keeps 197's xmin < horizon. A row written in a subtransaction carries
--              that subtransaction's id in xmin and the top-level id in insert_xid; the walk may then reach a row the
--              prefix does not yet admit, and the row waits for the next call, as 197 would have made it wait.
--
--              idx_event_store_unstamped_insert_order (197) no longer serves the walk and is kept: dropping an index
--              is decided on evidence of no reader, not on one fewer.
--
-- Dependencies: 197 (stamp_pending_commit_sequences, copied verbatim apart from the walk; insert_order),
--               116 (the per-database horizon)
-- Objects: wh_event_store.insert_xid, idx_event_store_unstamped_insert_xid, stamp_pending_commit_sequences
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_PERSPECTIVE__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- No default on the ADD: a volatile default would rewrite every existing row under an exclusive lock.
ALTER TABLE __SCHEMA__.wh_event_store ADD COLUMN IF NOT EXISTS insert_xid xid8;
ALTER TABLE __SCHEMA__.wh_event_store
  ALTER COLUMN insert_xid SET DEFAULT ((pg_current_xact_id()::text::numeric % 4294967296)::text::xid8);

-- The rows still waiting for a stamp: the id that wrote them is their xmin.
UPDATE __SCHEMA__.wh_event_store
SET insert_xid = xmin::text::xid8
WHERE commit_sequence IS NULL
  AND insert_xid IS NULL;

COMMENT ON COLUMN __SCHEMA__.wh_event_store.insert_xid IS
'205 (#1217): the id of the transaction that inserted the row, in the 32-bit form xmin carries, so the commit-order stamper can find rows past the visibility horizon through an index instead of reading every unstamped row. NULL only on rows stamped before 205.';

-- The stamp's walk: unstamped rows by the transaction that wrote them, then by insertion order within it.
CREATE INDEX IF NOT EXISTS idx_event_store_unstamped_insert_xid
  ON __SCHEMA__.wh_event_store (insert_xid, insert_order)
  WHERE commit_sequence IS NULL;

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
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

  -- The statement below is written to be read through its indexes and nothing else. Scans that read every
  -- row are switched off for this statement alone and put back after it (197).
  PERFORM set_config('enable_seqscan', 'off', true);
  PERFORM set_config('enable_bitmapscan', 'off', true);

  WITH reached AS MATERIALIZED (
    -- The walk (205): unstamped rows past the horizon, found through the id of the transaction that wrote
    -- them, earliest transaction first and insertion order within one, stopped at the batch. A row behind
    -- the horizon is not read at all. It decides which streams move and how many rows each may stamp; it is
    -- not the set stamped.
    SELECT es.stream_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.commit_sequence IS NULL
      AND es.insert_xid < v_horizon
    ORDER BY es.insert_xid, es.insert_order
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

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences(INTEGER, BOOLEAN) IS
'205 supersedes 197. Stamps rows past the visibility horizon (unchanged): the walk reads only unstamped rows past the horizon, through insert_xid, earliest transaction first and insertion order within one, and stops at the batch, so rows behind the horizon cost nothing; each stream it reaches is stamped from its lowest unstamped version and stops at the first that is not yet eligible or is held elsewhere; values go out in insertion order across the batch and version order within each stream. Within a stream commit_sequence follows the versions. The post-stamp ring is 132''s. p_notify_owners is retained for signature compatibility and ignored. Returns count of rows stamped.';
