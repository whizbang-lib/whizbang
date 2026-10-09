-- Migration: 200_ClaimLocksItsLedgerRowsInOnePass.sql
-- Date: 2026-10-09
-- Description: The outbox acquisition takes its stream-ledger rows in one stream_id order (#1238).
--
--              claim_orphaned_outbox (inside claim_work) writes a wh_active_streams row for every
--              stream it leased rows on, along two paths (PR #227): REFRESH for the streams this
--              instance already owns under a live lease, and PIN (INSERT ... ON CONFLICT DO UPDATE)
--              for the rest. Each path took its rows in stream_id order (172), but the refreshes all
--              ran first and the pins after, so the statement as a whole locked its ledger rows in
--              two ascending runs, not one. Two claims where each refreshes a stream the other pins
--              then wait on each other:
--                claim A: refresh s3, then pin s2 (waits for B)
--                claim B: refresh s2, then pin s3 (waits for A)
--
--              A claim pins a stream another instance owns only when it sees that owner as not live,
--              and then it ranks alone and its window admits every stream. A fleet whose heartbeat rows
--              all go stale at once, behind a pooler that hides the application name, meets exactly
--              that. Reproduced by four instances that never register: 98 deadlocks in 361 rounds,
--              each one sampled reported at claim_work's claim_orphaned_outbox call.
--
--              The ledger section now takes its rows in one pass:
--                * ledger_locked locks every existing ledger row of the claimed streams, whoever owns
--                  it, ORDER BY stream_id FOR UPDATE. It is the only place the statement waits for an
--                  existing ledger row, so it waits in the one order (172's rule 2);
--                * the refresh and the takeover then update rows this statement already holds, so
--                  neither can wait. The refresh is still a plain UPDATE of the streams this instance
--                  owns, as PR #227 requires; the takeover applies the pin's ownership rules (148)
--                  unchanged to the rest;
--                * only streams with no ledger row are inserted, after the lock pass and in stream_id
--                  order, ON CONFLICT DO NOTHING: an insert waits only for another claim's uncommitted
--                  insert of the same stream, and a row that appeared since the snapshot is left to
--                  whoever created it rather than locked out of order.
--              The rows locked are the ones the two paths locked before, the outbox section and the
--              table order (wh_outbox, then wh_active_streams) are unchanged, and each ledger row is
--              still found by one primary-key probe per claimed stream.
--
-- Dependencies: 194 (claim_orphaned_outbox, copied verbatim apart from the ledger section), 172 (the
--               lock order), 148 (the ownership rules the takeover keeps)
-- Objects: claim_orphaned_outbox
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- The arity differs across the corpus (171 added two parameters), so every definition site clears the
-- overloads first (OverloadGuardsCoverEveryDefinitionSiteTests). The signature is 171's, unchanged.
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_outbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_BacklogOwnedByALivePeer_CostFollowsTheBatchNotTheBacklogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_AnExpiredLeaseBehindAPeerBacklog_IsStillReclaimedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxAcquisitionWindowSqlTests.cs:ClaimOrphanedOutbox_UnownedStreamInsideTheWindow_IsTakenFromItsHeadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ClaimOrphanedOutbox_RefreshesTheStreamLedgerInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ClaimOrphanedOutbox_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ConcurrentClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
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
  p_max_heads INTEGER DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
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
    FROM (SELECT DISTINCT c.c_stream_id FROM claimed c WHERE c.c_stream_id IS NOT NULL) cs
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
        AND NOT EXISTS (
          SELECT 1 FROM ledger_locked ll WHERE ll.locked_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO NOTHING
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
  'Acquires unowned/abandoned pending outbox rows for an instance (see 024 for the ownership rationale, 115 for the '
  'coalesce-aware selection, 148 for the stream lease). 171: the oldest claimable rows (at most p_max_heads) choose '
  'the streams that move, and each leases up to p_run_length of its next consecutive rows as a prefix, within '
  'p_max_rows in total; a run of 1 is the previous behavior. 172: a run row another session leased since the snapshot '
  'is not locked. 194: the heads are chosen from a window of eight times as many oldest claimable rows per lane '
  '(unowned by arrival, expired leases by expiry), so a poll costs a multiple of its batch whatever the backlog holds. '
  '200: the stream ledger rows of the claimed streams are locked in one stream_id pass, whoever owns them, before they '
  'are refreshed or taken over, and streams with no row are inserted after that pass in the same order.';
