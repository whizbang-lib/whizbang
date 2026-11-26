-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 536_PerspectiveWorkQueueCallsArePricedByTheBatch.sql
-- Date: 2026-11-20
-- Description: The perspective work-queue calls cost what their batch holds, never what the queue or the history
--              holds (#2286).
--
--              A fan-out to 100,000 new streams slowed from about 2.6 s to 11.9 s per 1,000 streams claimed as the
--              backlog grew. Measured per call (index entries and table rows read, inside the call's own transaction)
--              over 10,000 and 40,000 pending perspective rows with a batch of 1,000 streams, three statements read
--              the whole queue, every call:
--
--              1. get_stream_events joined the batch's work rows to the event store, and the event store to the body
--                 table, as joins the planner was free to plan. Planned for a batch of 1,000 streams it hashed the
--                 work rows and scanned the whole event store against them: 10,000 rows read at 10,000 pending,
--                 40,000 at 40,000. The live-owner gate was a NOT EXISTS the planner may turn into an anti join over
--                 the whole stream ledger the same way.
--              2. complete_perspective rang the owners through notify_instance_owners_with_payload, whose search for
--                 the streams no instance owns was a NOT EXISTS over the stream ledger. Planned for a flush of 1,000
--                 streams it hashed the whole ledger: 40,000 rows read at 40,000 pending.
--              3. cleanup_completed_streams evicted a stream from the ledger only if no work table held pending work
--                 for it, three NOT EXISTS probes. Planned for 1,000 streams, the perspective probe became an anti
--                 join that hashed every pending perspective row: 39,000 rows read at 40,000 pending.
--              4. A fan-out that outlasts the lease its rows were stored under (five minutes) leaves them orphaned, and
--                 the claim acquires them again through claim_orphaned_perspective_events. Its window's two ledger
--                 EXISTS were planned as one hashed pass over the whole ledger, its earlier-event NOT EXISTS as an anti
--                 join over every pending row, and its lease UPDATE ... FROM the locked rows as a pass over the queue;
--                 wh_lease_claimed_streams, the claim's one ledger write, joined the claimed streams to the whole
--                 ledger twice.
--              5. complete_perspective settles the rewind flag of the pairs it retired (_wh_settle_rewind_flags), and
--                 the flag test (status & 32) has no index, so it was planned as a pass over every cursor, a table that
--                 grows with every stream ever applied: 41,000 rows read per flush after 41,000 streams.
--
--              Each was a plan the planner chose for the arguments it was given: a batch that is a few percent of
--              the queue makes a full scan look as cheap as a probe per key. No statistics fix that, and the plan a
--              cached statement settles on can differ from the first one. Each now reaches the other table one key
--              at a time through a LATERAL subquery with LIMIT 1, which the planner cannot flatten into a join and
--              can only run as an index probe per row of the batch (the same construction claim_work's stream walks
--              use), a scalar subquery with LIMIT 1 (never turned into a join or hashed), or the batch as an index
--              condition (= ANY(ARRAY(...)), estimated at a handful of keys whatever the batch). The results are
--              unchanged:
--
--                * get_stream_events reads each work row's event by its primary key, and its body by its key, judges
--                  the live-owner gate by one probe of the stream's ledger row, and tests the lease as a filter on the
--                  batch's rows rather than as a range over a lease index (which read every row the instance held);
--                * notify_instance_owners_with_payload finds a stream no instance owns by one probe of its ledger row;
--                * cleanup_completed_streams probes each work table once per candidate stream, stopping at the first
--                  pending row;
--                * claim_orphaned_perspective_events reads each window row's ledger row once (stream_id is its key, so
--                  the owner path and the live-owner test read the same row), probes for an earlier pending event per
--                  row, and locks, leases and pins by key, in the same orders as before;
--                * wh_lease_claimed_streams locks, refreshes and takes over the claimed streams' rows by key, in
--                  stream_id order as before;
--                * _wh_settle_rewind_flags reaches each pair's cursor by its key, locked in (stream, perspective)
--                  order as update_perspective_cursors locks them, and settles it by its row address.
--
--              Rows read for a batch of 1,000 streams over 10,000 and then 40,000 pending and applied streams with
--              lapsed leases, before and after: acquisition 61,011 and 212,011, now 21,001 and 22,001 (its window is
--              eight rows per stream by design); ledger write 20,005 and 41,005, now 1,000 at both; fetch 26,022 and
--              87,022, now 8,000 at both; completion 65,016 and 89,017, now 47,012 and 10,013 (its own work-row DELETE
--              and update_perspective_cursors, unchanged here, scan tables that small and switch to their keys as the
--              tables grow); eviction 10,004 and 39,004, now 2,001 at both.
--              PerspectiveWorkQueueBacklogCostSqlTests gates each call at a backlog and at four times it, planned both
--              for its arguments and generically.
--
-- Cost: every changed statement reads index entries in proportion to its batch and nothing in proportion to the queue.
--
-- Reversal: reversible (each function's previous last word is restored whole; results are the same, only the plans
--           the planner may choose come back)
--
-- Dependencies: 317 (get_stream_events and claim_orphaned_perspective_events, copied with the delta above), 369
--               (notify_instance_owners_with_payload), 162 (cleanup_completed_streams), 374 (_wh_settle_rewind_flags),
--               202 (wh_lease_claimed_streams), each copied with the delta above
-- Objects: get_stream_events, notify_instance_owners_with_payload, cleanup_completed_streams, _wh_settle_rewind_flags,
--          claim_orphaned_perspective_events, wh_lease_claimed_streams
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_OUTBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- get_stream_events: last word 317, the event, body and owner reached one probe per work row.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

-- <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FirstEventInStreamReadTests.cs:GetStreamEvents_ReturnsWhetherEachEventIsTheFirstInItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCommitSequencePropagationSqlTests.cs:GetStreamEvents_StampedReturnedUnstampedExcludedOnSameStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveFailureCounterSqlTests.cs:GetStreamEvents_SurfacesFailures_ForTheDeadLetterDecisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCrossPodOwnershipTests.cs:LiveOwnersExpiredLeaseRow_NotStolenByAnotherInstance</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:GetStreamEvents_ReadsWhatItsBatchHolds_NotTheBacklogAsync</tests>
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
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stamp_cutoff := p_now - v_stamp_grace;

  -- Atomic claim+fetch (slice 25) + grace-windowed unstamped gate (mig 058) + live-owner gate
  -- (mig 059). A row is claimable only if its stream is NOT owned by a DIFFERENT live instance:
  -- enforcing single-writer-per-stream so two pods never apply one stream concurrently.
  -- 536: the event and the ledger row are each one probe per work row (LATERAL ... LIMIT 1 cannot be flattened into a
  -- join, so no plan can scan either table whole against the batch).
  WITH eligible AS (
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
      AND owner.held IS NULL
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
  ORDER BY pe.stream_id, es.commit_sequence ASC NULLS LAST, es.event_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events(UUID, UUID[], TIMESTAMPTZ, INTEGER) IS
  'Atomic claim+fetch of pending perspective work for the given streams (slice 25): claims unleased '
  'or lapsed rows for the instance (bumping attempts), gates unstamped rows behind a grace window '
  '(058) and streams owned by another live instance (059), then returns the rows leased to the '
  'instance with their event bodies, commit sequence, attempts and failures (139), and whether each '
  'event is the first in its stream (264). 536: the event, its body and the stream''s ledger row are each read by one '
  'probe per work row, so a call reads what its batch holds and never the whole event store or ledger.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- notify_instance_owners_with_payload: last word 369, copied with the 536 delta: an unclaimed stream is found by one
-- probe of its ledger row.
SELECT __SCHEMA__.drop_all_overloads('notify_instance_owners_with_payload');

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ManyOwners_JudgesLivenessOnceNotPerOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ALiveOwnerDraining_IsSuppressed_ButADeadOneIsRungAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/NotifyKindAndPayloadSqlTests.cs:NotifyInstanceOwners_KindAndPayloadAreIndependent_PayloadReachesTheWire_KindKeysTheStateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:NotifyOwners_TargetsOnlyInstancesTheRuleCallsLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CompletePerspective_ReadsWhatItsBatchHolds_NotTheBacklogAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.notify_instance_owners_with_payload(
  p_kind TEXT,
  p_payload TEXT,
  p_stream_ids UUID[]
) RETURNS VOID AS $$
DECLARE
  v_unclaimed_streams UUID[];
  v_active_count INTEGER;
  v_debounce INTEGER;
  -- 369: the live instances in instance id order, read once; element n + 1 is the instance of rank n.
  v_live UUID[];
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
     WHERE setting_key = 'notify_debounce_seconds'), 7)
  INTO v_debounce;

  -- 369: one read of the liveness rule (317) for the whole call. The rule reads pg_locks, whose cost grows with the
  -- server's connection limit; judged per target, it was planned and run again for every owner rung.
  SELECT COALESCE(array_agg(l.instance_id ORDER BY l.instance_id), ARRAY[]::UUID[])
  INTO v_live
  FROM __SCHEMA__.wh_live_instance_ids() l;

  -- Step 1 (unchanged targeting): per-owner notify for streams in wh_active_streams.
  PERFORM __SCHEMA__._wh_notify_debounced_for(a.assigned_instance_id, p_kind, p_payload, v_debounce,
                                             a.assigned_instance_id = ANY(v_live))
  FROM (
    SELECT DISTINCT assigned_instance_id
    FROM __SCHEMA__.wh_active_streams
    WHERE stream_id = ANY(p_stream_ids)
      AND assigned_instance_id IS NOT NULL
  ) a;

  -- Step 2 (unchanged targeting): deterministic-target notify for unclaimed streams.
  -- 536: one probe of each stream's ledger row (a NOT EXISTS here was planned as an anti join that hashed the whole
  -- ledger for a large batch).
  SELECT ARRAY_AGG(s) INTO v_unclaimed_streams
  FROM unnest(p_stream_ids) AS s
  LEFT JOIN LATERAL (
    SELECT 1 AS owned
    FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = s
      AND a.assigned_instance_id IS NOT NULL
    LIMIT 1
  ) c ON TRUE
  WHERE c.owned IS NULL;

  IF v_unclaimed_streams IS NULL OR cardinality(v_unclaimed_streams) = 0 THEN
    RETURN;
  END IF;

  v_active_count := cardinality(v_live);

  IF v_active_count = 0 THEN
    RETURN;
  END IF;

  -- The partition number is COMPUTED, never read from a queue table (see 317 and its predecessors): the stream
  -- ledger's number first, compute_partition only for a stream the ledger has never held, so the doorbell wakes the
  -- instance the claim itself routes the stream to. A doorbell kind other than these three (schedule) and a signal's
  -- wire name reach no branch, as before. Every target here is live by construction (it is drawn from v_live).
  IF p_kind IN (__CATEGORY_OUTBOX__, __CATEGORY_INBOX__, __CATEGORY_PERSPECTIVE__) THEN
    PERFORM __SCHEMA__._wh_notify_debounced_for(targets.target_instance_id, p_kind, p_payload, v_debounce, true)
    FROM (
      SELECT DISTINCT v_live[1 + (
        COALESCE(
          (SELECT ast.partition_number
           FROM __SCHEMA__.wh_active_streams ast
           WHERE ast.stream_id = s),
          __SCHEMA__.compute_partition(s)
        ) % v_active_count)] AS target_instance_id
      FROM unnest(v_unclaimed_streams) AS s
    ) AS targets
    -- A number outside 0..count-1 (only a negative stored partition) matched no rank before and rings no one now.
    WHERE targets.target_instance_id IS NOT NULL;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_instance_owners_with_payload(TEXT, TEXT, UUID[]) IS
'Instance-routed NOTIFY emission with doorbell debounce (slice 27 + v0.685 + 130, kind split from payload in 141). 369: the live instances are read once per call, and each target''s liveness and the deterministic rank come from that read (the rule reads pg_locks, so judging it per target cost a plan and a pg_locks read per owner). Per-owner for pinned streams; rank-deterministic for unclaimed outbox/inbox/perspective streams, where the partition number is the stream ledger''s, else computed through compute_partition, never read out of a queue table. Every emission goes through _wh_notify_debounced_for(instance, p_kind, p_payload, window, live). Signals call this form with their wire name as both, and reach only the per-owner path. 536: an unclaimed stream is found by one probe of its ledger row, never by a pass over the ledger.';

-- ---------------------------------------------------------------------------------------------
-- cleanup_completed_streams: last word 162, each work table probed once per stream.
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

-- ---------------------------------------------------------------------------------------------
-- _wh_settle_rewind_flags: last word 374, each pair's cursor reached by its key.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_settle_rewind_flags');

-- The flagged cursors among the given pairs that have no pending work left are settled: status 2, as
-- update_perspective_cursors leaves a pair whose work is complete. Returns how many were settled.
-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_TheCursorFlushedBeforeItsWork_ClearsTheRewindFlagWhenTheWorkIsRetiredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_RetiringOneOfTwoPendingRows_KeepsTheRewindFlagAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_AFlaggedCursorOfAnotherPerspective_IsLeftAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:CompletePerspective_ReadsWhatItsBatchHolds_NotTheBacklogAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_settle_rewind_flags(
  p_stream_ids UUID[],
  p_perspective_names TEXT[]
) RETURNS INTEGER AS $$
DECLARE
  v_settled INTEGER;
BEGIN
  IF p_stream_ids IS NULL OR cardinality(p_stream_ids) = 0 THEN
    RETURN 0;
  END IF;

  -- 536 (#2286): each pair's cursor is one probe of the cursor key, locked in (stream, perspective) order, the order
  -- update_perspective_cursors locks cursors in, and its pending work one probe that stops at the first row. Joined
  -- to the pairs, the flag test (status & 32, which no index answers) was planned as a pass over every cursor the
  -- table holds, which grows with every stream ever applied, on every completion flush.
  WITH flagged AS (
    SELECT c.cursor_row
    FROM (
      SELECT DISTINCT u.sid, u.pname
      FROM unnest(p_stream_ids, p_perspective_names) AS u(sid, pname)
      ORDER BY u.sid, u.pname
    ) p
    CROSS JOIN LATERAL (
      SELECT pc.ctid AS cursor_row
      FROM __SCHEMA__.wh_perspective_cursors pc
      WHERE pc.stream_id = p.sid
        AND pc.perspective_name = p.pname
        AND (pc.status & 32) = 32
      FOR UPDATE
    ) c
    WHERE (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = p.sid
        AND pe.perspective_name = p.pname
        AND pe.processed_at IS NULL
      LIMIT 1
    ) IS NULL
  )
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = 2
  WHERE pc.ctid = ANY(ARRAY(SELECT f.cursor_row FROM flagged f));
  GET DIAGNOSTICS v_settled = ROW_COUNT;
  RETURN v_settled;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_settle_rewind_flags(UUID[], TEXT[]) IS
'374: settles the RewindRequired cursors among the given (stream, perspective) pairs that have no pending work left '
'(status 2, as update_perspective_cursors leaves a completed pair). Returns how many were settled. 536: each pair''s '
'cursor and pending work are read by key, so a flush reads what its pairs hold, never every cursor.';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_perspective_events: last word 317, the ledger and the earlier-event probes one key at a time.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_perspective_events');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:ClaimOrphanedPerspectiveEvents_LeasesTheStream_WithTheRowLeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedPerspectiveEvents_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedPerspectiveEvents_AStaleOwnerRowTheRuleCallsDead_LeasesReclaimableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedPerspectiveEvents_DeadInstanceRow_WithOnlyAConnectionNamedForIt_LeasesReclaimableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:AcquireOrphanedPerspectiveWork_ReadsWhatItLeases_NotTheBacklogAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_perspective_events(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_max_streams INTEGER DEFAULT 500,
  p_instance_rank INTEGER DEFAULT 0,
  p_active_instance_count INTEGER DEFAULT 1,
  -- 160: rows this call may lease, as p_max_streams bounds the streams it may take. Without it the
  -- claim selected a batch of STREAMS and then leased every pending event of each, so a poll cost
  -- what a consumer's streams happened to hold: 3,730 blocks a call over streams twelve events
  -- deep and 54,914 over streams two hundred deep, for the same batch. NULL keeps the old
  -- unbounded behavior for a caller that has not been taught to pass one; claim_work passes its own
  -- row budget. 145 (#714) is the warning against the other mistake, handing a STREAM count to an
  -- acquisition as a row cap and turning a fat stream into one row per cycle -- hence a multiple of
  -- the batch rather than the batch itself.
  p_max_rows INTEGER DEFAULT NULL,
  -- 202 (#1256): TRUE leaves the stream ledger to the caller. claim_work passes it so its acquisitions
  -- write no ledger row of their own; it writes every claimed stream's row once, in one stream_id pass,
  -- after the last acquisition (wh_lease_claimed_streams). A direct caller keeps the default and gets
  -- the same one-pass write here, inside this statement.
  p_ledger_deferred BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  -- 202: the partition the ledger row of a new stream is recorded under, for a caller that defers the ledger.
  partition_number INTEGER
) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  -- 157: the window this poll chooses streams from, bounded by the batch. The most urgent
  -- claimable-looking events in (priority, event_id) order, walked from idx_perspective_event_urgency
  -- with an early stop, so the cost of choosing streams follows the batch and never the backlog.
  -- Aggregating every claimable event per poll, on every instance, was most of a saturated database's
  -- CPU under a bulk load. A stream whose head lies beyond the window is by definition less urgent
  -- than every stream selected from it, and a later poll sees it. Ownership and per-stream ordering
  -- are decided on the window below, and a selected stream is still captured in full.
  WITH head AS (
    SELECT
      pe.event_work_id,
      pe.stream_id,
      pe.perspective_name,
      pe.event_id,
      pe.partition_number,
      pe.priority
    FROM __SCHEMA__.wh_perspective_events pe
    WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND pe.processed_at IS NULL
    ORDER BY pe.priority, pe.event_id
    LIMIT GREATEST(p_max_streams, 1) * 8
  ),
  claimable_events AS (
    -- The events of the window this instance may claim (orphaned or unleased, owned or unowned).
    SELECT
      pe.event_work_id,
      pe.stream_id,
      pe.perspective_name,
      pe.event_id,
      pe.partition_number,
      pe.priority
    FROM head pe
    -- 536 (#2286): the stream's ledger row, one probe by its key (stream_id is the ledger's primary key, so this is
    -- the row both paths below read). Two EXISTS over the ledger were planned as a hashed pass over the whole ledger
    -- once a poll's window was large, so every claim of orphaned work read every stream the ledger held.
    LEFT JOIN LATERAL (
      SELECT ast.assigned_instance_id
      FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = pe.stream_id
      LIMIT 1
    ) owner ON TRUE
    WHERE TRUE
      -- Phase H step 6 slice 2: stream ownership now combines wh_active_streams pinning
      -- (OWNER PATH: always wins) with partition-modulo load balancing for unowned streams
      -- (UNOWNED PATH: symmetric with claim_orphaned_outbox / _inbox).
      AND (
        -- OWNER PATH: wh_active_streams says this instance owns the stream. Always claim.
        owner.assigned_instance_id IS NOT DISTINCT FROM p_instance_id
        OR
        -- UNOWNED / ABANDONED PATH: partition-modulo selection for streams with no live owner.
        (
          (pe.partition_number % p_active_instance_count) = p_instance_rank
          AND NOT COALESCE(
            owner.assigned_instance_id != p_instance_id
            -- 317 (#2027): the owner counts while the one liveness rule calls it live.
            AND owner.assigned_instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l),
            FALSE)
        )
      )
      -- Ensure ordering - no earlier uncompleted events in same perspective
      -- 536: a scalar probe per row of the window (a NOT EXISTS here was planned as an anti join over every pending
      -- row of the queue).
      AND (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
        WHERE earlier.stream_id = pe.stream_id
          AND earlier.perspective_name = pe.perspective_name
          AND earlier.event_id < pe.event_id
          AND (
            (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
            OR (earlier.scheduled_for > p_now)
          )
        LIMIT 1
      ) IS NULL
  ),
  -- Select up to p_max_streams distinct streams from claimable events
  -- 150: the most urgent streams first (the fold is the stream's most urgent claimable event), then the oldest.
  selected_streams AS (
    SELECT ce.stream_id
    FROM claimable_events ce
    GROUP BY ce.stream_id
    ORDER BY MIN(ce.priority), MIN(ce.event_id::TEXT)   -- no min(uuid); a v7 id orders chronologically as text
    LIMIT p_max_streams
  ),
  -- 140: lock the selected streams' rows with SKIP LOCKED before leasing them (issue #699), the
  -- shape claim_orphaned_inbox and claim_orphaned_outbox already use. A row another session holds
  -- is a row being completed or leased; waiting for it stalled the whole claim tick behind one
  -- commit batch. The lock is scoped to the selected streams, never the full eligible backlog.
  -- 157: full-stream capture reads the selected streams' rows through idx_perspective_event_order,
  -- not the window, so a stream still drains in one lease as before; the same ordering predicate
  -- keeps an event behind a leased or scheduled earlier one out of the claim.
  -- 160: the row bound has to be reached BEFORE the per-row work, not after it. Bounding only the
  -- final result still sorted every claimable row of every selected stream and ran the ordering
  -- probe below on each one, so leasing fell to a handful of rows a call while the poll still cost
  -- what the streams held: 8,352 blocks a call at depth against 1,523 shallow, for 6.7 rows leased.
  -- Each stream is walked separately instead, oldest first through idx_perspective_event_order,
  -- and stops at the bound on its own. Work is then bounded by the batch times the bound rather
  -- than by the depth of whatever streams the batch happened to select, and a deep stream can still
  -- contribute the whole bound rather than being rationed to one row a cycle (145, #714).
  candidate_events AS (
    SELECT c.event_work_id, c.event_id
    FROM selected_streams ss
    CROSS JOIN LATERAL (
      SELECT pe.event_work_id, pe.event_id
      FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = ss.stream_id
        AND (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
        AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
        AND pe.processed_at IS NULL
        -- 536: the ordering probe as a scalar subquery, one index probe per row, as in the window above.
        AND (
          SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
          WHERE earlier.stream_id = pe.stream_id
            AND earlier.perspective_name = pe.perspective_name
            AND earlier.event_id < pe.event_id
            AND (
              (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
              OR (earlier.scheduled_for > p_now)
            )
          LIMIT 1
        ) IS NULL
      ORDER BY pe.event_id
      LIMIT COALESCE(p_max_rows, 2147483647)
    ) c
  ),
  locked AS (
    -- Oldest first across the selected streams, and stop at the row bound. event_id is a v7 id, so
    -- this is arrival order, and per-stream order falls out of it: an event is only reached after
    -- every earlier event of its own stream, so a stream is captured from its head and never with a
    -- hole. A stream deeper than the bound is captured over consecutive polls, which is what the
    -- inbox has done since 145; the drain is per stream with an unbounded channel and the re-offer
    -- hands the stream back each poll, so the remainder is picked up rather than stranded.
    -- Full-stream capture in one lease was never a correctness property, only the previous cost of
    -- not bounding this.
    -- 536: the candidates' rows by their key (the candidates as an index condition, never a join the planner may
    -- turn into a pass over the queue); a candidate's event_id is its row's.
    SELECT pe.event_work_id
    FROM __SCHEMA__.wh_perspective_events pe
    WHERE pe.event_work_id = ANY(ARRAY(SELECT ce.event_work_id FROM candidate_events ce))
      AND (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND pe.processed_at IS NULL
    ORDER BY pe.event_id
    LIMIT COALESCE(p_max_rows, 2147483647)
    FOR UPDATE OF pe SKIP LOCKED
  ),
  -- Claim the locked events for the selected streams, oldest first, up to the row bound (160).
  claimed AS (
    UPDATE __SCHEMA__.wh_perspective_events pe
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = pe.attempts + 1
    -- 536: by key, the locked rows as an index condition (an UPDATE ... FROM was planned as a pass over the queue).
    WHERE pe.event_work_id = ANY(ARRAY(SELECT l.event_work_id FROM locked l))
    RETURNING pe.event_work_id AS c_event_work_id, pe.stream_id AS c_stream_id, pe.perspective_name AS c_perspective_name, pe.partition_number AS c_partition_number
  ),
  -- The stream ledger: one row per stream this call leased rows on. 202 (#1256): written in migration 200's
  -- one-pass shape (#1238), which this function lacked as the inbox did. Its refresh was a bare UPDATE ... FROM claimed, locking
  -- in whatever order the join produced, and the pin ran after it, so the statement took its ledger rows in two
  -- runs, the first not even sorted. Two claims that each refresh a stream the other pins then wait on each
  -- other, which a claim that sees no live peer (every heartbeat stale, no whizbang-<id> application name)
  -- reaches by taking over its peers' streams.
  --
  -- Every existing ledger row of the claimed streams is locked here first, whoever owns it, in stream_id order
  -- (172's rule 2). The refresh and the takeover then update rows this statement already holds and cannot wait,
  -- and streams with no row are inserted after the pass, in the same order. When the caller defers the ledger
  -- (claim_work), none of this runs and the caller writes it once for all its acquisitions.
  ledger_locked AS (
    SELECT ast.stream_id AS locked_stream_id,
           ast.assigned_instance_id AS locked_owner,
           ast.lease_expiry AS locked_lease_expiry
    -- 536: the claimed streams as an index condition on the ledger's key, never a join over the ledger.
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = ANY(ARRAY(
      SELECT DISTINCT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL AND NOT p_ledger_deferred))
    ORDER BY ast.stream_id
    FOR UPDATE OF ast
  ),
  -- REFRESH: the streams this instance owns under a live lease, judged on the row as locked. Same write as
  -- before: last_activity_at and the lease move, the owner does not.
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    -- 536: by key, as the lock pass found them.
    WHERE ast.stream_id = ANY(ARRAY(
      SELECT ll.locked_stream_id FROM ledger_locked ll
      WHERE ll.locked_owner = p_instance_id
        AND ll.locked_lease_expiry > p_now))
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  -- TAKEOVER: every other locked row, under the ownership rules the pin's ON CONFLICT branch applied (148),
  -- unchanged. Whoever the CASE leaves as owner holds the lease.
  taken_over AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        assigned_instance_id = CASE
          WHEN ast.assigned_instance_id IS NULL THEN p_instance_id
          WHEN NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_service_instances si
            WHERE si.instance_id = ast.assigned_instance_id
          ) THEN p_instance_id
          ELSE ast.assigned_instance_id
        END,
        lease_expiry = CASE
          WHEN ast.assigned_instance_id IS NULL THEN p_lease_expiry
          WHEN ast.assigned_instance_id = p_instance_id THEN p_lease_expiry
          WHEN NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_service_instances si
            WHERE si.instance_id = ast.assigned_instance_id
          ) THEN p_lease_expiry
          ELSE ast.lease_expiry
        END
    -- 536: by key, as the lock pass found them.
    WHERE ast.stream_id = ANY(ARRAY(
      SELECT ll.locked_stream_id FROM ledger_locked ll
      -- Exactly the rows the refresh does not take. COALESCEd because a NULL owner or lease makes the
      -- refresh's test NULL, and NOT NULL would leave the row to neither path.
      WHERE NOT COALESCE(ll.locked_owner = p_instance_id AND ll.locked_lease_expiry > p_now, FALSE)))
    RETURNING ast.stream_id AS taken_stream_id
  ),
  -- PIN: the streams with no ledger row, inserted after the lock pass and in stream_id order, ON CONFLICT DO
  -- NOTHING. A row another session created since this statement's snapshot is left to it rather than locked
  -- here, out of order.
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, COALESCE(c.c_partition_number, 0) AS partition_number
      FROM claimed c
      WHERE c.c_stream_id IS NOT NULL
        AND NOT p_ledger_deferred
        AND NOT EXISTS (
          SELECT 1 FROM ledger_locked ll WHERE ll.locked_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO NOTHING
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_event_work_id AS event_work_id, c.c_stream_id AS stream_id, c.c_perspective_name AS perspective_name,
         c.c_partition_number AS partition_number
  FROM claimed c;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_perspective_events(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER, INTEGER, BOOLEAN) IS
  'Acquires unowned/abandoned pending perspective events for an instance, by stream (027 ownership, 140 per-stream gate, 148 stream leases). 150: the streams with the most urgent claimable event are selected first, oldest first within a priority. 160: bounded by ROWS as well as by streams -- it used to take a batch of streams and lease every pending event of each, so one call cost whatever a consumer''s streams happened to hold; a stream deeper than the bound is now captured from its head over consecutive polls. 202: the stream ledger is '
  'written in one stream_id pass (lock every existing row first, then refresh, take over, and insert new streams), and '
  'p_ledger_deferred leaves it to the caller (claim_work), each row carrying its partition number for that write. '
  '536 (#2286): the window''s ownership and ordering tests are one probe per window row, and the lock, the lease and the '
  'ledger pass reach their rows by key, so an acquisition reads in proportion to what it leases, never the queue or '
  'the ledger.';

-- ---------------------------------------------------------------------------------------------
-- wh_lease_claimed_streams: last word 202, the claimed streams' ledger rows reached by key.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_lease_claimed_streams');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_LedgerRowsOfEveryAcquisition_AreTakenInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_AStreamLeasedByTwoAcquisitions_IsWrittenOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentOutboxAndInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWorkQueueBacklogCostSqlTests.cs:LeaseClaimedStreams_ReadsWhatItsBatchHolds_NotTheLedgerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_lease_claimed_streams(
  p_instance_id UUID,
  p_now TIMESTAMPTZ,
  p_lease_expiry TIMESTAMPTZ,
  -- The streams the claim leased work on, and each one's partition number, in step. A stream may appear more
  -- than once (two acquisitions, or several rows); a NULL stream id has no ledger row and is skipped.
  p_stream_ids UUID[],
  p_partition_numbers INTEGER[]
) RETURNS INTEGER AS $$
#variable_conflict use_column
DECLARE
  v_streams INTEGER;
BEGIN
  WITH claimed AS (
    SELECT DISTINCT ON (u.u_stream_id) u.u_stream_id AS c_stream_id, u.u_partition_number AS c_partition_number
    FROM unnest(p_stream_ids, p_partition_numbers) AS u(u_stream_id, u_partition_number)
    WHERE u.u_stream_id IS NOT NULL
    ORDER BY u.u_stream_id
  ),
  -- The one pass, the shape 200 gave the outbox acquisition (#1238): every existing ledger row of the claimed
  -- streams locked first, whoever owns it, in stream_id order (172's rule 2); then the refresh and the takeover of
  -- rows already held, which cannot wait; then the streams with no row, inserted in the same order.
  ledger_locked AS (
    SELECT ast.stream_id AS locked_stream_id,
           ast.assigned_instance_id AS locked_owner,
           ast.lease_expiry AS locked_lease_expiry
    -- 536 (#2286): the claimed streams as an index condition on the ledger's key. A join was planned as a hash over
    -- the whole ledger once a claim leased a large batch, so every such claim read every stream the ledger held.
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = ANY(ARRAY(SELECT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL))
    ORDER BY ast.stream_id
    FOR UPDATE OF ast
  ),
  -- REFRESH: the streams this instance owns under a live lease, judged on the row as locked. Same write as
  -- before: last_activity_at and the lease move, the owner does not.
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    -- 536: by key, as the lock pass found them.
    WHERE ast.stream_id = ANY(ARRAY(
      SELECT ll.locked_stream_id FROM ledger_locked ll
      WHERE ll.locked_owner = p_instance_id
        AND ll.locked_lease_expiry > p_now))
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  -- TAKEOVER: every other locked row, under the ownership rules the pin's ON CONFLICT branch applied (148),
  -- unchanged. Whoever the CASE leaves as owner holds the lease.
  taken_over AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        assigned_instance_id = CASE
          WHEN ast.assigned_instance_id IS NULL THEN p_instance_id
          WHEN NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_service_instances si
            WHERE si.instance_id = ast.assigned_instance_id
          ) THEN p_instance_id
          ELSE ast.assigned_instance_id
        END,
        lease_expiry = CASE
          WHEN ast.assigned_instance_id IS NULL THEN p_lease_expiry
          WHEN ast.assigned_instance_id = p_instance_id THEN p_lease_expiry
          WHEN NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_service_instances si
            WHERE si.instance_id = ast.assigned_instance_id
          ) THEN p_lease_expiry
          ELSE ast.lease_expiry
        END
    -- 536: by key, as the lock pass found them.
    WHERE ast.stream_id = ANY(ARRAY(
      SELECT ll.locked_stream_id FROM ledger_locked ll
      -- Exactly the rows the refresh does not take. COALESCEd because a NULL owner or lease makes the
      -- refresh's test NULL, and NOT NULL would leave the row to neither path.
      WHERE NOT COALESCE(ll.locked_owner = p_instance_id AND ll.locked_lease_expiry > p_now, FALSE)))
    RETURNING ast.stream_id AS taken_stream_id
  ),
  -- PIN: the streams with no ledger row, inserted after the lock pass and in stream_id order, ON CONFLICT DO
  -- NOTHING. A row another session created since this statement's snapshot is left to it rather than locked
  -- here, out of order.
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, COALESCE(c.c_partition_number, 0) AS partition_number
      FROM claimed c
      WHERE c.c_stream_id IS NOT NULL
        AND NOT EXISTS (
          SELECT 1 FROM ledger_locked ll WHERE ll.locked_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO NOTHING
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT count(*)::INTEGER INTO v_streams FROM claimed;
  RETURN v_streams;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_lease_claimed_streams(UUID, TIMESTAMPTZ, TIMESTAMPTZ, UUID[], INTEGER[]) IS
  'Writes the stream ledger (wh_active_streams) for every stream one claim leased work on, once, in one stream_id '
  'pass: every existing row locked first whoever owns it, then refreshed or taken over under the 148 ownership rules, '
  'then streams with no row inserted in the same order (ON CONFLICT DO NOTHING). Returns the number of distinct '
  'streams. 202 (#1256): claim_work calls it after its last acquisition, so one claim never takes its ledger rows in '
  'more than one run. 536 (#2286): the claimed streams'' rows are reached by key, never by a pass over the ledger.';
