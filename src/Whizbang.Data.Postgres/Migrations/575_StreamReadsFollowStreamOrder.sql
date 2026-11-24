-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 575_StreamReadsFollowStreamOrder.sql
-- Date: 2026-11-23
-- Description: The perspective reads return a stream's events in stream order, never by id (#2551).
--
--              get_stream_events and wh_collective_sink_queue read a stream's events by commit sequence and broke a
--              tie (the unstamped events they admit past the stamp cutoff) by event id; fetch_events_by_ids,
--              fetch_pending_perspective_events and claim_and_fetch_pending_perspective_events returned a stream's
--              events by event id. An id is time-ordered and never stream position (573 and 574 version a stream in
--              emission and receive order). Each now orders by the stream's version, unique within a stream, after
--              the commit sequence where it read one. Only the ORDER BY changes.
--
--              Cost: the same rows are read; each sort key is a column of a row already in hand, so the sort costs
--              what it did (EXPLAIN in the commit report).
--
--              Number 575: reserved for this change (573 to 576).
--
-- Reversal: reversible (the down file restores each function's previous last word)
-- Dependencies: 566 (get_stream_events), 175 (wh_collective_sink_queue), 078 (fetch_events_by_ids), 042
--               (fetch_pending_perspective_events, claim_and_fetch_pending_perspective_events)
-- Objects: get_stream_events, wh_collective_sink_queue, fetch_events_by_ids, fetch_pending_perspective_events,
--          claim_and_fetch_pending_perspective_events
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- get_stream_events: copied from its last word (566_ClaimsDecideThroughTheOwnershipTable.sql), ordered by stream version, never by id (575).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

-- <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FirstEventInStreamReadTests.cs:GetStreamEvents_ReturnsWhetherEachEventIsTheFirstInItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCommitSequencePropagationSqlTests.cs:GetStreamEvents_StampedReturnedUnstampedExcludedOnSameStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveFailureCounterSqlTests.cs:GetStreamEvents_SurfacesFailures_ForTheDeadLetterDecisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCrossPodOwnershipTests.cs:LiveOwnersExpiredLeaseRow_NotStolenByAnotherInstance</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:GetStreamEvents_ReadsWhatItsBatchHolds_NotTheBacklogAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Fetch_TheNewOwnerFetchesTheStream_AndTheStoredOwnerNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StreamOrderNeverByIdGuardTests.cs:NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.get_stream_events(
  p_instance_id UUID,
  p_stream_ids UUID[],
  p_now TIMESTAMPTZ DEFAULT NOW(),
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT,
  out_event_work_id UUID,
  out_perspective_name VARCHAR(200),
  out_commit_sequence BIGINT,
  out_attempts INTEGER,
  out_failures INTEGER,
  out_first_in_stream BOOLEAN
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  v_stamp_grace CONSTANT INTERVAL := INTERVAL '5 seconds';
  v_stamp_cutoff TIMESTAMPTZ;
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stamp_cutoff := p_now - v_stamp_grace;

  -- Atomic claim+fetch (slice 25) + grace-windowed unstamped gate (mig 058) + live-owner gate
  -- (mig 059). A row is claimable only if its stream is NOT owned by a DIFFERENT live instance:
  -- enforcing single-writer-per-stream so two pods never apply one stream concurrently.
  -- 536: the event and the ledger row are each one probe per work row (LATERAL ... LIMIT 1 cannot be flattened into a
  -- join, so no plan can scan either table whole against the batch).
  WITH
  -- 566 (#2555): in map mode, the partitions this instance owns, read once per call through wh_partitions_i_own (the set
  -- form of wh_i_own: the same rule over the same table) and tested by hash membership, as the rank was tested by
  -- arithmetic. A per-row probe over the window would cost one index probe per row the window reads. Empty when off.
  mine_partitions AS MATERIALIZED (
    SELECT mp.partition_number
    FROM __SCHEMA__.wh_partitions_i_own(p_instance_id) AS mp(partition_number)
    WHERE v_map
  ),
  eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM __SCHEMA__.wh_perspective_events pe
    CROSS JOIN LATERAL (
      SELECT es.commit_sequence
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = pe.event_id
        AND es.stream_id = pe.stream_id
      LIMIT 1
    ) es
    LEFT JOIN LATERAL (
      -- mig 059: single-writer gate. Do not claim a row whose stream is owned by a different LIVE instance. 317
      -- (#2027): the owner counts while the one liveness rule calls it live. Caller-owned / unowned / dead-owner
      -- streams stay claimable.
      SELECT 1 AS held
      FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = pe.stream_id
        AND ast.assigned_instance_id IS NOT NULL
        AND ast.assigned_instance_id <> p_instance_id
        AND ast.assigned_instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l)
        -- 566: the stored owner decides nothing in map mode, so the ledger is not read.
        AND NOT v_map
      LIMIT 1
    ) owner ON TRUE
    WHERE pe.stream_id = ANY(p_stream_ids)
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      -- 536: the lease test is a filter on the batch's rows, never an index condition (IS TRUE keeps it out of the
      -- lease and per-instance indexes): as a range over a lease index it read every row the instance holds, or every
      -- lapsed row, whichever the statistics guessed was the smaller.
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      ) IS TRUE
      AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
      -- 566 (#2555): in map mode a row is fetched exactly when this instance owns its partition (the set form of
      -- wh_i_own); the live-owner gate is the legacy mode's.
      AND CASE WHEN v_map THEN (pe.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE owner.held IS NULL END
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = v_lease_expiry,
      attempts = pe.attempts + 1
  -- 536: by key, the locked rows as an index condition (an UPDATE ... FROM may be planned as a pass over the queue).
  WHERE pe.event_work_id = ANY(ARRAY(SELECT e.event_work_id FROM eligible e));

  RETURN QUERY
  SELECT
    pe.stream_id,
    es.event_id,
    es.event_type::TEXT,
    eb.event_data::TEXT,
    eb.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name,
    es.commit_sequence,
    pe.attempts,
    pe.failures,
    -- 264 (#1878): the event's position decides it, never a row. A version-1 event is first unless its stream also
    -- holds a version 0 (numbered from 0, before numbering was unified); the probe runs for version-1 rows only.
    CASE
      WHEN es.version = 0 THEN TRUE
      WHEN es.version = 1 THEN NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store e0
        WHERE e0.stream_id = es.stream_id AND e0.version = 0
      )
      ELSE FALSE
    END
  FROM __SCHEMA__.wh_perspective_events pe
  -- 536: the event by its key, one probe per work row.
  CROSS JOIN LATERAL (
    SELECT s.event_id, s.stream_id, s.event_type, s.scope, s.commit_sequence, s.version
    FROM __SCHEMA__.wh_event_store s
    WHERE s.event_id = pe.event_id
      AND s.stream_id = pe.stream_id
    LIMIT 1
  ) es
  -- Migration 072: COALESCE the offloaded ephemeral body back in. Sourced events have a non-NULL
  -- inline body so the join contributes nothing; ephemeral events read their body from wh_event_body.
  -- 536: by its key, one probe per work row.
  LEFT JOIN LATERAL (
    SELECT b.event_data, b.metadata
    FROM __SCHEMA__.wh_event_body b
    WHERE b.event_id = es.event_id
    LIMIT 1
  ) eb ON TRUE
  -- 536: the batch's rows by stream, the lease a filter on them (IS TRUE: as an index condition on the instance's lease
  -- it read every row the instance holds, which during a fan-out is the whole backlog).
  WHERE (pe.instance_id = p_instance_id AND pe.lease_expiry > p_now) IS TRUE
    AND pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
    AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
  -- 575 (#2551): an unstamped event is read in stream order, its version, never by id.
  ORDER BY pe.stream_id, es.commit_sequence ASC NULLS LAST, es.version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events(UUID, UUID[], TIMESTAMPTZ, INTEGER) IS
  'Atomic claim+fetch of pending perspective work for the given streams (slice 25): claims unleased '
  'or lapsed rows for the instance (bumping attempts), gates unstamped rows behind a grace window '
  '(058) and streams owned by another live instance (059), then returns the rows leased to the '
  'instance with their event bodies, commit sequence, attempts and failures (139), and whether each '
  'event is the first in its stream (264). 536: the event, its body and the stream''s ledger row are each read by one '
  'probe per work row, so a call reads what its batch holds and never the whole event store or ledger. 566 (#2554, #2555): with the service''s switch on (wh_partition_ownership_by_map), decided through wh_i_own alone.';

-- ---------------------------------------------------------------------------------------------
-- wh_collective_sink_queue: copied from its last word (175_CollectiveSinkQueue.sql), ordered by stream version, never by id (575).
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/messaging/collective-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueue_TwoCollectivesSharingAKey_AreInCommitOrder_WhenIdsRunBackwardAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueue_LeavesOutProcessedRowsOtherPerspectivesAndOtherStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueue_UnstampedRowsComeLastInEventIdOrderAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StreamOrderNeverByIdGuardTests.cs:NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_collective_sink_queue(
  p_stream_id UUID
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID,
  out_commit_sequence BIGINT
) AS $$
  SELECT pe.event_work_id, pe.event_id, es.commit_sequence
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es
    ON es.stream_id = pe.stream_id
    AND es.event_id = pe.event_id
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = '__collective__'
    AND pe.processed_at IS NULL
  -- 575 (#2551): an unstamped event is read in stream order, its version, never by id.
  ORDER BY es.commit_sequence ASC NULLS LAST, es.version;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_collective_sink_queue(UUID) IS
  'The collective sink queue of one stream (#963): its unprocessed __collective__ work rows with their events, in '
  'commit_sequence order (unstamped last, event_id breaking ties), the order the stream''s collectives apply and the '
  'order a replay reads the stream in. A read, not a claim.';

-- ---------------------------------------------------------------------------------------------
-- fetch_events_by_ids: copied from its last word (078_DropInlineBodyColumns.sql), ordered by stream version, never by id (575).
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/event-store/version-ordering#reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StreamOrderNeverByIdGuardTests.cs:NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_events_by_ids(
  p_event_ids UUID[]
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    es.stream_id,
    es.event_id,
    es.event_type::TEXT,
    eb.event_data::TEXT,
    eb.metadata::TEXT,
    es.scope::TEXT
  FROM __SCHEMA__.wh_event_store es
  -- Migration 072: COALESCE the offloaded ephemeral body (see get_stream_events above).
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
  WHERE es.event_id = ANY(p_event_ids)
  -- 575 (#2551): each stream's events in stream order, never by id.
  ORDER BY es.stream_id, es.version;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- fetch_pending_perspective_events: copied from its last word (042_FetchPendingPerspectiveEvents.sql), ordered by stream version, never by id (575).
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/event-store/version-ordering#reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StreamOrderNeverByIdGuardTests.cs:NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_pending_perspective_events(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_instance_id UUID
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID,
  -- INNER JOIN wh_event_store + commit_sequence IS NOT NULL gate: rows whose post-commit
  -- stamper hasn't caught up are invisible to the drainer until they're stamped. Closes
  -- the stamper-lag race at the SQL source, downstream filters can rely on every row
  -- coming through here having an authoritative commit_sequence.
  out_commit_sequence BIGINT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    pe.event_work_id,
    pe.event_id,
    es.commit_sequence
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = p_perspective_name
    AND pe.instance_id = p_instance_id
    AND pe.processed_at IS NULL
    AND es.commit_sequence IS NOT NULL
  -- 575 (#2551): the stream's events in stream order, never by id.
  ORDER BY es.version;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- claim_and_fetch_pending_perspective_events: copied from its last word (042_FetchPendingPerspectiveEvents.sql), ordered by stream version, never by id (575).
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/event-store/version-ordering#reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StreamOrderNeverByIdGuardTests.cs:NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_and_fetch_pending_perspective_events(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID,
  -- INNER JOIN wh_event_store + commit_sequence IS NOT NULL gate on both the claim CTE
  -- and the return-fetch: rows whose post-commit stamper hasn't caught up are neither
  -- leased nor returned. Closes the stamper-lag race at the SQL source, downstream
  -- filters can rely on every row coming through here having an authoritative
  -- commit_sequence. Unstamped rows stay unowned until a later cycle (post-stamping).
  out_commit_sequence BIGINT
) AS $$
BEGIN
  -- Step 1: atomic claim. Lease every eligible row (orphaned, expired, or already
  -- ours) to this instance. Same instance gets lease-extension; new claims bump
  -- attempts to match the claim_orphaned_perspective_events convention.
  --
  -- Deadlock prevention (slice 25 hotfix): inner CTE acquires row locks in
  -- deterministic event_work_id order via FOR UPDATE SKIP LOCKED. Concurrent
  -- callers can't lock rows in conflicting orders, same-row contention turns
  -- into "the loser skips this row this cycle" instead of a 40P01 deadlock.
  -- Slice 28: only UPDATE rows that actually NEED claiming, unowned or with expired
  -- lease. Rows already leased to us are returned by step 2's SELECT without re-extending
  -- the lease here; LeaseRenewalWorker handles in-flight renewals separately. Skipping the
  -- redundant own-row UPDATEs eliminates ~7x WAL write volume on wh_perspective_events
  -- (pg_stat_user_tables in a production run showed a large multiple of UPDATEs versus inserts).
  WITH eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
    WHERE pe.stream_id = p_stream_id
      AND pe.perspective_name = p_perspective_name
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      )
      AND es.commit_sequence IS NOT NULL
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = p_lease_expiry,
      attempts = pe.attempts + 1
  FROM eligible e
  WHERE pe.event_work_id = e.event_work_id;

  -- Step 2: return all rows now claimed by us, in event_id ASC order.
  -- Within the same transaction so we see our own UPDATE writes.
  -- Same stamper-lag gate as the claim CTE, defense in depth in case a row was
  -- already leased to us before its commit_sequence got stamped.
  RETURN QUERY
  SELECT
    pe.event_work_id,
    pe.event_id,
    es.commit_sequence
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = p_perspective_name
    AND pe.instance_id = p_instance_id
    AND pe.processed_at IS NULL
    AND es.commit_sequence IS NOT NULL
  -- 575 (#2551): the stream's events in stream order, never by id.
  ORDER BY es.version;
END;
$$ LANGUAGE plpgsql;
