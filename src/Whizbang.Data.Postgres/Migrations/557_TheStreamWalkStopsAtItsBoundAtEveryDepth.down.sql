-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 557_TheStreamWalkStopsAtItsBoundAtEveryDepth.sql (#2523): claim_orphaned_perspective_events as 536
-- defined it, copied whole. Its results are the same; only the plans it leaves the planner free to choose come back.
-- Constants: the double-underscore tokens in this file are substituted from Migrations/constants.txt at apply time.

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_perspective_events: 536's body, copied whole.
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
