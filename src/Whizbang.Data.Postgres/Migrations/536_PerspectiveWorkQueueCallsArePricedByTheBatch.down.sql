-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 536_PerspectiveWorkQueueCallsArePricedByTheBatch.sql (#2286): each function as its previous last word
-- defined it, copied whole (get_stream_events and claim_orphaned_perspective_events from 317,
-- notify_instance_owners_with_payload from 369, cleanup_completed_streams from 162, _wh_settle_rewind_flags from 374,
-- wh_lease_claimed_streams from 202). Their results are the same; only the plans they leave the planner free to
-- choose come back.

-- ---------------------------------------------------------------------------------------------
-- get_stream_events: 317's body, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

-- <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FirstEventInStreamReadTests.cs:GetStreamEvents_ReturnsWhetherEachEventIsTheFirstInItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCommitSequencePropagationSqlTests.cs:GetStreamEvents_StampedReturnedUnstampedExcludedOnSameStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveFailureCounterSqlTests.cs:GetStreamEvents_SurfacesFailures_ForTheDeadLetterDecisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCrossPodOwnershipTests.cs:LiveOwnersExpiredLeaseRow_NotStolenByAnotherInstance</tests>
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
  WITH eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN __SCHEMA__.wh_event_store es
      ON es.stream_id = pe.stream_id
      AND es.event_id = pe.event_id
    WHERE pe.stream_id = ANY(p_stream_ids)
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      )
      AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
      -- mig 059: single-writer gate. Do not claim a row whose stream is owned by a different
      -- LIVE instance (mirrors claim_orphaned's liveness: a wh_service_instances row exists, or a
      -- live LISTEN connection in pg_stat_activity). Caller-owned / unowned / dead-owner streams
      -- stay claimable.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_active_streams ast
        WHERE ast.stream_id = pe.stream_id
          AND ast.assigned_instance_id IS NOT NULL
          AND ast.assigned_instance_id <> p_instance_id
          -- 317 (#2027): the owner counts while the one liveness rule calls it live.
              AND ast.assigned_instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l)
      )
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = v_lease_expiry,
      attempts = pe.attempts + 1
  FROM eligible e
  WHERE pe.event_work_id = e.event_work_id;

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
  INNER JOIN __SCHEMA__.wh_event_store es
    ON pe.stream_id = es.stream_id
    AND pe.event_id = es.event_id
  -- Migration 072: COALESCE the offloaded ephemeral body back in. Sourced events have a non-NULL
  -- inline body so the join contributes nothing; ephemeral events read their body from wh_event_body.
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
  WHERE pe.instance_id = p_instance_id
    AND pe.lease_expiry > p_now
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
  'event is the first in its stream (264).';

-- ---------------------------------------------------------------------------------------------
-- notify_instance_owners_with_payload: 369's body, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('notify_instance_owners_with_payload');

-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ManyOwners_JudgesLivenessOnceNotPerOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:NotifyOwners_ALiveOwnerDraining_IsSuppressed_ButADeadOneIsRungAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/NotifyKindAndPayloadSqlTests.cs:NotifyInstanceOwners_KindAndPayloadAreIndependent_PayloadReachesTheWire_KindKeysTheStateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:NotifyOwners_TargetsOnlyInstancesTheRuleCallsLiveAsync</tests>
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
  SELECT ARRAY_AGG(s) INTO v_unclaimed_streams
  FROM unnest(p_stream_ids) AS s
  WHERE NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = s
      AND a.assigned_instance_id IS NOT NULL
  );

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
'Instance-routed NOTIFY emission with doorbell debounce (slice 27 + v0.685 + 130, kind split from payload in 141). 369: the live instances are read once per call, and each target''s liveness and the deterministic rank come from that read (the rule reads pg_locks, so judging it per target cost a plan and a pg_locks read per owner). Per-owner for pinned streams; rank-deterministic for unclaimed outbox/inbox/perspective streams, where the partition number is the stream ledger''s, else computed through compute_partition, never read out of a queue table. Every emission goes through _wh_notify_debounced_for(instance, p_kind, p_payload, window, live). Signals call this form with their wire name as both, and reach only the per-owner path.';

-- ---------------------------------------------------------------------------------------------
-- cleanup_completed_streams: 162's body, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('cleanup_completed_streams');

-- <docs>fundamentals/work-coordinator/overview</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorDeepPathTests.cs:CleanupCompletedStreamsAsync_NullStreamList_ReturnsZeroAsync</tests>
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
  WITH evicted AS (
    DELETE FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = ANY(p_stream_ids)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = a.stream_id
          AND o.processed_at IS NULL
      )
      AND NOT EXISTS (
        -- 162: stream_id is a write-once copy here and processed_at moved, so the probe reads the
        -- narrow table. This runs per candidate stream, so keeping it off the wide row matters.
        SELECT 1 FROM __SCHEMA__.wh_inbox_state ist
        WHERE ist.stream_id = a.stream_id
          AND ist.processed_at IS NULL
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = a.stream_id
          AND pe.processed_at IS NULL
      )
    RETURNING a.stream_id
  )
  SELECT COUNT(*)::INTEGER INTO v_evicted FROM evicted;

  RETURN COALESCE(v_evicted, 0);
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- _wh_settle_rewind_flags: 374's body, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_settle_rewind_flags');

-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_TheCursorFlushedBeforeItsWork_ClearsTheRewindFlagWhenTheWorkIsRetiredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_RetiringOneOfTwoPendingRows_KeepsTheRewindFlagAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_AFlaggedCursorOfAnotherPerspective_IsLeftAloneAsync</tests>
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

  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = 2
  FROM (SELECT DISTINCT u.sid, u.pname FROM unnest(p_stream_ids, p_perspective_names) AS u(sid, pname)) p
  WHERE pc.stream_id = p.sid
    AND pc.perspective_name = p.pname
    AND (pc.status & 32) = 32
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = pc.stream_id
        AND pe.perspective_name = pc.perspective_name
        AND pe.processed_at IS NULL);
  GET DIAGNOSTICS v_settled = ROW_COUNT;
  RETURN v_settled;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_settle_rewind_flags(UUID[], TEXT[]) IS
'374: settles the RewindRequired cursors among the given (stream, perspective) pairs that have no pending work left '
'(status 2, as update_perspective_cursors leaves a completed pair). Returns how many were settled.';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_perspective_events: 317's body, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_perspective_events');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:ClaimOrphanedPerspectiveEvents_LeasesTheStream_WithTheRowLeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedPerspectiveEvents_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedPerspectiveEvents_AStaleOwnerRowTheRuleCallsDead_LeasesReclaimableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedPerspectiveEvents_DeadInstanceRow_WithOnlyAConnectionNamedForIt_LeasesReclaimableAsync</tests>
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
    WHERE TRUE
      -- Phase H step 6 slice 2: stream ownership now combines wh_active_streams pinning
      -- (OWNER PATH: always wins) with partition-modulo load balancing for unowned streams
      -- (UNOWNED PATH: symmetric with claim_orphaned_outbox / _inbox).
      AND (
        -- OWNER PATH: wh_active_streams says this instance owns the stream. Always claim.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = pe.stream_id
            AND ast.assigned_instance_id = p_instance_id
        )
        OR
        -- UNOWNED / ABANDONED PATH: partition-modulo selection for streams with no live owner.
        (
          (pe.partition_number % p_active_instance_count) = p_instance_rank
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = pe.stream_id
              AND ast.assigned_instance_id != p_instance_id
              -- 317 (#2027): the owner counts while the one liveness rule calls it live.
              AND ast.assigned_instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l)
          )
        )
      )
      -- Ensure ordering - no earlier uncompleted events in same perspective
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
        WHERE earlier.stream_id = pe.stream_id
          AND earlier.perspective_name = pe.perspective_name
          AND earlier.event_id < pe.event_id
          AND (
            (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
            OR (earlier.scheduled_for > p_now)
          )
      )
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
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
          WHERE earlier.stream_id = pe.stream_id
            AND earlier.perspective_name = pe.perspective_name
            AND earlier.event_id < pe.event_id
            AND (
              (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
              OR (earlier.scheduled_for > p_now)
            )
        )
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
    SELECT pe.event_work_id
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN candidate_events ce ON ce.event_work_id = pe.event_work_id
    WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND pe.processed_at IS NULL
    ORDER BY ce.event_id
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
    FROM locked l
    WHERE pe.event_work_id = l.event_work_id
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
    FROM (SELECT DISTINCT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL AND NOT p_ledger_deferred) cs
    JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = cs.c_stream_id
    ORDER BY ast.stream_id
    FOR UPDATE OF ast
  ),
  -- REFRESH: the streams this instance owns under a live lease, judged on the row as locked. Same write as
  -- before: last_activity_at and the lease move, the owner does not.
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM ledger_locked ll
    WHERE ast.stream_id = ll.locked_stream_id
      AND ll.locked_owner = p_instance_id
      AND ll.locked_lease_expiry > p_now
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
    FROM ledger_locked ll
    WHERE ast.stream_id = ll.locked_stream_id
      -- Exactly the rows the refresh does not take. COALESCEd because a NULL owner or lease makes the
      -- refresh's test NULL, and NOT NULL would leave the row to neither path.
      AND NOT COALESCE(ll.locked_owner = p_instance_id AND ll.locked_lease_expiry > p_now, FALSE)
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
  'p_ledger_deferred leaves it to the caller (claim_work), each row carrying its partition number for that write.';

-- ---------------------------------------------------------------------------------------------
-- wh_lease_claimed_streams: 202's body, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_lease_claimed_streams');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_LedgerRowsOfEveryAcquisition_AreTakenInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_AStreamLeasedByTwoAcquisitions_IsWrittenOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentOutboxAndInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
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
    FROM (SELECT DISTINCT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL) cs
    JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = cs.c_stream_id
    ORDER BY ast.stream_id
    FOR UPDATE OF ast
  ),
  -- REFRESH: the streams this instance owns under a live lease, judged on the row as locked. Same write as
  -- before: last_activity_at and the lease move, the owner does not.
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM ledger_locked ll
    WHERE ast.stream_id = ll.locked_stream_id
      AND ll.locked_owner = p_instance_id
      AND ll.locked_lease_expiry > p_now
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
    FROM ledger_locked ll
    WHERE ast.stream_id = ll.locked_stream_id
      -- Exactly the rows the refresh does not take. COALESCEd because a NULL owner or lease makes the
      -- refresh's test NULL, and NOT NULL would leave the row to neither path.
      AND NOT COALESCE(ll.locked_owner = p_instance_id AND ll.locked_lease_expiry > p_now, FALSE)
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
  'more than one run.';
