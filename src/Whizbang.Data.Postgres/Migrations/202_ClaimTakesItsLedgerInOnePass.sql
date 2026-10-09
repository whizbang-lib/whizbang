-- Migration: 202_ClaimTakesItsLedgerInOnePass.sql
-- Date: 2026-10-09
-- Description: One claim takes every stream-ledger row it writes in one stream_id order (#1256).
--
--              200 (#1238) put the outbox acquisition's wh_active_streams writes into one ordered pass.
--              Two shapes it did not reach could still deadlock two claims:
--
--                * claim_orphaned_inbox (last word 167) and claim_orphaned_perspective_events (150)
--                  wrote the ledger the way the outbox did before 200: a REFRESH of the streams the
--                  instance owns, a bare UPDATE ... FROM claimed that locks in whatever order the join
--                  produces, then a PIN of the rest. Two claims that each refresh a stream the other
--                  pins wait on each other. Reproduced by four instances that never register, draining
--                  one inbox: 9 deadlocks in 44 rounds, each at claim_work's inbox acquisition.
--                * one claim_work ran the outbox, inbox and perspective acquisitions in turn, and each
--                  wrote its own streams' ledger rows before the next ran. Even with every pass sorted,
--                  one transaction then took its ledger rows in up to three ascending runs, and two
--                  claims could each hold a row the other's later acquisition needed. Reproduced by the
--                  same four instances draining outbox and inbox work on the same streams: 88 deadlocks
--                  in 64 rounds.
--
--              Now:
--                * every acquisition writes the ledger in 200's shape: lock every existing ledger row of
--                  the claimed streams in stream_id order, whoever owns it; refresh and take over rows
--                  already held; insert streams with no row after the pass, ON CONFLICT DO NOTHING;
--                * each acquisition takes p_ledger_deferred, and returns each claimed row's partition
--                  number. claim_work defers all three, collects the streams they leased, and writes the
--                  ledger once, in one pass, through wh_lease_claimed_streams, after the last queue row
--                  it locks. That also restores the work-table order (162) for the whole claim:
--                  wh_outbox, wh_inbox_state, wh_perspective_events, then wh_active_streams;
--                * a direct caller of an acquisition keeps the default and gets the one-pass write
--                  inside that statement, as before.
--
--              The rows locked are the ones the paths locked before; only the order and the number of
--              passes change. Ownership rules (148), the acquisition windows (159, 194) and the SKIP
--              LOCKED queue-row locks are unchanged. No retries, timeouts or deadlock catching were
--              added. The functions are copied verbatim from their last words apart from the ledger
--              section, the new parameter, the returned partition number and claim_work's collection.
--
-- Dependencies: 200 (claim_orphaned_outbox), 167 (claim_orphaned_inbox), 150
--               (claim_orphaned_perspective_events), 196 (claim_work), 172 (the lock order), 148 (the
--               ownership rules the takeover keeps)
-- Objects: wh_lease_claimed_streams, claim_orphaned_outbox, claim_orphaned_inbox, claim_orphaned_perspective_events, claim_work
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- wh_lease_claimed_streams: the stream ledger for every stream one claim leased work on, written once.
-- ---------------------------------------------------------------------------------------------
-- New in 202. claim_work ran three acquisitions (outbox, inbox, perspective events), and each wrote
-- wh_active_streams rows for its own streams before the next one ran. One transaction therefore took
-- its ledger rows in up to three ascending runs, so two claims could each hold a stream the other's
-- later acquisition needed, whatever order each run was in. The acquisitions now defer the ledger, and
-- claim_work hands every stream they leased to this function once, after the last of them.
--
-- The work-table order (162) is kept as well: every acquisition locks its queue rows before any ledger
-- row is taken, so the claim takes wh_outbox, wh_inbox_state, wh_perspective_events and then
-- wh_active_streams, where it used to lock ledger rows between the queue tables.
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

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_outbox: last word 200, with the ledger deferrable.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_outbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_BacklogOwnedByALivePeer_CostFollowsTheBatchNotTheBacklogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_AnExpiredLeaseBehindAPeerBacklog_IsStillReclaimedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_UnownedStreamInsideTheWindow_IsTakenFromItsHeadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ClaimOrphanedOutbox_RefreshesTheStreamLedgerInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ClaimOrphanedOutbox_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_LedgerRowsOfEveryAcquisition_AreTakenInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedDeadlockMitigationSqlTests.cs:ClaimOrphanedOutbox_AlreadyOwnedWithLiveLease_RefreshesLastActivityAtAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedDeadlockMitigationSqlTests.cs:ClaimOrphanedOutbox_NullOwner_StillTakesPinPathAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedDeadlockMitigationSqlTests.cs:ClaimOrphanedOutbox_NeverPinned_PinPathInsertsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimOrphanedOutbox_RunStopsAtTheFirstRowItMayNotTakeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ClaimOrphanedOutbox_FirstClaim_BumpsToOneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  -- 171: how many consecutive rows one stream may lease in one call. 1 is the previous behavior.
  p_run_length INTEGER DEFAULT 1,
  -- 171: how many of the oldest rows choose the streams that move. NULL means p_max_rows, the
  -- previous behavior; claim_work passes its stream window.
  p_max_heads INTEGER DEFAULT NULL,
  -- 202 (#1256): TRUE leaves the stream ledger to the caller. claim_work passes it so its acquisitions
  -- write no ledger row of their own; it writes every claimed stream's row once, in one stream_id pass,
  -- after the last acquisition (wh_lease_claimed_streams). A direct caller keeps the default and gets
  -- the same one-pass write here, inside this statement.
  p_ledger_deferred BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  -- 202: the partition the ledger row of a new stream is recorded under, for a caller that defers the ledger.
  partition_number INTEGER
) AS $$
#variable_conflict use_column
DECLARE
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  -- LEAST ignores a NULL, so a NULL head cap falls back to the row bound, and a NULL row bound (an
  -- untaught caller) stays unlimited exactly as before.
  v_heads INTEGER := LEAST(p_max_heads, p_max_rows);
  -- 194: how many of the oldest claimable rows this call examines, per lane: eight times the heads it
  -- chooses (the window only chooses heads; each head's run is walked separately below, priced by its
  -- share). A constant against the backlog, as 159 made it for the inbox. A NULL head bound (an
  -- untaught caller) keeps the window unlimited, exactly as before. A variable rather than a subquery,
  -- so the planner sees the number: with a LIMIT it cannot read it estimates the whole table, and a
  -- plan built for the whole table joins the window back to wh_outbox by hashing wh_outbox.
  v_window BIGINT := CASE WHEN v_heads IS NULL THEN NULL
                          ELSE GREATEST(LEAST(v_heads, 1000000), 1)::BIGINT * 8 END;
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION — see claim_orphaned_inbox (mig 025) for the full rationale. Unbounded, this
  -- statement leases every eligible row at once and charges an attempt to each, so the caller's
  -- claim limit governs only what comes back out, never how much gets taken. LIMIT NULL is
  -- unlimited in Postgres, so the default preserves the previous behavior for untaught callers.
  WITH
  -- 194: ownership is a property of the STREAM, so it is decided once per call against the two small
  -- sets below and tested by hash membership (145's shape for the inbox). "Live" keeps 025's meaning:
  -- a heartbeat within p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity.
  others_live AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
    WHERE ast.assigned_instance_id <> p_instance_id
      AND ast.lease_expiry > p_now
      AND (
        si.last_heartbeat_at >= p_stale_cutoff
        OR EXISTS (
          SELECT 1 FROM pg_stat_activity sa
          WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || si.instance_id::text
        )
      )
  ),
  mine_owned AS MATERIALIZED (
    SELECT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
  ),
  -- 194: the rows this call looks at, read once, each lane in an order an index carries and stopped
  -- at the window. Before this the heads were chosen by walking every pending row in arrival order
  -- and testing each for ownership until a batch was found. Under a load the oldest pending rows are
  -- the unleased tails of streams a live peer owns (it leases a run at a time), so every other
  -- instance walked all of them on every poll and found nothing: a measured call returned 13 rows
  -- after visiting 79,590,235 shared buffers, almost all cache hits. The bound has to be on rows
  -- examined, not on rows found, or a call that cannot reach its LIMIT runs to the end of the table.
  --   unowned rows by arrival, through idx_outbox_pending_arrival (157);
  --   expired leases by expiry, through idx_outbox_lease_expiry (158), so a lease that lapsed on a
  --   vanished instance is reclaimed wherever it sits in arrival order.
  -- A row deferred into the future is not claimable and does not take a place in the window, as in
  -- the inbox: a parked row (scheduled_for = infinity) must never be able to fill it.
  scanned AS MATERIALIZED (
    (SELECT o.message_id AS s_message_id, o.stream_id AS s_stream_id, o.created_at AS s_created_at,
            o.partition_number AS s_partition_number
     FROM __SCHEMA__.wh_outbox o
     WHERE o.processed_at IS NULL
       AND o.coalesce_group IS NULL
       AND o.instance_id IS NULL
       AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
     ORDER BY o.created_at, o.message_id
     LIMIT v_window)
    UNION ALL
    (SELECT o.message_id, o.stream_id, o.created_at, o.partition_number
     FROM __SCHEMA__.wh_outbox o
     WHERE o.processed_at IS NULL
       AND o.coalesce_group IS NULL
       AND o.instance_id IS NOT NULL
       AND o.lease_expiry < p_now
       AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
     ORDER BY o.lease_expiry, o.created_at, o.message_id
     LIMIT v_window)
  ),
  -- The ownership rules, unchanged from 148, applied to the window only.
  eligible AS MATERIALIZED (
    SELECT s.s_message_id AS e_message_id, s.s_created_at AS e_created_at
    FROM scanned s
    WHERE (
        EXISTS (SELECT 1 FROM mine_owned m WHERE m.stream_id = s.s_stream_id)
        OR (
          (s.s_partition_number IS NULL
           OR (s.s_partition_number % p_active_instance_count) = p_instance_rank)
          AND NOT EXISTS (SELECT 1 FROM others_live ol WHERE ol.stream_id = s.s_stream_id)
        )
      )
  ),
  candidates AS (
    -- The HEADS: the oldest claimable rows of the window, which choose the streams that move this
    -- call. Locked here, re-asserting the takeability the window read under the statement's
    -- snapshot (172's rule 4), so a row another session took since then is skipped, not held. Driven
    -- from the window and ordered by its column, so the plan sorts the window and probes the primary
    -- key per row; ordered by the table's column, a planner walks the arrival index instead and the
    -- window bounds nothing.
    SELECT o.message_id AS cand_message_id, o.stream_id AS cand_stream_id, o.created_at AS cand_created_at
    FROM eligible e
    JOIN __SCHEMA__.wh_outbox o ON o.message_id = e.e_message_id
    WHERE (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
      -- The stream-ordering rule, unchanged from 148: an earlier row deferred into the future blocks
      -- the later ones. Evaluated here rather than over the whole window, so it runs only for the rows
      -- the walk reaches before it has its heads.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_outbox earlier
        WHERE earlier.stream_id = o.stream_id
          AND earlier.created_at < o.created_at
          AND earlier.scheduled_for IS NOT NULL
          AND earlier.scheduled_for > p_now
          AND earlier.processed_at IS NULL
          -- #668 H3: a coalesce-pending row parks with scheduled_for = created + MaxDelay,
          -- which made it an 'earlier scheduled row' that blocked EVERY later row on the
          -- same stream from claiming for the whole window — re-armed by each new coalesce
          -- row, i.e. permanently under sustained ingest. Coalesce rows are not deferred
          -- deliveries; they are parked for folding and must not gate their stream.
          AND earlier.coalesce_group IS NULL
      )
    ORDER BY e.e_created_at, e.e_message_id
    LIMIT v_heads
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- 171: the streams whose runs extend past their heads. A NULL or empty stream id is a singleton
  -- (the drain looks it up by message id), so it has no run to extend. With a run of 1 there is
  -- nothing to extend either, and the walk below never executes.
  run_streams AS (
    SELECT DISTINCT c.cand_stream_id AS run_stream_id
    FROM candidates c
    WHERE v_run > 1
      AND c.cand_stream_id IS NOT NULL
      AND c.cand_stream_id <> __EMPTY_UUID__::uuid
  ),
  -- Each stream's share of the row bound: the run length, or an even split of the bound when the
  -- chosen streams cannot all have a full run. Walking only the share is what keeps the walk priced
  -- by the batch: streams x share is about the row bound, never streams x run length.
  run_share AS (
    SELECT CASE
             WHEN p_max_rows IS NULL THEN v_run
             ELSE LEAST(v_run, GREATEST(1, CEIL(p_max_rows::NUMERIC / GREATEST(count(*), 1))::INTEGER))
           END AS share
    FROM run_streams
  ),
  run_walk AS (
    SELECT w.w_message_id, w.w_stream_id, w.w_created_at, w.w_takeable, w.w_pos
    FROM run_streams rs
    CROSS JOIN LATERAL (
      -- The stream's pending rows from its first entry, in its own order, skipping the ones this
      -- instance already holds under a live lease (they are its own run already, in flight). One
      -- range scan on idx_outbox_stream_run: Index Cond stream_id = rs.run_stream_id, the ORDER BY
      -- is the index order, the lease test is an index filter on INCLUDEd columns, and the LIMIT
      -- stops the scan at the share.
      SELECT o.message_id AS w_message_id,
             o.stream_id AS w_stream_id,
             o.created_at AS w_created_at,
             -- COALESCEd because bool_and below skips a NULL, which would read as "not blocking".
             COALESCE((o.instance_id IS NULL OR o.lease_expiry < p_now)
               AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now), FALSE) AS w_takeable,
             ROW_NUMBER() OVER (ORDER BY o.created_at, o.message_id) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = rs.run_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND (o.instance_id IS DISTINCT FROM p_instance_id
             OR o.lease_expiry IS NULL
             OR o.lease_expiry <= p_now)
      ORDER BY o.created_at, o.message_id
      LIMIT (SELECT share FROM run_share)
    ) w
  ),
  -- A run is a prefix: it ends at the first row this instance may not take -- a row another
  -- instance holds under a live lease, or a retry deferred into the future. Taking a row beyond it
  -- would publish past a row that has not been published.
  run_open AS (
    SELECT rw.*, bool_and(rw.w_takeable) OVER (PARTITION BY rw.w_stream_id ORDER BY rw.w_pos) AS w_open
    FROM run_walk rw
  ),
  -- 172: the lock re-asserts that the row is still takeable. The walk above read the rows under
  -- the statement's snapshot; a row another session leased and committed since then (the drain's
  -- continuation, a sibling's claim) passes a lock that tests only its id, and this statement then
  -- held it until claim_work committed while never updating it, so that session's completion of
  -- the row waited for the whole poll. With the predicate the lock's recheck drops it, and it ends
  -- the run as a gap does.
  run_locked AS (
    SELECT o.message_id AS locked_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id IN (SELECT ro.w_message_id FROM run_open ro WHERE ro.w_open)
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- ...and at the first row another session holds locked, for the same reason: a lock skipped in
  -- the middle of a run would leave a gap in it.
  run_first_gap AS (
    SELECT ro.w_stream_id AS gap_stream_id, MIN(ro.w_pos) AS gap_pos
    FROM run_open ro
    WHERE ro.w_open
      AND NOT EXISTS (SELECT 1 FROM run_locked rl WHERE rl.locked_message_id = ro.w_message_id)
    GROUP BY ro.w_stream_id
  ),
  run_rows AS (
    SELECT ro.w_message_id, ro.w_pos, ro.w_created_at
    FROM run_open ro
    LEFT JOIN run_first_gap g ON g.gap_stream_id = ro.w_stream_id
    WHERE ro.w_open
      AND (g.gap_pos IS NULL OR ro.w_pos < g.gap_pos)
  ),
  -- The heads always, then run rows round-robin by position (every stream's 2nd row before any
  -- stream's 3rd), cut at the row bound. Cutting by position keeps each stream's rows a prefix.
  chosen AS (
    SELECT u.chosen_message_id
    FROM (
      SELECT a.chosen_message_id, MIN(a.chosen_pos) AS chosen_pos, MIN(a.chosen_created_at) AS chosen_created_at
      FROM (
        SELECT c.cand_message_id AS chosen_message_id, 0::BIGINT AS chosen_pos, c.cand_created_at AS chosen_created_at
        FROM candidates c
        UNION ALL
        SELECT r.w_message_id, r.w_pos, r.w_created_at
        FROM run_rows r
      ) a
      GROUP BY a.chosen_message_id
    ) u
    ORDER BY u.chosen_pos, u.chosen_created_at, u.chosen_message_id
    LIMIT p_max_rows
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = o.attempts + 1
    FROM chosen c
    WHERE o.message_id = c.chosen_message_id
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL  -- 115: coalesce-pending singles are never leased by the pump
    -- Ownership, partition routing and the stream-ordering check were all resolved in `candidates`
    -- above, under a row lock, and a run shares its head's stream and so its ownership. Only the
    -- cheap invariants are re-asserted here, against a lease that lapsed between selection and write.
    --
    --   OWNER PATH — a stream's live owner always claims its messages, partition modulo ignored,
    --   which prevents the rank-churn wedge described in migration 025.
    --   UNOWNED / ABANDONED PATH — partition-based load balancing for streams with no live owner;
    --   "live" means a fresh heartbeat OR a registered LISTEN connection in pg_stat_activity.
    --   STREAM ORDERING — an earlier message in the same stream awaiting a future retry blocks the
    --   later ones, so per-stream order survives a scheduled retry.
    RETURNING o.message_id AS c_message_id, o.stream_id AS c_stream_id, o.partition_number AS c_partition_number
  ),
  -- The stream ledger: one row per stream this call leased rows on. PR #227 split its maintenance
  -- into a REFRESH (a plain UPDATE of the streams this instance already owns under a live lease, so
  -- the steady state never takes the INSERT path) and a PIN for the rest; 172 put each path's rows in
  -- stream_id order.
  --
  -- 200 (#1238): each path in order was not enough, because the refreshes all ran before the pins, so
  -- the statement took its ledger rows in two ascending runs. A claim that refreshes s3 and pins s2
  -- and a claim that refreshes s2 and pins s3 then wait on each other. A claim pins a stream another
  -- instance owns when it sees that owner as not live, which is every stream when the whole fleet's
  -- heartbeats are stale at once.
  --
  -- So every existing ledger row of the claimed streams is locked here, whoever owns it, in the one
  -- order every waiting writer of wh_active_streams uses (172's rule 2). This is the only place this
  -- statement waits for an existing ledger row. The refresh and the takeover below update rows it
  -- already holds and cannot wait, and the insert of a new stream comes after it, in the same order.
  -- The rows locked are the ones the two paths locked before; only their order changes.
  ledger_locked AS (
    SELECT ast.stream_id AS locked_stream_id,
           ast.assigned_instance_id AS locked_owner,
           ast.lease_expiry AS locked_lease_expiry
    -- 202: nothing to lock when the caller writes the ledger itself.
    FROM (SELECT DISTINCT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL AND NOT p_ledger_deferred) cs
    JOIN __SCHEMA__.wh_active_streams ast ON ast.stream_id = cs.c_stream_id
    ORDER BY ast.stream_id
    FOR UPDATE OF ast
  ),
  -- REFRESH: the streams this instance owns under a live lease, judged on the row as locked. Same
  -- write as before: last_activity_at and the lease move, the owner does not.
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
  -- TAKEOVER: every other locked row, under the ownership rules the pin's ON CONFLICT branch applied
  -- (148), unchanged. Whoever the CASE leaves as owner holds the lease: a stream with no owner, one
  -- whose owner is no longer registered, or one already this instance's under a lapsed lease is
  -- leased to p_lease_expiry; a stream another registered instance owns keeps its owner and its lease.
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
  -- PIN: the streams with no ledger row, inserted after the lock pass and in stream_id order. The
  -- sort consumes the whole anti-join before the first insert, and a stream that is not in
  -- ledger_locked reads it to the end, so every row lock above is taken before any insert. An insert
  -- waits only for another claim's uncommitted insert of the same stream, which that claim also makes
  -- after its own lock pass and in the same order. A row that another session created and committed
  -- since this statement's snapshot is left to it (DO NOTHING) rather than locked here, out of order.
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
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id, c.c_partition_number AS partition_number FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER, BOOLEAN) IS
  'Acquires unowned/abandoned pending outbox rows for an instance (see 024 for the ownership rationale, 115 for the '
  'coalesce-aware selection, 148 for the stream lease). 171: the oldest claimable rows (at most p_max_heads) choose '
  'the streams that move, and each leases up to p_run_length of its next consecutive rows as a prefix, within '
  'p_max_rows in total; a run of 1 is the previous behavior. 172: a run row another session leased since the snapshot '
  'is not locked. 194: the heads are chosen from a window of eight times as many oldest claimable rows per lane '
  '(unowned by arrival, expired leases by expiry), so a poll costs a multiple of its batch whatever the backlog holds. '
  '200: the stream ledger rows of the claimed streams are locked in one stream_id pass, whoever owns them, before they '
  'are refreshed or taken over, and streams with no row are inserted after that pass in the same order. 202: '
  'p_ledger_deferred leaves the ledger to the caller (claim_work writes it once for all its acquisitions), and each '
  'row carries its partition number for that write.';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_inbox: last word 167, with 200's one-pass ledger and the ledger deferrable.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_inbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_AnInteractiveStream_IsClaimedAheadOfOlderStandardStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_AStreamWithAnInteractiveRowBehindStandardRows_IsPulledForward_InOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimOrphanedInbox_HonorsTheRowLimitItIsGivenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimOrphanedInbox_ChargesAnAttemptOnlyToRowsItActuallyClaimsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:ClaimOrphanedInbox_LeasesTheStream_WithTheRowLeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedInbox_RefreshesTheStreamLedgerInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedInbox_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedInbox_AStreamWithNoLedgerRow_IsPinnedToTheClaimerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE,
  -- 167: how much of the idle band may be ACQUIRED on this call. Zero withholds it, NULL admits it
  -- at full width, and a positive number is a trickle slice. One parameter rather than a flag,
  -- because admission and size are one decision: a trickle that is admitted but unbounded leases
  -- the whole band and merely re-emits a slice of it, which holds the rows against every peer and
  -- spends the outstanding budget on work this poll has already decided not to do.
  --
  -- Defaulted to zero: a caller that has not been taught about the band must never lease work it
  -- will then withhold.
  p_idle_max_rows INTEGER DEFAULT 0,
  -- 202 (#1256): TRUE leaves the stream ledger to the caller. claim_work passes it so its acquisitions
  -- write no ledger row of their own; it writes every claimed stream's row once, in one stream_id pass,
  -- after the last acquisition (wh_lease_claimed_streams). A direct caller keeps the default and gets
  -- the same one-pass write here, inside this statement.
  p_ledger_deferred BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  -- 202: the partition the ledger row of a new stream is recorded under, for a caller that defers the ledger.
  partition_number INTEGER
) AS $$
#variable_conflict use_column
DECLARE
  -- 150: the bands (Whizbang.Core.Priority.WorkPriority) and the scheduling constants. The wait target and the
  -- floor are the framework's defaults for this release; a host's batch hook adjusts a stream's number in memory.
  c_interactive_band_end CONSTANT INTEGER := 99;
  c_standard_band_end CONSTANT INTEGER := 199;
  c_background_wait_target_seconds CONSTANT INTEGER := 300;
  c_background_floor_share CONSTANT NUMERIC := 0.1;
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION, not just re-emission. Without a limit here this statement leases every
  -- eligible row in one shot and charges an attempt to each, so an instance restarting onto a large
  -- backlog claims the whole thing instantly. The rows it cannot dispatch inside the lease expire
  -- un-dispatched, get re-claimed here, spend another attempt, and eventually dead-letter as
  -- MaxAttemptsExceeded having never reached a receptor - the error branch below is this statement
  -- stamping its own casualties.
  --
  -- The caller's claim limit previously reached claim_orphaned_perspective_events but not this
  -- function, so both the adaptive claim window and the outstanding budget were throttling a valve
  -- downstream of the flood: p_max_streams bounds only the RE-EMISSION of work already held
  -- (claim_work's eligible_inbox filters on instance_id = p_instance_id). That is why narrowing the
  -- window changed the rate of lease saturation without ever converging.
  --
  -- LIMIT NULL is unlimited in Postgres, so the default preserves the old behavior for any caller
  -- that has not been taught to pass a bound.
  WITH params AS (
    -- BIGINT: the unbounded default is INT max and max_rows + 1 below must not overflow.
    SELECT COALESCE(p_max_rows, 2147483647)::BIGINT AS max_rows,
           -- 150: a background stream that has waited past the background wait target competes as standard.
           p_now - (c_background_wait_target_seconds * INTERVAL '1 second') AS background_promote_before,
           -- 150: the share of a batch the background bucket always receives while it has pending streams.
           GREATEST(1, CEIL(LEAST(COALESCE(p_max_rows, 2147483647), 1000000) * c_background_floor_share))::BIGINT AS background_floor,
           -- 159: rows of one band and one ownership class the acquisition will look at. A multiple
           -- of the batch, and a constant against the backlog, which is the whole point: the window
           -- is what makes a poll cost the same whether the queue holds a thousand rows or a
           -- million. It cannot cost the acquisition its throughput, only its breadth: the lanes
           -- claim p_max_rows rows either way, and a narrower window means a lane meets fewer
           -- distinct streams and takes more rows from each (the few-mode branch below), which is
           -- the same batch drawn less widely. The window is over the OLDEST claimable rows and
           -- those are what the poll claims, so it advances every poll rather than re-reading one
           -- set forever.
           GREATEST(LEAST(COALESCE(p_max_rows, 1000), 1000000), 1)::BIGINT * 8 AS claim_window
  ),
  -- 145: ownership is a per-STREAM property. The two sets below are built ONCE per call from the small
  -- ledger tables and tested by hash membership. 138 evaluated the same predicate as correlated
  -- EXISTS probes on every pending inbox row, which is one of the two reasons the acquisition scan
  -- grew with the backlog instead of the batch (#714). "Live" keeps 025's meaning: a heartbeat within
  -- p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity.
  others_live AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
    WHERE ast.assigned_instance_id <> p_instance_id
      AND ast.lease_expiry > p_now
      AND (
        si.last_heartbeat_at >= p_stale_cutoff
        OR EXISTS (
          SELECT 1 FROM pg_stat_activity sa
          WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || si.instance_id::text
        )
      )
  ),
  mine_owned AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
  ),
  -- 159: the rows this call may take, read once, in an order an index carries. Every lane below
  -- selects from here instead of from wh_inbox_state, and each had re-derived the same predicate for
  -- itself:
  --
  --   WHERE processed_at IS NULL AND (instance_id IS NULL OR lease_expiry < p_now)
  --
  -- A disjunction has no index order, so each of the five walked the whole pending inbox and threw
  -- away what a live peer holds -- one plan removed 4,716 rows of 4,800 by filter to yield seven
  -- streams -- and fetched a heap page per row doing it, because the stream-ordered index carries
  -- neither is_event nor priority and a queue table under load is never all-visible, so nothing on
  -- it is ever really index-only. Five passes over the backlog per poll, several polls a second,
  -- every instance. Doubling the backlog doubled the poll.
  --
  -- Split in two, each half is a partial predicate a planner can prove, so 159's indexes carry the
  -- order: unowned rows by arrival, leased rows by lease expiry so the longest-expired come first.
  -- One walk per band per class, each stopped at the window, each covered by its index. The bucket
  -- is the leading key so a band draws from its own range: a large background backlog can never
  -- crowd an interactive row out of the window, and the background floor below still finds the
  -- background band populated. Rows of one stream keep their arrival order, which is what per-stream
  -- FIFO rests on, and the lanes' own band, kind, ownership and partition filters are unchanged --
  -- this narrows what they read, never what they choose.
  --
  -- The window is a bound the LIMITs below could not provide on their own. A lane stops at its
  -- LIMIT only when it can reach it, and during an import most pending rows belong to a peer or to
  -- another rank, so a lane that wants eleven streams and can see seven never stops early and runs
  -- to the end of the table. The bound has to be on rows examined, not on rows found.
  --
  -- The scheduled_for predicate rides along: a row that is not due yet is not claimable now, and
  -- every lane applied it.
  -- Each lane names its band as a LITERAL, and the reason is the only reason: a partial index is
  -- considered only when Postgres can prove the query's predicate implies the index's, and that
  -- proof is textual. The previous shape selected a band by comparing a CASE expression to a bucket
  -- column joined in from a VALUES list, which is not provable against ANY band predicate because
  -- the bucket is a join column and not a constant. Every band index was therefore unusable, and
  -- the lanes fell back to reading the whole pending set once per bucket -- O(backlog) on the
  -- hottest path in the system, while the three indexes they were meant to read went on charging
  -- every claim write for nothing. Measured on 300k pending rows with the held-lane index in
  -- place, which is the comparison that is actually fair: 11,102 buffers and 5,249ms against
  -- 5 buffers and under 20ms. The old shape DID reach an index -- the held-lane one -- but it
  -- leads with instance_id, so for the unowned set it reads the whole backlog per bucket and
  -- top-N sorts, because received_at sits late in its key. The band indexes are keyed on
  -- (received_at, message_id), which is the ORDER BY, so they stop at the LIMIT.
  --
  -- A plpgsql constant would reintroduce the same defect. c_interactive_band_end is a PARAMETER at
  -- plan time, and the generic plan plpgsql settles into after a few executions cannot fold it, so
  -- the bounds are written here as the same literals the index predicates carry. That agreement is
  -- held by PriorityLaneIndexUsabilityTests against WorkPriority, not by hope.
  --
  -- The four lanes partition the pending set with no overlap: interactive takes the whole band
  -- regardless of kind (its index has no is_event predicate), the two event lanes take their bands,
  -- and commands take everything else that is not an event. Each keeps its own claim_window, so a
  -- deep background band cannot consume the budget an interactive row needs.
  claimable AS MATERIALIZED (
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.priority <= 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.is_event = TRUE
       AND i.priority >= 99 + 1
       AND i.priority <= 199
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.is_event = TRUE
       -- 167: bounded above, so the background lane stops at 399 and idle rows are not acquired
       -- as background. The literal matches the lane index's own literal, which is what lets the
       -- planner prove the index applies.
       AND i.priority > 199
       AND i.priority <= 399
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    -- 167: the idle band. Acquired only when the caller says the band is admitted this poll --
    -- settled, past the trickle age, or past the forced-drain floor. The guard is a parameter and
    -- not a time test here because admission is decided once per poll in claim_work; deciding it
    -- again per branch would let the two disagree.
    --
    -- The bound is a literal so it matches the idle lane's own literal predicate. A computed or
    -- joined bound cannot be proved against a partial index, which is how the band lanes came to be
    -- unreachable before 150.
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.is_event = TRUE
       AND i.priority > 399
       AND (p_idle_max_rows IS NULL OR p_idle_max_rows > 0)
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     -- Bounded by the slice when there is one, by the ordinary window when the band is admitted
     -- at full width. This is where "a trickle is bounded by its slice" is actually enforced.
     LIMIT LEAST(
       (SELECT claim_window FROM params),
       COALESCE(p_idle_max_rows, (SELECT claim_window FROM params))))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NULL
       AND i.is_event = FALSE
       AND i.priority > 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NOT NULL AND i.lease_expiry < p_now
       AND i.priority <= 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.lease_expiry, i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
    UNION ALL
    (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
     FROM __SCHEMA__.wh_inbox_state i
     WHERE i.processed_at IS NULL AND i.instance_id IS NOT NULL AND i.lease_expiry < p_now
       AND i.priority > 99
       AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
     ORDER BY i.lease_expiry, i.received_at, i.message_id
     LIMIT (SELECT claim_window FROM params))
  ),
  -- 150 BUCKET LANES (priority step 3). Every row carries an effective priority (149); the claim schedules by the
  -- bucket the number falls in: interactive (1 to 99), standard (100 to 199), background (200 and up). Lane 0 is
  -- every stream with a pending interactive row anywhere in it: the fold is over all of a stream's pending rows,
  -- so an interactive row queued behind bulk rows on its own stream pulls the whole stream forward while the
  -- stream's rows are still taken in order (its predecessors are prerequisites). That set is small by nature and
  -- served by idx_inbox_pending_interactive, so ranking it is bounded by the pending interactive rows, not the
  -- backlog. Lanes 1 and 2 keep 145's breadth-first selection with an early stop, run once per band over the
  -- band's own arrival-order index, so each lane's cost still follows the batch. Background streams keep a floor
  -- of every batch (never starved by a steady standard flow), and a background stream that has waited past the
  -- background wait target competes as standard. Inside a lane a command keeps its place ahead of events (145's
  -- command lane, #721). Priority reorders streams, never rows within a stream.
  urgent_streams AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.priority <= c_interactive_band_end
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows FROM params)
  ),
  pick_urgent AS (
    SELECT i.message_id AS cand_message_id,
           0 AS lane,
           CASE WHEN i.is_event THEN 1 ELSE 0 END AS kind,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    JOIN urgent_streams us ON us.stream_id = i.stream_id
    WHERE TRUE
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
  ),
  urgent_taken AS (
    SELECT LEAST((SELECT count(*) FROM pick_urgent), (SELECT max_rows FROM params))::BIGINT AS n
  ),
  -- Commands outside the urgent streams: a request someone is waiting on keeps its lane ahead of events, inside
  -- the band its number puts it in. Served by idx_inbox_pending_commands; the set is small by nature.
  pick_commands AS (
    SELECT i.message_id AS cand_message_id,
           CASE WHEN (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params)) THEN 2 ELSE 1 END AS lane,
           0 AS kind,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE TRUE
      AND i.is_event = FALSE
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
  ),
  commands_taken AS (
    SELECT LEAST((SELECT count(*) FROM pick_commands),
                 GREATEST((SELECT max_rows FROM params) - (SELECT n FROM urgent_taken), 0))::BIGINT AS n
  ),
  budget AS (
    SELECT GREATEST((SELECT max_rows FROM params) - (SELECT n FROM urgent_taken) - (SELECT n FROM commands_taken), 0) AS remaining_total
  ),
  -- The background reservation: as many rows as the floor, bounded by what is actually pending (a bounded probe)
  -- and by what the batch has left after the urgent rows and the commands.
  background_reserved AS (
    SELECT LEAST((SELECT background_floor FROM params),
                 (SELECT count(*) FROM (
                    SELECT 1 FROM claimable i
                    WHERE TRUE
                      AND i.is_event = TRUE
                      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
                      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
                      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
                    LIMIT (SELECT background_floor FROM params)) probe),
                 (SELECT remaining_total FROM budget))::BIGINT AS n
  ),
  -- LANE 1: standard rows, plus background rows promoted past the wait target; breadth-first, early stop.
  eligible_event_streams_std AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.is_event = TRUE
      AND (i.priority BETWEEN c_interactive_band_end + 1 AND c_standard_band_end OR (i.priority > c_standard_band_end AND i.received_at < (SELECT background_promote_before FROM params)))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows + 1 FROM params)
  ),
  mode_std AS (
    SELECT (SELECT count(*) FROM eligible_event_streams_std) AS n_streams,
           GREATEST((SELECT remaining_total FROM budget) - (SELECT n FROM background_reserved), 0) AS remaining
  ),
  event_heads_std AS (
    SELECT i.message_id AS cand_message_id,
           1 AS lane,
           1 AS kind,
           1::BIGINT AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE (SELECT n_streams > remaining FROM mode_std)
      AND i.is_event = TRUE
      AND (i.priority BETWEEN c_interactive_band_end + 1 AND c_standard_band_end OR (i.priority > c_standard_band_end AND i.received_at < (SELECT background_promote_before FROM params)))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox_state j
        WHERE j.stream_id = i.stream_id
          AND j.processed_at IS NULL
          AND j.is_event = TRUE
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
          AND (j.instance_id IS NULL OR j.lease_expiry < p_now)
          AND (j.scheduled_for IS NULL OR j.scheduled_for <= p_now)
          AND (j.partition_number IS NULL
               OR (j.partition_number % p_active_instance_count) = p_instance_rank
               OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = j.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      )
    ORDER BY i.received_at, i.message_id
    LIMIT (SELECT remaining FROM mode_std)
  ),
  kk_std AS (
    SELECT GREATEST(1, CEIL(remaining::NUMERIC / GREATEST(n_streams, 1)))::INTEGER AS k FROM mode_std
  ),
  event_few_std AS (
    SELECT h.message_id AS cand_message_id,
           1 AS lane,
           1 AS kind,
           h.rn AS stream_seq,
           h.received_at AS cand_received_at
    FROM eligible_event_streams_std s
    CROSS JOIN kk_std
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at,
             ROW_NUMBER() OVER (ORDER BY i.received_at, i.message_id) AS rn
      FROM claimable i
      WHERE i.stream_id = s.stream_id
        AND i.is_event = TRUE
        AND (i.partition_number IS NULL
             OR (i.partition_number % p_active_instance_count) = p_instance_rank
             OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      ORDER BY i.received_at, i.message_id
      LIMIT kk_std.k
    ) h
    WHERE (SELECT n_streams <= remaining FROM mode_std)
  ),
  standard_taken AS (
    SELECT ((SELECT count(*) FROM event_heads_std) + (SELECT count(*) FROM event_few_std))::BIGINT AS n
  ),
  -- LANE 2: background rows inside the wait target; whatever the standard lane left, never less than the floor.
  eligible_event_streams_bg AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.is_event = TRUE
      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows + 1 FROM params)
  ),
  mode_bg AS (
    SELECT (SELECT count(*) FROM eligible_event_streams_bg) AS n_streams,
           GREATEST((SELECT remaining_total FROM budget) - (SELECT n FROM standard_taken), 0) AS remaining
  ),
  event_heads_bg AS (
    SELECT i.message_id AS cand_message_id,
           2 AS lane,
           1 AS kind,
           1::BIGINT AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE (SELECT n_streams > remaining FROM mode_bg)
      AND i.is_event = TRUE
      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox_state j
        WHERE j.stream_id = i.stream_id
          AND j.processed_at IS NULL
          AND j.is_event = TRUE
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
          AND (j.instance_id IS NULL OR j.lease_expiry < p_now)
          AND (j.scheduled_for IS NULL OR j.scheduled_for <= p_now)
          AND (j.partition_number IS NULL
               OR (j.partition_number % p_active_instance_count) = p_instance_rank
               OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = j.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      )
    ORDER BY i.received_at, i.message_id
    LIMIT (SELECT remaining FROM mode_bg)
  ),
  kk_bg AS (
    SELECT GREATEST(1, CEIL(remaining::NUMERIC / GREATEST(n_streams, 1)))::INTEGER AS k FROM mode_bg
  ),
  event_few_bg AS (
    SELECT h.message_id AS cand_message_id,
           2 AS lane,
           1 AS kind,
           h.rn AS stream_seq,
           h.received_at AS cand_received_at
    FROM eligible_event_streams_bg s
    CROSS JOIN kk_bg
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at,
             ROW_NUMBER() OVER (ORDER BY i.received_at, i.message_id) AS rn
      FROM claimable i
      WHERE i.stream_id = s.stream_id
        AND i.is_event = TRUE
        AND (i.partition_number IS NULL
             OR (i.partition_number % p_active_instance_count) = p_instance_rank
             OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox_state l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      ORDER BY i.received_at, i.message_id
      LIMIT kk_bg.k
    ) h
    WHERE (SELECT n_streams <= remaining FROM mode_bg)
  ),
  pick AS (
    -- A stream with rows in two bands is picked by both lanes (each takes the stream's heads in order); keep
    -- the more urgent lane's row so no message is a candidate twice.
    SELECT DISTINCT ON (u.cand_message_id) u.cand_message_id, u.lane, u.kind, u.stream_seq, u.cand_received_at
    FROM (
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM pick_urgent
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM pick_commands
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_heads_std
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_few_std
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_heads_bg
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_few_bg
    ) u
    ORDER BY u.cand_message_id, u.lane
  ),
  -- 159: the batch is cut before the row lookup, not after it. The lanes together offer several
  -- times the batch, every one of them was fetched by primary key to be ordered and then discarded,
  -- and the whole ordering key (lane, kind, stream order, arrival, id) is already carried here --
  -- nothing the lookup returns takes part in it. A few batches of headroom is kept so SKIP LOCKED
  -- below still has rows to fall through to when a peer holds the head of the order; past that a
  -- contended poll returns a short batch and the next poll takes the rest, which is what SKIP LOCKED
  -- means everywhere else in this function.
  pick_ordered AS (
    SELECT u.cand_message_id, u.lane, u.kind, u.stream_seq, u.cand_received_at
    FROM pick u
    ORDER BY u.lane, u.kind, u.stream_seq, u.cand_received_at, u.cand_message_id
    LIMIT (SELECT max_rows * 4 FROM params)
  ),
  candidates AS (
    -- Lock under lane order. SKIP LOCKED skips rows a concurrent claimer holds, and the volatile predicates
    -- re-check under the lock - a row leased between pick and here is filtered exactly as before.
    SELECT i.message_id AS cand_message_id,
           pick.lane AS cand_lane,
           pick.kind AS cand_kind,
           pick.stream_seq AS cand_stream_seq,
           pick.cand_received_at
    FROM __SCHEMA__.wh_inbox_state i
    JOIN pick_ordered pick ON pick.cand_message_id = i.message_id
    WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND i.processed_at IS NULL
    ORDER BY pick.lane, pick.kind, pick.stream_seq, pick.cand_received_at, pick.cand_message_id
    LIMIT (SELECT max_rows FROM params)
    FOR UPDATE OF i SKIP LOCKED
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_inbox_state i
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: claim_orphaned is the SOLE source of attempt counting.
        -- Bumps unconditionally on every claim (fresh or re-claim) so attempts = 1 means
        -- "first attempt has started" (one-based). process_inbox_failures records error and
        -- releases the lease but does NOT bump - the next claim's bump captures attempt N+1.
        -- Single-source removes the double-counting that two bumps per failed cycle would cause.
        -- Without this, hung handlers (no exception thrown, lease eventually expires) looked
        -- identical to fresh messages in a consumer's production environment - an extended stuck-message backlog with
        -- attempts=0 across thousands of rows (production audit).
        attempts = i.attempts + 1,
        -- Attribute the attempt this claim is REPLACING. process_inbox_failures records
        -- error/failure_reason only when dispatch reported a failure; when the process is killed
        -- mid-dispatch (SIGKILL from a failed liveness probe, container replaced, handler hung past
        -- its lease) nothing reports anything - the lease just expires and the bump above spends
        -- another attempt in silence. The budget then runs out and the row dead-letters as
        -- "MaxAttemptsExceeded: attempts=N > max=M", which describes the counter and not the cause.
        --
        -- Observed in production as ~54k inbox rows averaging 11 attempts, every one with
        -- error IS NULL and failure_reason = 99 (Unknown) - no way to tell a crash-looping host
        -- from a genuinely failing handler. Stamp the abandonment so the row carries its own
        -- history: an expired lease still held by an instance (instance_id IS NOT NULL) means the
        -- previous attempt ended without ever reporting. Guarded on error IS NULL so a real
        -- recorded failure is never papered over - that error is the better diagnostic.
        failure_reason = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 6  -- MessageFailureReason.LeaseExpired
          ELSE i.failure_reason
        END,
        error = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 'Attempt ' || i.attempts || ' ended without a reported outcome: lease held by instance '
               || i.instance_id::text || ' expired at ' || i.lease_expiry::text
               || ' (process terminated mid-dispatch, or the handler outran its lease). '
               || 'No dispatch failure was recorded for that attempt.'
          ELSE i.error
        END
    -- Ownership and partition routing were resolved in `candidates` above, under a row lock. Only
    -- the two cheap invariants are re-asserted here, as defense against a lease that lapsed between
    -- selection and write:
    --
    --   OWNER PATH - a stream's live owner always claims its messages, ignoring partition modulo,
    --   which preserves per-stream FIFO and prevents the rank-churn wedge seen in production: when
    --   active_instance_count changes, modulo routing for a partition can shift to a different rank
    --   than the stream's existing owner. Without that branch the modulo-matched instance is blocked
    --   by the ownership NOT EXISTS and the owner is blocked by the modulo filter - neither claims.
    --
    --   UNOWNED / ABANDONED PATH - no live owner, so partition-based load balancing decides the rank.
    --   NULL partition_number (no stream binding) stays claimable by any rank. "Live" means a
    --   heartbeat within p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity; an
    --   instance killed by SIGKILL holds no meaningful ownership, and without the recency clause its
    --   dead lease would block cross-instance claims for the full lease duration (300 s default).
    --   See 011 (cleanup_stale_instances) for the eventual DELETE - this claim-time check is what
    --   makes recovery happen at the stale threshold rather than at lease expiry.
    FROM candidates c
    WHERE i.message_id = c.cand_message_id
      AND i.processed_at IS NULL
      AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
    RETURNING i.message_id AS c_message_id, i.stream_id AS c_stream_id, i.partition_number AS c_partition_number,
              c.cand_lane AS c_lane, c.cand_kind AS c_kind, c.cand_stream_seq AS c_stream_seq, c.cand_received_at AS c_received_at
  ),
  -- The stream ledger: one row per stream this call leased rows on. 202 (#1256): written in migration 200's
  -- one-pass shape (#1238), which this function lacked. Its refresh was a bare UPDATE ... FROM claimed, locking
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
  -- 150: the result carries the pick order (bucket lane, commands before events, then breadth-first). UPDATE ... RETURNING
  -- alone promises no order, and a caller that consumes the rows in order relies on the lane.
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id, c.c_partition_number AS partition_number FROM claimed c
  ORDER BY c.c_lane, c.c_kind, c.c_stream_seq, c.c_received_at, c.c_message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_inbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, BOOLEAN, INTEGER, BOOLEAN) IS
  'Acquires unowned/abandoned pending inbox rows for an instance, bounded by p_max_rows (025 ownership, 145 bounded '
  'acquisition, 148 stream leases, 150 bucket lanes, 159 claim window, 167 idle band). 202: the stream ledger rows of '
  'the claimed streams are locked in one stream_id pass, whoever owns them, before they are refreshed or taken over, '
  'and streams with no row are inserted after that pass in the same order; p_ledger_deferred leaves the ledger to the '
  'caller (claim_work), and each row carries its partition number for that write.';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_perspective_events: last word 150, with 200's one-pass ledger and the ledger deferrable.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_perspective_events');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:ClaimOrphanedPerspectiveEvents_LeasesTheStream_WithTheRowLeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedPerspectiveEvents_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
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
      -- (OWNER PATH — always wins) with partition-modulo load balancing for unowned streams
      -- (UNOWNED PATH — symmetric with claim_orphaned_outbox / _inbox).
      AND (
        -- OWNER PATH — wh_active_streams says this instance owns the stream. Always claim.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = pe.stream_id
            AND ast.assigned_instance_id = p_instance_id
        )
        OR
        -- UNOWNED / ABANDONED PATH — partition-modulo selection for streams with no live owner.
        (
          (pe.partition_number % p_active_instance_count) = p_instance_rank
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = pe.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND (
                -- Existing check: a row in wh_service_instances counts as alive.
                -- This is removed by cleanup_stale_instances at the stale threshold.
                EXISTS (
                  SELECT 1 FROM __SCHEMA__.wh_service_instances si
                  WHERE si.instance_id = ast.assigned_instance_id
                )
                -- Slice 2b of zero-idle-polling — additive defensive predicate:
                -- a pod with a live Whizbang LISTEN connection in pg_stat_activity
                -- counts as alive even if its wh_service_instances row has been
                -- cleaned up (transient state during pod restart, race between
                -- cleanup_stale_instances and the pod opening its LISTEN
                -- connection on next boot). Strictly additive — only adds
                -- protection against premature orphan-claim, never loosens.
                OR EXISTS (
                  SELECT 1 FROM pg_stat_activity sa
                  WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || ast.assigned_instance_id::text
                )
              )
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
-- claim_work: last word 196, writing the ledger once for all its acquisitions.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_work');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkSqlTests.cs:ClaimWork_OutboxHasUnprocessedWork_ReturnsThatWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimWork_DoesNotAcquireMoreThanItsCallerAskedForAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_BackgroundStreams_KeepAFloorOfTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IdleBandHeldRowReOfferTests.cs:WhileBusy_AnIdleBandCommandTheClaimLeases_IsReOfferedToTheDrainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IdleBandHeldRowReOfferTests.cs:AnIdleBandCommandPastMaxAttempts_ReclaimedAfterItsLeaseLapses_ReachesTheDrainEveryCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_ReOffersOneRowPerHeldStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OutboxAcquisitionFillsItsBound_RaisesTheFullNoticeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatForTheSameInstanceDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_TheRegistrationCallDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_StaleRegistration_LeavesTheRowAlone_AndAsksForRegistrationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_MissingRegistration_DoesNotInsertIt_AndAsksForRegistrationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_StaleOwnRegistration_StillRanksItselfAmongTheLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_MissingOwnRegistration_StillRanksItselfAmongTheLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_LedgerRowsOfEveryAcquisition_AreTakenInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimWork_AStreamLeasedByTwoAcquisitions_IsWrittenOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentOutboxAndInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300,
  p_fresh_share DOUBLE PRECISION DEFAULT 0.5,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE,
  p_max_perspective_streams INTEGER DEFAULT NULL,
  -- 167: the idle band. The CALLER owns settledness -- it already tracks the dwell for housekeeping
  -- -- and passes it in. Computing it here would mean counting the backlog on every poll, which is
  -- the cost the claim is measured against.
  p_idle_settled BOOLEAN DEFAULT FALSE,
  p_idle_trickle_after INTERVAL DEFAULT INTERVAL '30 minutes',
  p_idle_trickle_slice INTEGER DEFAULT 10,
  p_idle_force_after INTERVAL DEFAULT INTERVAL '4 hours',
  -- 171: the outbox's own row bound, independent of the stream window. NULL is the previous
  -- behavior: acquisition bounded by p_max_streams rows.
  p_max_outbox_rows INTEGER DEFAULT NULL,
  -- 171: how many consecutive rows one outbox stream may lease per claim. NULL (or 1) is the
  -- previous behavior: one row per chosen head.
  p_outbox_run_length INTEGER DEFAULT NULL
) RETURNS TABLE(
  source VARCHAR(20),           -- __CATEGORY_OUTBOX__ | __CATEGORY_INBOX__ | 'receptor' | __CATEGORY_PERSPECTIVE__
  work_id UUID,
  work_stream_id UUID,
  partition_number INTEGER,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  message_data TEXT,
  metadata JSONB,
  status INTEGER,
  attempts INTEGER,
  is_newly_stored BOOLEAN,
  is_orphaned BOOLEAN,
  perspective_name VARCHAR(200),
  priority INTEGER,             -- 150: the inbox row's effective priority (NULL for other sources)
  received_at TIMESTAMPTZ       -- 150: the inbox row's arrival (NULL for other sources)
) AS $$
DECLARE
  c_source_outbox CONSTANT VARCHAR(20) := __CATEGORY_OUTBOX__;
  c_source_inbox CONSTANT VARCHAR(20) := __CATEGORY_INBOX__;
  v_has_any_work BOOLEAN;
  -- 167: the idle band's admission, decided once per poll in the outer block. 170: it bounds inbox
  -- ACQUISITION and the perspective lane loop; the inbox re-offer no longer reads it.
  v_idle_admitted BOOLEAN := FALSE;  -- may the idle band be claimed at all on this poll
  v_idle_capped BOOLEAN := FALSE;    -- admitted by trickle, so bounded to a slice
  -- What to hand acquisition: 0 withheld, NULL full width, N a trickle slice.
  v_idle_acquire_rows INTEGER := 0;
  v_idle_oldest INTERVAL;            -- age of the oldest idle row this instance can see
BEGIN
  -- 157: this function runs under plan_cache_mode = force_custom_plan (see its closing clause), so
  -- the plans below and in the acquisition functions it calls are made for the tables as they are
  -- at each poll, never kept from a poll that found them empty.
  -- Empty-call short-circuit: cheap indexed EXISTS lookups on partial indexes.
  -- Each LIMIT 1 against an existing partial index is sub-millisecond when buffer-cached.
  -- Note: wh_receptor_processing uses completed_at (not processed_at) for the "is done" semantic.
  -- 115: coalesce-pending rows are not claim-pump work, they wait for the coalesce worker.


  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL AND coalesce_group IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox_state WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_receptor_processing WHERE completed_at IS NULL LIMIT 1);

  IF NOT v_has_any_work THEN
    RETURN;  -- empty result set; orphan-claim sub-functions never invoked
  END IF;

  -- Non-empty path: claim outbox work and return it.
  -- Inbox / receptor / perspective claim + return land in subsequent TDD cycles.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    v_count INTEGER;
    -- v0.661: track per-category RETURN QUERY rowcount so the drain-mode hint
    -- can be derived from ROW_COUNT instead of four fresh COUNT(*) queries.
    v_outbox_rows INTEGER := 0;
    v_inbox_rows INTEGER := 0;
    v_receptor_rows INTEGER := 0;
    v_perspective_rows INTEGER := 0;
    -- 171: the outbox acquisition's row bound and what it actually leased, for the full notice.
    v_outbox_bound INTEGER := COALESCE(p_max_outbox_rows, p_max_streams);
    v_outbox_acquired INTEGER := 0;
    -- 158: the lane walks that re-offer held inbox and perspective streams.
    v_bucket INTEGER;
    v_is_event BOOLEAN;
    v_start UUID;
    v_remaining INTEGER;
    v_rows INTEGER;
    -- 202 (#1256): every stream the acquisitions below lease work on, with its partition number, in step. The
    -- acquisitions no longer write the stream ledger themselves; it is written once, after the last of them.
    v_ledger_streams UUID[] := '{}';
    v_ledger_partitions INTEGER[] := '{}';
  BEGIN
    -- 167: whether the idle band may be claimed at all on this poll.
    --
    -- Idle work is work nobody waits for, so it is withheld while the service is busy. A bucket that
    -- is withheld can starve, and its first occupant is auditing, so withholding is bounded by TIME
    -- rather than by the service happening to go quiet:
    --
    --   settled            -> drain at full width, which is the point of the band
    --   older than force   -> drain at full width anyway, however busy. The floor.
    --   older than trickle -> take a slice, so progress is never zero
    --   otherwise          -> withheld
    --
    -- One probe against the held-lane index answers all three, and only when the instance holds idle
    -- work at all: no idle rows, no cost.
    -- CLAIMABLE idle work, not merely work this instance already holds. An earlier draft asked only
    -- about held rows, which cannot admit anything: idle rows arrive unowned, so the band could
    -- never be entered, nothing was ever leased, and the probe went on reporting nothing to do. The
    -- band must be admitted on what this instance COULD take.
    --
    -- Both tables, because the band's first occupant is auditing and audit lands in perspective
    -- events; a gate derived from the inbox alone would withhold a perspective backlog for ever.
    --
    -- MIN of the timestamp rather than MAX of the age: the aggregate has to be over an indexed
    -- COLUMN for the planner to answer it by walking to the first qualifying entry and stopping.
    -- MAX(v_now - received_at) is an expression, and costs a pass over the whole band instead.
    -- LEAST ignores NULLs in Postgres, so a band empty on one side still reports the other, and
    -- empty on both leaves this NULL, which is the "nothing to admit" case below.
    SELECT v_now - LEAST(
      (SELECT MIN(i.received_at)
       FROM __SCHEMA__.wh_inbox_state i
       WHERE i.processed_at IS NULL
         AND i.is_event = TRUE
         AND i.priority > 399
         AND (i.instance_id = p_instance_id OR i.instance_id IS NULL OR i.lease_expiry < v_now)
         AND (i.scheduled_for IS NULL OR i.scheduled_for <= v_now)),
      (SELECT MIN(pe.created_at)
       FROM __SCHEMA__.wh_perspective_events pe
       WHERE pe.processed_at IS NULL
         AND pe.priority > 399
         AND (pe.instance_id = p_instance_id OR pe.instance_id IS NULL OR pe.lease_expiry < v_now)
         AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= v_now))
    ) INTO v_idle_oldest;

    IF v_idle_oldest IS NULL THEN
      v_idle_admitted := FALSE;
    ELSIF p_idle_settled OR v_idle_oldest >= p_idle_force_after THEN
      v_idle_admitted := TRUE;
    ELSIF v_idle_oldest >= p_idle_trickle_after THEN
      v_idle_admitted := TRUE;
      v_idle_capped := TRUE;
    END IF;

    -- Inbox acquisition and the perspective re-offer are both derived from the one decision above.
    IF NOT v_idle_admitted THEN
      v_idle_acquire_rows := 0;
    ELSIF v_idle_capped THEN
      v_idle_acquire_rows := GREATEST(p_idle_trickle_slice, 0);
    ELSE
      v_idle_acquire_rows := NULL;
    END IF;
    -- 196 (#1226): the claim reads this instance's registration and never writes it. It used to
    -- repair a missing or stale row here, inside the claim's transaction, and the row stayed locked
    -- until the claim committed: record_heartbeat for this instance then waited for the whole claim,
    -- so a slow claim made the heartbeat late and a late heartbeat made the next claim hold the row.
    --
    -- A missing or stale row is reported instead, and the caller registers in a statement of its
    -- own once this claim has returned. On the healthy path that costs one primary-key probe, the
    -- one the repair's guard already paid.
    IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_service_instances
      WHERE instance_id = p_instance_id
        AND last_heartbeat_at >= v_stale_cutoff
    ) THEN
      RAISE NOTICE 'whizbang.instance_registration_stale=true';
    END IF;

    -- The rank counts this instance as live whatever its row says: it is running this claim. Ranked
    -- as absent it would fall to a solo rank and widen its claim over every peer's partitions until
    -- its registration lands. Peers are ranked as before, on a fresh heartbeat row. Their view of
    -- this instance catches up when the caller registers, which is no later than it did when the
    -- repair ran here: a write inside this transaction was invisible to them until the claim
    -- committed anyway.
    SELECT ranked.instance_rank, ranked.active_instance_count INTO v_rank, v_count
    FROM (
      SELECT live.instance_id,
             (ROW_NUMBER() OVER (ORDER BY live.instance_id) - 1)::INTEGER AS instance_rank,
             COUNT(*) OVER ()::INTEGER AS active_instance_count
      FROM (
        SELECT si.instance_id
        FROM __SCHEMA__.wh_service_instances si
        WHERE si.last_heartbeat_at >= v_stale_cutoff
        UNION
        SELECT p_instance_id
      ) live
    ) ranked
    WHERE ranked.instance_id = p_instance_id;

    -- v0.683, per-inner-function guards. The existing v_has_any_work short-circuit
    -- (top of the function) only fires when ALL four queues are empty. Under steady
    -- import load, that's rare, but the typical pattern is "one queue has work,
    -- the others don't." Without per-function guards, claim_work paid the full
    -- claim_orphaned_*/emit_chain scan cost on every call regardless. Each guard
    -- uses an existing partial index (idx_{outbox,inbox}_unprocessed_claiming WHERE
    -- processed_at IS NULL, etc.) so the EXISTS probe is sub-millisecond. Behavior
    -- is preserved: if a guard returns false, the corresponding inner function had
    -- no rows to claim anyway, so skipping its scan is a pure win.

    -- Claim orphaned / unowned outbox work, only if any outbox row is unprocessed
    -- AND either unowned or has an expired lease (the orphan predicate matched by
    -- claim_orphaned_outbox's WHERE clause).
    -- 158: two index probes, not one scan. A single predicate over "unowned or expired" has to examine
    -- every pending row to prove there is none, which on a busy instance is its whole holdings on
    -- every poll. Unowned rows are found under instance_id IS NULL through the outstanding-by-instance
    -- index (123); expired leases are the head of the lease-expiry index (158), so an instance whose
    -- leases are all live proves it at the first entry.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE processed_at IS NULL
        AND coalesce_group IS NULL  -- 115: pending singles are never orphan-claimable
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND coalesce_group IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- Bounded for the same reason as the inbox call below: without a limit this acquires the whole
      -- eligible backlog in one statement, and the caller's claim limit never reaches the flood.
      -- 171: bounded by the OUTBOX row bound, not the stream window. The window is a stream count
      -- that starts at its floor and grows only on inbox evidence; as a row cap it leased one row on
      -- each of the oldest streams per claim, so a backlog on a few long streams drained one row per
      -- stream per cycle. The window still chooses how many streams move (the heads); each of them
      -- then leases a run of its next rows. Counted, so a full acquisition can say so below.
      -- 202: the ledger is deferred (the last argument) and collected for the one pass below.
      SELECT count(*)::INTEGER,
             v_ledger_streams || COALESCE(array_agg(a.stream_id), '{}'),
             v_ledger_partitions || COALESCE(array_agg(a.partition_number), '{}')
      INTO v_outbox_acquired, v_ledger_streams, v_ledger_partitions
      FROM __SCHEMA__.claim_orphaned_outbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        v_outbox_bound, GREATEST(COALESCE(p_outbox_run_length, 1), 1), p_max_streams,
        TRUE
      ) a;
    END IF;

    -- Claim orphaned / unowned inbox work, same predicate shape.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- p_max_streams bounds ACQUISITION here, not just the re-emission below. Omitting it let this
      -- call lease the entire eligible backlog in one statement, charging an attempt to every row ,
      -- while the caller's claim window and outstanding budget bounded only what came back out of
      -- eligible_inbox. Both throttles sat downstream of the flood, so neither could ever have held.
      -- 145: acquisition has its own ROW bound. p_max_streams is a stream count; handing it to the
      -- acquisition as a row cap turned fat streams into one row per cycle (#714). The caller passes
      -- the rows it can afford; a caller that does not is bounded by the stream count as before.
      -- 202: the ledger is deferred (the last argument) and collected for the one pass below.
      SELECT v_ledger_streams || COALESCE(array_agg(a.stream_id), '{}'),
             v_ledger_partitions || COALESCE(array_agg(a.partition_number), '{}')
      INTO v_ledger_streams, v_ledger_partitions
      FROM __SCHEMA__.claim_orphaned_inbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        COALESCE(p_max_rows, p_max_streams), p_allow_steal,
        -- 167: the admission decided once per poll, carrying its size. 170: this is where the band
        -- is withheld; whatever is acquired is re-offered below in full, so a leased idle row can
        -- never be held unoffered.
        v_idle_acquire_rows,
        TRUE
      ) a;
    END IF;

    -- Claim orphaned perspective events, same predicate shape on
    -- wh_perspective_events.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- 145 (#719): perspective ACQUISITION has its own bound. The caller passes 0 while its drain channel is
      -- above its cap, so a perspective backlog is drained before more is leased; re-emission of held work
      -- below stays on p_max_streams so the drain keeps moving.
      -- 160: the perspective acquisition gets a ROW bound as well as its stream bound. It used to
      -- take a batch of streams and lease every pending event of each, so one poll leased whatever
      -- a consumer's streams happened to hold and cost accordingly. The caller's own budget is used
      -- when it passes one; otherwise a multiple of the batch, because handing a stream count
      -- straight to an acquisition as a row cap is 145's #714 mistake in reverse.
      -- 202: the ledger is deferred (the last argument) and collected for the one pass below.
      SELECT v_ledger_streams || COALESCE(array_agg(a.stream_id), '{}'),
             v_ledger_partitions || COALESCE(array_agg(a.partition_number), '{}')
      INTO v_ledger_streams, v_ledger_partitions
      FROM __SCHEMA__.claim_orphaned_perspective_events(
        p_instance_id, v_lease_expiry, v_now, COALESCE(p_max_perspective_streams, p_max_streams), v_rank, v_count,
        COALESCE(p_max_rows, GREATEST(COALESCE(p_max_perspective_streams, p_max_streams), 1) * 8),
        TRUE
      ) a;
    END IF;

    -- Claim orphaned receptor work, wh_receptor_processing uses completed_at
    -- (not processed_at) for the "is done" semantic.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_receptor_processing
      WHERE completed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_receptor_work(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now
      );
    END IF;

    -- 202 (#1256): the stream ledger for every stream the acquisitions above leased work on, in ONE stream_id
    -- pass. Each acquisition used to write its own streams' rows before the next one ran, so a claim took its
    -- ledger rows in up to three ascending runs, and two claims could each hold a row the other's later
    -- acquisition needed (rule 5 of the work-table lock order, ai-docs/load-under-bulk-import.md). Written
    -- here, after every queue row this claim locks, it also keeps the work-table order (162): the ledger last.
    --
    -- What a later acquisition saw changes in one case only: a stream an earlier acquisition in this same
    -- call took for the first time is not yet this instance's in the ledger when the later one ranks its
    -- rows, so they are claimed by partition as any unowned stream's are, and by ownership from the next
    -- call on. The emit chain below still reads the ledger after this pass, as it did.
    IF cardinality(v_ledger_streams) > 0 THEN
      PERFORM __SCHEMA__.wh_lease_claimed_streams(p_instance_id, v_now, v_lease_expiry, v_ledger_streams, v_ledger_partitions);
    END IF;

    -- Back-fill wh_event_store + wh_perspective_events for inbox events claimed above.
    -- Replaces legacy process_work_batch Phase 4.5B + 4.6 self-healing, ensures that by the
    -- time an inbox event row reaches InboxDispatchWorker, its event_store row exists and
    -- perspective_events have been created so PerspectiveWorker can pick them up.
    --
    -- v0.683 guard: only call when this instance owns at least one unprocessed
    -- inbox event row with a stream_id. emit_chain's own internal NOT EXISTS
    -- check against wh_event_store filters out already-emitted rows; we don't
    -- repeat that check here because a production measurement
    -- showed the wrapping NOT EXISTS predicate at 42 ms mean (~4-5% of total
    -- DB time) under heavy inbox load, overwhelming the savings from skipping
    -- emit_chain. The simpler EXISTS uses idx_inbox_instance_lease and is
    -- sub-millisecond. The handler-delay backlog scenario where every event_id
    -- is already in wh_event_store is rare and is more appropriately addressed
    -- on the handler side (composite events) than in the work-pump.
    -- 158: only rows the chain has never read (idx_inbox_chain_pending). A holder in steady state
    -- has none, and the poll skips the chain without touching a held row.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state i
      WHERE i.instance_id = p_instance_id
        AND i.processed_at IS NULL
        AND i.is_event = true
        AND i.stream_id IS NOT NULL
        AND i.chain_emitted_at IS NULL
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__._emit_event_store_chain_for_inbox(p_instance_id, v_lease_expiry, v_now, p_partition_count);
    END IF;

    -- Return outbox work owned by this instance, oldest first, a batch of it.
    -- 158: a walk of idx_outbox_held_arrival that stops at the batch. The previous shape ranked every
    -- held row per stream with a window function it never read and sorted them all before taking the
    -- batch, so the poll priced itself by the holdings: a busy instance holds thousands of leased rows
    -- and polls several times a second. Same rows in the same order, ties broken by message id.
    RETURN QUERY
    -- Per-stream-drain projection (Phase H step 5b): claim_work returns stream_ids only for
    -- outbox. The OutboxDrainWorker consumes WorkBatch.OutboxStreamIds and pulls full payloads
    -- on demand via fetch_outbox_batch. Body columns are NULL, keeps the bytes-on-the-wire
    -- proportional to the active stream set, not the leased-row count × payload size.
    --
    -- 171: one row per held STREAM, a batch of streams. The drain reads stream ids, and a claim now
    -- leases runs, so returning held rows let one stream's run fill the batch and hide every other
    -- stream this instance holds from the drain. The held rows are walked exactly as before (the
    -- same predicate and order, a Sort + Limit over this instance's leased rows, 166), bounded by
    -- the larger of the stream window and the outbox row bound, and folded to each stream's most
    -- urgent row. A row with no routable stream id is its own stream, as the drain keys it.
    SELECT
      c_source_outbox               AS source,
      h.message_id                  AS work_id,
      h.stream_id                   AS work_stream_id,
      h.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      h.status,
      h.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      h.priority                    AS priority,   -- 166: what this row was scheduled on
      NULL::TIMESTAMPTZ             AS received_at
    FROM (
      SELECT DISTINCT ON (held.stream_key) held.*
      FROM (
        SELECT o.message_id, o.stream_id, o.partition_number, o.status, o.attempts, o.priority, o.created_at,
               CASE WHEN o.stream_id IS NULL OR o.stream_id = __EMPTY_UUID__::uuid
                    THEN o.message_id ELSE o.stream_id END AS stream_key
        FROM __SCHEMA__.wh_outbox o
        WHERE o.instance_id = p_instance_id
          AND o.processed_at IS NULL
          AND o.coalesce_group IS NULL  -- 115: pending singles; the index predicate
          AND o.lease_expiry > v_now
          AND o.published_at IS NULL  -- skip debug-mode forensic rows (production never sets this, row is deleted)
          AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
        -- 166: priority first, arrival as the tiebreak. Every other queue in the path honors the
        -- number the producer declared -- the inbox through its band lanes, the read-model claim by
        -- ordering on it -- and this one selected by arrival alone, so a reply someone was waiting for
        -- published behind whatever bulk work happened to be queued first.
        --
        -- On the NUMBER, not the band, because that is what the priority contract says: scheduling
        -- works on the bucket, and inside a bucket the number orders work.
        --
        -- This costs no index HERE. The branch is already narrowed to one instance's leased rows by
        -- idx_outbox_outstanding_by_instance, and it already sorts, because that index carries
        -- lease_expiry rather than created_at. Measured on a deployed database the plan is identical
        -- either way (Sort, cost 1.12..1.13), so priority joins an existing sort key. The ACQUISITION
        -- side is the one that needed idx_outbox_pending_priority_arrival (166); that restraint
        -- matters because the outbox is the hottest write path during a bulk load.
        ORDER BY o.priority, o.created_at, o.message_id
        LIMIT GREATEST(p_max_streams, v_outbox_bound, 0)
      ) held
      ORDER BY held.stream_key, held.priority, held.created_at, held.message_id
    ) h
    ORDER BY h.priority, h.created_at, h.message_id
    LIMIT p_max_streams;

    -- v0.661: track this category's RETURN QUERY rowcount so the drain-mode
    -- hint at function end can be derived from ROW_COUNT instead of a fresh
    -- COUNT(*) scan. See drain-mode hint block below.
    GET DIAGNOSTICS v_outbox_rows = ROW_COUNT;

    -- Return inbox work owned by this instance: one row per lane a held stream appears in, a batch
    -- of them.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_inbox_held_lanes one index-only probe per stream, lane by lane in the
    -- order the batch is ordered: most urgent bucket first (150), commands before events inside a
    -- bucket (145). Inside a lane the fresh and retried classes are walked side by side (126), each
    -- able to take the whole batch, because the share must not hold a slot empty when the other
    -- class is: what apportions them is the interleave below, not the walks. A lane's walk starts at
    -- a stream id drawn per poll and wraps once, so a lane larger than the batch does not enumerate
    -- the same streams on every poll; a lane no larger than the batch is enumerated whole. One step
    -- lands on one stream, so a lane costs exactly the streams it enumerates.
    --
    -- One row stands for a held stream in a lane: the oldest row the instance holds of it there,
    -- carrying that row's number, arrival and attempts. A stream whose rows span lanes is returned
    -- once per lane, exactly as before, and the batch hooks fold those rows to the stream's most
    -- urgent number and oldest arrival (ClaimedInboxStreamFolder), unchanged. What is no longer
    -- returned is a stream's further rows inside one lane: the drain consumes stream ids and pulls a
    -- stream's rows on demand (fetch_inbox_batch), so they carried nothing a caller read, and ranking
    -- them is what cost the poll. A stream met in both classes of a lane stands as its retried row,
    -- which is its head -- an attempt is charged at the head and everything older is processed, so a
    -- stream with a retried row has a retried head, and stream FIFO means the fresh rows behind it
    -- cannot dispatch anyway (126). The previous shape ranked every held row with three window
    -- functions on every poll and fetched every held row's heap page to do it, and a busy instance
    -- holds thousands of rows and polls several times a second.
    --
    -- GREATEST drops a NULL, so a NULL batch leaves nothing to fill and the re-offer returns nothing.
    -- Nothing passes NULL (the parameter defaults to 1000 and the coordinators pass an integer), and
    -- returning nothing is the safe reading of "no batch size": the previous LIMIT NULL meant no
    -- bound at all, which is the failure this migration exists to stop.
    v_remaining := GREATEST(p_max_streams, 0);
    <<inbox_lanes>>
    FOR v_bucket IN 0..3 LOOP
      -- 170: the idle band (bucket 3) is re-offered like every other bucket. These walks visit only
      -- rows this instance already holds under a live lease, so there is nothing left to withhold:
      -- the lease is taken and the attempt charged. 167 skipped the bucket unless the band was
      -- admitted, and acquisition takes idle commands and lapsed idle rows regardless, so a held row
      -- could sit here unoffered, lapse, be re-acquired and charged again, for ever -- never reaching
      -- the dispatcher that enforces the attempt ceiling. Admission bounds ACQUISITION
      -- (claim_orphaned_inbox's p_idle_max_rows); it never hides what is already held.
      FOREACH v_is_event IN ARRAY ARRAY[false, true] LOOP
        EXIT inbox_lanes WHEN v_remaining <= 0;
        v_start := gen_random_uuid();
        RETURN QUERY
        WITH RECURSIVE lanes AS (
          -- Two walks side by side, one per class, seeded at the drawn point. A seed is a bound and
          -- not a candidate.
          SELECT c.fresh_lane, v_start AS stream_id, false AS wrapped, true AS is_seed, 0 AS steps,
                 NULL::INTEGER AS priority, NULL::UUID AS message_id, NULL::TIMESTAMPTZ AS received_at,
                 NULL::INTEGER AS attempts, NULL::INTEGER AS partition_number, NULL::INTEGER AS status
          FROM (VALUES (true), (false)) AS c(fresh_lane)
          UNION ALL
          SELECT h.fresh_lane, n.stream_id, n.wrapped, false, h.steps + 1,
                 n.priority, n.message_id, n.received_at, n.attempts, n.partition_number, n.status
          FROM lanes h
          CROSS JOIN LATERAL (
            -- The lane's next stream after the current one, up to the drawn point once wrapped. The
            -- index is keyed (holder, bucket, kind, class, stream, arrival, id) and covers every
            -- column below, so this is one index-only probe: it lands on the stream's oldest row in
            -- the lane and the next stream is one probe away. The upper bound is written as a CASE
            -- rather than an OR so it stays an index condition; as a filter the walk would read past
            -- the drawn point instead of stopping at it.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, h.wrapped AS wrapped
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND i.stream_id > h.stream_id
               AND i.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            UNION ALL
            -- ...or, past the lane's last stream, its first one: the walk wraps, once.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, true
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND NOT h.wrapped
               AND i.stream_id <= v_start
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            LIMIT 1
          ) n
          -- One step, one stream: each class may take the whole batch, and the interleave apportions.
          WHERE h.steps < v_remaining
        ),
        inbox_candidates AS (
          -- A stream met in both classes stands as its retried row: that row is its head (126).
          SELECT DISTINCT ON (l.stream_id)
                 l.stream_id, l.priority, l.message_id, l.received_at, l.attempts,
                 l.partition_number, l.status
          FROM lanes l
          WHERE NOT l.is_seed
          ORDER BY l.stream_id, l.fresh_lane
        ),
        ranked_inbox AS (
          SELECT c.stream_id, c.priority, c.message_id, c.received_at, c.attempts,
                 c.partition_number, c.status,
                 (c.attempts = 0) AS is_fresh,
                 ROW_NUMBER() OVER (PARTITION BY (c.attempts = 0) ORDER BY c.received_at, c.message_id) AS class_rank
          FROM inbox_candidates c
        ),
        ordered_inbox AS (
          SELECT ri.stream_id, ri.priority, ri.message_id, ri.received_at, ri.attempts,
                 ri.partition_number, ri.status
          FROM ranked_inbox ri
          ORDER BY
            -- 126: fresh-head streams receive p_fresh_share of the batch, retried-head streams the
            -- remainder, each class in arrival order; an empty class hands its share to the other,
            -- because the key only competes candidates that exist.
            CASE WHEN ri.is_fresh
                 THEN (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(LEAST(p_fresh_share, 1.0), 0.000001)
                 ELSE (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(1.0 - LEAST(p_fresh_share, 1.0), 0.000001)
            END,
            ri.received_at,
            ri.message_id
          LIMIT v_remaining
        )
        -- Per-stream-drain projection (Phase H step 5d): inbox follows outbox into stream-ids-only.
        -- InboxDrainWorker reads stream_ids off IInboxDrainChannel and pulls payloads on demand
        -- via fetch_inbox_batch. Body columns are NULL -- keeps claim_work's bytes-on-the-wire
        -- proportional to active stream count.
        SELECT
          c_source_inbox                AS source,
          oi.message_id                 AS work_id,
          oi.stream_id                  AS work_stream_id,
          oi.partition_number,
          NULL::VARCHAR(200)            AS destination,
          NULL::VARCHAR(500)            AS message_type,
          NULL::VARCHAR(500)            AS envelope_type,
          NULL::TEXT                    AS message_data,
          NULL::JSONB                   AS metadata,
          oi.status,
          oi.attempts,
          false                         AS is_newly_stored,
          false                         AS is_orphaned,
          NULL::VARCHAR(200)            AS perspective_name,
          oi.priority                   AS priority,
          oi.received_at                AS received_at
        FROM ordered_inbox oi;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_remaining := v_remaining - v_rows;
      END LOOP;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_inbox_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- Return receptor work owned by this instance.
    -- Receptor work uses `id` as the work_id (not message_id) and `completed_at` as the "done" marker.
    -- Most fields are NULL, receptors carry their state in dedicated columns the worker reads
    -- directly via the work_id; the row here just signals "this receptor needs attention".
    RETURN QUERY
    SELECT
      'receptor'::VARCHAR(20)       AS source,
      rp.id                         AS work_id,
      rp.stream_id                  AS work_stream_id,
      rp.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      rp.status::INTEGER,
      rp.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      NULL::INTEGER                 AS priority,
      NULL::TIMESTAMPTZ             AS received_at
    FROM __SCHEMA__.wh_receptor_processing rp
    WHERE rp.instance_id = p_instance_id
      AND rp.lease_expiry > v_now
      AND rp.completed_at IS NULL
    LIMIT p_max_streams;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_receptor_rows = ROW_COUNT;

    -- Return perspective work as one row per distinct stream owned by this instance, most urgent
    -- bucket first, oldest event first within a bucket, a batch of streams.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_perspective_held_lanes one index-only probe per stream, bucket by bucket,
    -- most urgent first; the first entry of a stream in its bucket is its oldest held event there,
    -- which is what orders the streams within the bucket. The walk starts at a stream id drawn per
    -- poll and wraps once, so a bucket larger than the batch does not enumerate the same streams on
    -- every poll, and one no larger than the batch is enumerated whole. One step lands on one stream,
    -- so a bucket costs exactly the streams it enumerates. The previous shape aggregated every held
    -- event per poll to put streams with a hundred or fewer pending events ahead of larger ones; that
    -- tier guarded a batch of rows against one large stream, and the drain has been per stream with an
    -- unbounded channel since Phase H, so a large stream no longer displaces small ones.
    v_remaining := GREATEST(p_max_streams, 0);
    FOR v_bucket IN 0..3 LOOP
      -- 167: the idle band is bucket 3 and is skipped unless time or quiet admitted it.
      CONTINUE WHEN v_bucket = 3 AND NOT v_idle_admitted;
      IF v_bucket = 3 AND v_idle_capped THEN
        -- admitted by trickle, not by quiet: a slice, never a burst.
        v_remaining := LEAST(v_remaining, GREATEST(p_idle_trickle_slice, 0));
      END IF;
      EXIT WHEN v_remaining <= 0;
      v_start := gen_random_uuid();
      RETURN QUERY
      WITH RECURSIVE lane AS (
        -- Seeded at the drawn point; the seed is not a candidate.
        SELECT v_start AS stream_id, NULL::UUID AS event_id, false AS wrapped, true AS is_seed, 0 AS steps
        UNION ALL
        SELECT n.stream_id, n.event_id, n.wrapped, false, h.steps + 1
        FROM lane h
        CROSS JOIN LATERAL (
          -- The bucket's next stream after the current one, up to the drawn point once wrapped...
          (SELECT pe.stream_id, pe.event_id, h.wrapped AS wrapped
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND pe.stream_id > h.stream_id
             AND pe.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          UNION ALL
          -- ...or, past the bucket's last stream, its first one: the walk wraps.
          (SELECT pe.stream_id, pe.event_id, true
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND NOT h.wrapped
             AND pe.stream_id <= v_start
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          LIMIT 1
        ) n
        WHERE h.steps < v_remaining
      )
      SELECT
        'perspective_stream'::VARCHAR(20) AS source,
        NULL::UUID                        AS work_id,
        l.stream_id                       AS work_stream_id,
        NULL::INTEGER                     AS partition_number,
        NULL::VARCHAR(200)                AS destination,
        NULL::VARCHAR(500)                AS message_type,
        NULL::VARCHAR(500)                AS envelope_type,
        NULL::TEXT                        AS message_data,
        NULL::JSONB                       AS metadata,
        0::INTEGER                        AS status,
        0::INTEGER                        AS attempts,
        false                             AS is_newly_stored,
        false                             AS is_orphaned,
        NULL::VARCHAR(200)                AS perspective_name,
        NULL::INTEGER                     AS priority,
        NULL::TIMESTAMPTZ                 AS received_at
      FROM lane l
      WHERE NOT l.is_seed
      ORDER BY l.event_id, l.stream_id
      LIMIT v_remaining;

      GET DIAGNOSTICS v_rows = ROW_COUNT;
      v_remaining := v_remaining - v_rows;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_perspective_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- 130 doorbell debounce: finding work stamps this instance's watermark, the signal
    -- producers use to suppress redundant notifies while this drainer is awake. Rides
    -- inside the claim (zero extra round trips) and skips the empty case so the
    -- empty-call short-circuit's ~1 ms idle floor is untouched.
    -- 140: the stamp never waits (issue #699). The store's debounce touches the same rows inside
    -- its own transaction; under a burst the two met in opposite orders with the inbox row locks
    -- and deadlocked. A watermark row another session holds is skipped this tick: the stamp is a
    -- freshness hint and the next tick re-stamps it. Rows that do not exist yet are created; rows
    -- that exist and are free are stamped in place.
    WITH kinds AS (
      SELECT k.kind
      FROM (VALUES (c_source_outbox), (c_source_inbox), (__CATEGORY_PERSPECTIVE__)) AS k(kind)
    WHERE (k.kind = c_source_outbox AND v_outbox_rows > 0)
       OR (k.kind = c_source_inbox AND (v_inbox_rows > 0 OR v_receptor_rows > 0))
       -- 133: the perspective watermark must reflect DRAINABLE progress, not merely a
       -- claimed Stored-but-fence-held row. claim_work returns a perspective_stream row for
       -- any leased unprocessed perspective_event, but a row whose underlying event is still
       -- unstamped (commit_sequence IS NULL, held by the per-database ordering fence) cannot
       -- be surfaced by the fetch gate, the drainer spins with no progress. Arming the
       -- watermark for it lets the doorbell debounce (130/131) suppress the fence-clearing
       -- stamp's make-up ring, stranding visibility on the adaptive poll cap (issue #677).
       -- The EXISTS runs at most once: the k.kind guard short-circuits it away for the
       -- outbox/inbox VALUES rows.
       -- 158: the question is asked of a batch of the rows leased longest, not of every held row.
       -- Over all of them it was priced by the holdings whenever the fence held (#677 is exactly
       -- that state), joining every leased row to the event store per poll. A held row whose event is
       -- stamped is found among the oldest leases if it is found at all; when none of them is, the
       -- watermark stays unarmed and the make-up ring goes through, which is the safe direction.
       OR (k.kind = __CATEGORY_PERSPECTIVE__ AND v_perspective_rows > 0 AND EXISTS (
             SELECT 1
             FROM (
               SELECT pe.event_id
               FROM __SCHEMA__.wh_perspective_events pe
               WHERE pe.instance_id = p_instance_id
                 AND pe.lease_expiry > v_now
                 AND pe.processed_at IS NULL
               ORDER BY pe.lease_expiry
               LIMIT GREATEST(p_max_streams, 1)
             ) held
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = held.event_id
             WHERE es.commit_sequence IS NOT NULL))
    ),
    lockable AS (
      SELECT ns.instance_id, ns.payload_kind
      FROM __SCHEMA__.wh_notify_state ns
      JOIN kinds ON kinds.kind = ns.payload_kind
      WHERE ns.instance_id = p_instance_id
      FOR UPDATE OF ns SKIP LOCKED
    ),
    stamped AS (
      UPDATE __SCHEMA__.wh_notify_state ns
      SET last_work_at = NOW()
      FROM lockable
      WHERE ns.instance_id = lockable.instance_id AND ns.payload_kind = lockable.payload_kind
      RETURNING ns.payload_kind
    )
    INSERT INTO __SCHEMA__.wh_notify_state (instance_id, payload_kind, last_work_at)
    SELECT p_instance_id, kinds.kind, NOW()
    FROM kinds
    WHERE NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_notify_state ns2
      WHERE ns2.instance_id = p_instance_id AND ns2.payload_kind = kinds.kind)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    -- Drain-mode hint: if any of the four return categories filled its LIMIT
    -- (rows == p_max_streams), there's likely more eligible work for this
    -- instance, RAISE NOTICE so the C# claim worker skips its wait and
    -- re-polls immediately. Survives pgbouncer (protocol message, not a
    -- session-state thing).
    --
    -- v0.661 forensic (gate.hold_duration_ms histogram during a consumer's
    -- draft-job import): the prior implementation ran four separate
    -- COUNT(*) queries here (one per category, plus a COUNT(DISTINCT
    -- stream_id) on wh_perspective_events). Under import load with millions
    -- of leased rows per instance, those counts dominated claim_work hold
    -- time, ClaimWorkAsync at p99 5031 ms / avg 128 ms. We don't need
    -- exact counts; we only need to know whether ANY category filled its
    -- LIMIT. ROW_COUNT after each RETURN QUERY gives us that for free.
    IF v_outbox_rows = p_max_streams
       OR v_inbox_rows = p_max_streams
       OR v_receptor_rows = p_max_streams
       OR v_perspective_rows = p_max_streams THEN
      RAISE NOTICE 'whizbang.has_more=true';
    END IF;

    -- 171: the outbox acquisition leased its whole row bound, so the backlog it was taken from is
    -- at least that large again. Distinct from has_more above, which also fires when this instance
    -- merely HOLDS a full batch (a re-offer), and a claim loop that re-claimed on a re-offer would
    -- spin. This one is only ever raised by NEW work, so the claim loop may claim again at once;
    -- what bounds that loop is the row bound itself, which the caller sizes from what it holds.
    IF v_outbox_bound > 0 AND v_outbox_acquired >= v_outbox_bound THEN
      RAISE NOTICE 'whizbang.outbox_acquisition_full=true';
    END IF;
  END;

  RETURN;
END;
-- 157: custom plans for the poll and everything it calls. The queue tables are empty between loads,
-- and a session that polled while they were empty kept generic plans made for empty tables; once
-- the tables filled those plans scanned them whole, nested, on every poll, until the next analyze
-- invalidated them. Measured: a poll that takes well under a second with fresh plans did not finish
-- inside the command timeout with the empty-table plans. Planning per call costs milliseconds.
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.claim_work(UUID, TEXT, TEXT, INTEGER, INTEGER, INTEGER, INTEGER, DOUBLE PRECISION, INTEGER, BOOLEAN, INTEGER, BOOLEAN, INTERVAL, INTEGER, INTERVAL, INTEGER, INTEGER) IS
  'The claim poll: acquires orphaned and unowned work for an instance (outbox, inbox, perspective events, receptor '
  'work) and returns what it holds. 196: never writes the caller''s registration. 202: the acquisitions defer the '
  'stream ledger, and it is written once for every stream they leased (wh_lease_claimed_streams), in one stream_id '
  'pass after the last queue row is locked.';
