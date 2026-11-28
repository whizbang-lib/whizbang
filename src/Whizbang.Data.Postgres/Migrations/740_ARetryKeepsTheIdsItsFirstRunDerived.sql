-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 740_ARetryKeepsTheIdsItsFirstRunDerived.sql
-- Date: 2026-11-27
-- Description: An inbox row records the derivation scheme its first claim ran under, and every retry runs under it
--              (#2633).
--
--              A receptor a handling invokes locally now runs in a handling of its own, so the ids it derives, sends
--              and emits move once, and so do the outer body's send ordinals after the call. A row that started on a
--              build before that change and is retried on one after it must keep its first run's ids, or the retry's
--              copies are new messages instead of duplicates the stores absorb.
--
--                wh_inbox_state.derivation_scheme  SMALLINT, NULL until a row's first claim records it: 0 is the scheme
--                                 of every build before #2633 (the inner receptor shares the caller's handling), 1 the
--                                 inner receptor's own handling. Nullable with no default, so adding it rewrites nothing.
--                claim_orphaned_inbox  copied from its last word (566), with p_derivation_scheme as a new last
--                                 parameter: the claim's existing UPDATE stamps it on a row whose attempts are 0 before
--                                 its bump and that records nothing, its first claim. No write of its own. NULL stamps
--                                 nothing: a build before 740 passes none, so a row it claims first records none, and
--                                 reads as 0 on every later claim, since its attempts are no longer 0.
--                claim_work       copied from its last word (598), with p_derivation_scheme as a new last parameter
--                                 handed to claim_orphaned_inbox. Both keep every earlier argument and its default, so a
--                                 build before 740 calls them unchanged.
--                release_unstarted_leases (563), release_unprocessed_inbox (162)  copied, with one delta: a refund that
--                                 takes a row back to no claim (attempts 1 to 0) un-records the scheme, because the
--                                 refunded claim never ran the row, so the row's real first claim records its own.
--                fetch_inbox_batch_with_derivation_scheme  fetch_inbox_batch_in_stream_order (576) verbatim, returning
--                                 each row's recorded scheme (0 when none). A read only. A new name, because a
--                                 function's result never changes under its name (454); 576's function stays for a
--                                 build before this one.
--
--              A lease an instance renews without a claim (462) keeps its stamp: the same build renews it. A build
--              before 740 that retries a row a later build first claimed derives its own build's ids: a build cannot
--              honor a scheme it does not know.
-- Reversal: reversible (the down file restores 566's claim_orphaned_inbox, 598's claim_work, 563's and 162's release
--           functions verbatim and drops the fetch and the column; a recorded scheme is lost with the column, and a row
--           retried after the reversal runs under the shared scheme, the behavior of the build the reversal returns to)
-- Dependencies: 162 (wh_inbox_state, release_unprocessed_inbox), 563 (release_unstarted_leases), 566
--               (claim_orphaned_inbox), 576 (fetch_inbox_batch_in_stream_order), 598 (claim_work)
-- Objects: wh_inbox_state.derivation_scheme, claim_orphaned_inbox, claim_work, release_unstarted_leases,
--          release_unprocessed_inbox, fetch_inbox_batch_with_derivation_scheme

ALTER TABLE __SCHEMA__.wh_inbox_state ADD COLUMN IF NOT EXISTS derivation_scheme SMALLINT;

COMMENT ON COLUMN __SCHEMA__.wh_inbox_state.derivation_scheme IS
'740 (#2633): the derivation scheme the row''s first claim ran under, which every retry runs under: 0 a locally invoked receptor shares its caller''s handling (every build before #2633), 1 it runs in a handling of its own. NULL until the first claim records it; a row a build before the column claimed first reads as 0.';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_inbox: copied from its last word (566), with the 740 delta: the first claim's scheme.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_inbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimDerivationSchemeSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_AnInteractiveStream_IsClaimedAheadOfOlderStandardStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_AStreamWithAnInteractiveRowBehindStandardRows_IsPulledForward_InOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimOrphanedInbox_HonorsTheRowLimitItIsGivenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimOrphanedInbox_ChargesAnAttemptOnlyToRowsItActuallyClaimsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ActiveStreamLeaseExpirySqlTests.cs:ClaimOrphanedInbox_LeasesTheStream_WithTheRowLeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedInbox_RefreshesTheStreamLedgerInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedInbox_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ClaimOrphanedInbox_AStreamWithNoLedgerRow_IsPinnedToTheClaimerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimLedgerLockOrderSqlTests.cs:ConcurrentInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedOutbox_DirectOwnerHoldingItsAliveLock_LeasesNotReclaimedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedOutbox_StaleHeartbeat_WithOnlyAConnectionNamedForIt_LeasesReclaimableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ListenLivenessSqlTests.cs:ClaimOrphanedInbox_StaleHeartbeat_WithOnlyAConnectionNamedForIt_LeasesReclaimableAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#a-held-back-stream-is-claimed-whole</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackStreamsClaimSqlTests.cs:Claim_AnInteractiveEventBehindBackgroundPredecessorsOutsideTheWindow_TakesThePredecessorsWithItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackStreamsClaimSqlTests.cs:Claim_AStreamWithNoEarlierPendingEvent_IsClaimedAsBeforeAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Inbox_TheMapMovesThePartition_TheNewOwnerClaimsTheStream_AndTheStoredOwnerNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Inbox_StealingNeverTakesAPartitionThisInstanceDoesNotOwnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:ClaimWork_Inbox_WithAMap_TakesThePartitionsTheMapGivesIt_NotItsResidueAsync</tests>
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
  p_ledger_deferred BOOLEAN DEFAULT FALSE,
  -- 566 (#2558): the published partition map, when the caller's assignment carries one: entry p + 1 is the index in
  -- the assignment's members of the member partition p belongs to. NULL ranks by modulo, as before. Every legacy-mode
  -- partition test in this function reads it through wh_partition_in_share.
  p_partition_owners SMALLINT[] DEFAULT NULL,
  -- 740 (#2633): the derivation scheme of the calling build, stamped on a row this call claims for the first time.
  -- NULL stamps nothing: a build before 740 passes none, so a row it claims first records none and reads as the
  -- shared scheme (0) on every later claim.
  p_derivation_scheme SMALLINT DEFAULT NULL
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
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
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
  -- 566 (#2555): in map mode, the partitions this instance owns, read once per call through wh_partitions_i_own (the set
  -- form of wh_i_own: the same rule over the same table) and tested by hash membership, as the rank was tested by
  -- arithmetic. A per-row probe over the window would cost one index probe per row the window reads. Empty when off.
  mine_partitions AS MATERIALIZED (
    SELECT mp.partition_number
    FROM __SCHEMA__.wh_partitions_i_own(p_instance_id) AS mp(partition_number)
    WHERE v_map
  ),
  others_live AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
    WHERE ast.assigned_instance_id <> p_instance_id
      AND ast.lease_expiry > p_now
      -- 317 (#2027): the one liveness rule. p_stale_cutoff is kept in the signature and no longer read.
      AND __SCHEMA__.wh_instance_liveness(si, si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
      -- 566 (#2555): a stored owner holds nothing in map mode.
      AND NOT v_map
  ),
  mine_owned AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
      AND NOT v_map
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
               OR CASE WHEN v_map THEN (j.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(j.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
               OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
             OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
             OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
           OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
           OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
               OR CASE WHEN v_map THEN (j.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(j.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
               OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
             OR CASE WHEN v_map THEN (i.partition_number % 10000) IN (SELECT mp.partition_number FROM mine_partitions mp) ELSE __SCHEMA__.wh_partition_in_share(i.partition_number, p_instance_rank, p_active_instance_count, p_partition_owners) END
             OR (p_allow_steal AND NOT v_map AND NOT EXISTS (
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
  -- 411 (#2120): a picked stream's earlier pending events go with it, whatever their priority. The store pass holds an
  -- event back while an earlier pending event of its stream is not held by this instance (230), and the lanes read each
  -- band through a window of its oldest rows, so an interactive event behind background events outside the background
  -- window was claimed and held back on every lease while its predecessors were never claimed. For each stream the
  -- batch picked, the events received before its first picked row that no live lease holds are taken ahead of it, at
  -- most as many as the stream's picked rows, through idx_inbox_state_stream_order. A stream with none (the common case)
  -- costs one empty index probe. They share the stream's lane and come first in it, since they are what the stream's
  -- picked rows wait for; the batch bound below still holds.
  pick_streams AS MATERIALIZED (
    SELECT c.stream_id,
           MIN(po.lane) AS lane,
           COUNT(*) AS picked,
           (array_agg(c.received_at ORDER BY c.received_at, c.message_id))[1] AS first_received_at,
           (array_agg(c.message_id ORDER BY c.received_at, c.message_id))[1] AS first_message_id
    FROM pick_ordered po
    JOIN claimable c ON c.message_id = po.cand_message_id
    WHERE c.stream_id IS NOT NULL
    GROUP BY c.stream_id
  ),
  predecessors AS (
    SELECT pr.message_id AS cand_message_id,
           s.lane,
           0 AS kind,
           0::BIGINT AS stream_seq,
           pr.received_at AS cand_received_at
    FROM pick_streams s
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at
      FROM __SCHEMA__.wh_inbox_state i
      WHERE i.stream_id = s.stream_id
        AND i.processed_at IS NULL
        AND i.is_event = TRUE
        AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
        AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
        AND (i.received_at, i.message_id) < (s.first_received_at, s.first_message_id)
      ORDER BY i.received_at, i.message_id
      LIMIT s.picked
    ) pr
  ),
  pick_final AS (
    SELECT p.cand_message_id, p.lane, p.kind, p.stream_seq, p.cand_received_at FROM pick_ordered p
    UNION ALL
    -- Received before the stream's first picked row, so never one of the picked rows themselves.
    SELECT d.cand_message_id, d.lane, d.kind, d.stream_seq, d.cand_received_at FROM predecessors d
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
    JOIN pick_final pick ON pick.cand_message_id = i.message_id
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
        -- 740 (#2633): a row's first claim (attempts 0 before this bump, nothing recorded) records the scheme the
        -- calling build runs its handling under, and every later claim keeps it, so a retry on any build derives the
        -- ids the first run derived. In the update the claim already makes: no write of its own.
        derivation_scheme = CASE
          WHEN i.attempts = 0 AND i.derivation_scheme IS NULL THEN p_derivation_scheme
          ELSE i.derivation_scheme
        END,
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
          -- 566 (#2555): in map mode the partition's owner claimed the stream, so the ledger names it.
          WHEN v_map THEN p_instance_id
          WHEN ast.assigned_instance_id IS NULL THEN p_instance_id
          WHEN NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_service_instances si
            WHERE si.instance_id = ast.assigned_instance_id
          ) THEN p_instance_id
          ELSE ast.assigned_instance_id
        END,
        lease_expiry = CASE
          WHEN v_map THEN p_lease_expiry
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
COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_inbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, BOOLEAN, INTEGER, BOOLEAN, SMALLINT[], SMALLINT) IS
  '411 (#2120): a picked stream''s earlier pending events that no live lease holds are taken ahead of its picked rows, '
  'whatever their priority, at most as many as the stream''s picked rows, so an event is not held back behind '
  'predecessors outside its band''s window. '
  'Acquires unowned/abandoned pending inbox rows for an instance, bounded by p_max_rows (025 ownership, 145 bounded '
  'acquisition, 148 stream leases, 150 bucket lanes, 159 claim window, 167 idle band). 202: the stream ledger rows of '
  'the claimed streams are locked in one stream_id pass, whoever owns them, before they are refreshed or taken over, '
  'and streams with no row are inserted after that pass in the same order; p_ledger_deferred leaves the ledger to the '
  'caller (claim_work), and each row carries its partition number for that write. 566 (#2554, #2555): with the service''s switch on (wh_partition_ownership_by_map), decided through wh_i_own alone. 740 (#2633): a row''s first claim (attempts 0, nothing recorded) records p_derivation_scheme, the calling build''s derivation scheme; NULL records nothing.';

-- ---------------------------------------------------------------------------------------------
-- claim_work: copied from its last word (598), with the 740 delta: the scheme handed to the inbox acquisition.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_work');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimDerivationSchemeSqlTests.cs</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#the-claim-runs-only-what-can-find-work</docs>
-- <docs>fundamentals/work-coordinator/per-stream-drain#working-set-budget</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimInboxWorkingSetSqlTests.cs:ClaimWork_WithoutInboxAcquisition_LeasesNoNewInboxRow_AndStillReOffersWhatItHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimInboxWorkingSetSqlTests.cs:ClaimWork_WithoutInboxAcquisition_StillAcquiresPerspectiveWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:InboxReOffer_AnInstanceHoldingOneLane_WalksOnlyThatLane_AndOffersItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:InboxReOffer_ALaneHoldingOnlyUnfetchableRows_IsStillWalked_AndOffersNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:Claim_AnInstanceHoldingNothing_RunsNoReOfferWalkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:PerspectiveReOffer_AnInstanceHoldingOneBucket_WalksOnlyThatBucket_AndOffersItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:PerspectiveReOffer_TheIdleBucketWhileWithheld_IsNotOffered_AndNotWalkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackEventsDoNotCrowdTheReOfferTests.cs:OneStreamReOffer_PassesOverAHeldBackEvent_ToAFetchableCheckpointAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackEventsDoNotCrowdTheReOfferTests.cs:HeldBackStream_OnceItsPredecessorIsHeldToo_IsStoredAndReOfferedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimReofferPlanShapeTests.cs:TheReOfferWalk_ReadsTheWorkTableOnlyThroughItsHeldLanesIndexAsync</tests>
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WithTheCurrentAssignment_RanksByItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WithASupersededAssignment_RanksItselfAndAsksForARefreshAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WithAnExpiredAssignment_RanksItselfAndAsksForARefreshAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:ClaimWork_WhenNotAMemberOfTheCurrentAssignment_RanksItselfWithoutANoticeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Rank_ADirectPeerOnTheSlowCadenceHoldingItsLock_IsCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Rank_ADirectPeerWhoseLockIsReleased_IsCountedOutAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Rank_APooledPeerPastTheCutoff_IsCountedOutWhateverLockItsSessionHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Notice_ADirectCallerHoldingItsLock_IsNotAskedToRegisterBetweenSlowBeatsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Notice_APooledCallerPastTheCutoff_IsStillAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_APeerInMigrating_IsNotCounted_SoTheCallerCoversEveryPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_APeerThatReachesAcceptingCommands_IsCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_ADrainingPeer_ItsShareMovesToTheCallerAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_ADirectPeerHoldingItsLockInMigrating_IsNotCountedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_AnOlderPeerThatRecordsNoPhase_IsCountedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Rank_ACallerThatIsNotYetEligible_StillCountsItselfAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Claim_ADrainingCaller_StillClaimsItsShareAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Claim_ACallerThatRecordsNoPhase_StillClaimsItsShareAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Claim_ARefusedCallerWhoseRowIsStale_IsStillAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ACallerWhoseRowRecordsAnotherPhase_IsAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ACallerWhoseRowRecordsItsPhase_IsNotAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ADrainingCallerWhoseRowSaysSo_IsNotAskedToRegisterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Notice_ACallerThatSaysNothingOfItsPhase_IsJudgedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Rank_ADirectPeerThatLostItsLock_IsNotCountedPastOneBeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Rank_ADirectPeerWithoutItsLock_IsCountedInsideOneBeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Rank_APeerInsideItsOwnWindow_IsCountedPastTheOldThirtySecondCutoffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Caller_LiveByTheRule_IsNotAskedToReRegisterAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#an-owner-renews-the-leases-it-let-run-out</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_AHeldRowWhoseLeaseRanOut_OnAStreamItsRankOwns_IsRenewedAndOffered_WithoutAnAttemptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_ARenewedStream_IsLeasedInTheLedgerToTheRenewingInstanceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_EveryExpiredRowOfARenewedStream_IsRenewedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_AHeldRowWhoseLeaseIsLive_KeepsItsLeaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_PastTheAcquisitionBound_TheRestOfItsExpiredRowsAreRenewed_NotChargedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_RenewsAtMostTheClaimWindow_OldestLeaseFirstAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_WhenTheInstanceCountChanged_AndThePartitionFallsToALivePeer_LeavesTheRowToAcquisitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_WhenThePartitionFallsToAPeer_ButTheHoldersLedgerLeaseIsLive_RenewsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_WhenALivePeerHoldsTheStream_LeavesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnerLeaseRenewalSqlTests.cs:Claim_WithStealingAllowed_TheRenewalStillDoesNotSteal_AcquisitionDoesAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkPlanShapeTests.ExpiredLeases.cs:ClaimWork_HeldRowsWhoseLeaseRanOut_AreRenewed_AndTheNextPollDoesNotPassOverThemAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkPlanShapeTests.ExpiredLeases.cs:ClaimWork_RenewingExpiredLeases_ReadsThemThroughTheLeaseOrderIndexAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimReofferWalkCostSqlTests.cs:PerspectiveReOffer_OnStatisticsThatSayEveryLeaseLapsed_ReadsWhatItHolds_OncePerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimReofferWalkCostSqlTests.cs:InboxReOffer_OnStatisticsThatSayEveryLeaseLapsed_ReadsWhatItHolds_OncePerStreamAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#the-re-offer-walk-start</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWalkSeedSqlTests.cs:ClaimWork_AsInstalled_DrawsEveryWalkStartThroughTheSeedableStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWalkSeedSqlTests.cs:ClaimWork_Seeded_ReOffersTheHeldStreamsThatFollowTheSeed_InStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/ClaimPollPerCallCostScenarioTests.cs:ClaimPollPerCallCost_TwoFillsOfOneDepth_AreOneWorkloadAtOneCostAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Perspective_TheMapMovesThePartition_TheNewOwnerClaimsTheStream_AndTheStoredOwnerNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:AClaimerOutsideTheMap_ClaimsNothing_InAnyQueueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:WithNoValidMap_NothingIsClaimed_AndTheClaimSaysSoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:AnAssignerLapse_DoesNotStallClaims_WhileTheMapsMembersAreLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Inbox_AnExpiredLeaseOnAPartitionThatMovedAway_IsNotRenewedByItsHolderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:SwitchOff_TheStoredOwnerKeepsItsStream_AsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:ClaimWork_Outbox_WithAMap_TakesThePartitionsTheMapGivesIt_NotItsResidueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:ClaimWork_WithAMapButAStaleVersion_RanksItselfByTheResidueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:ClaimWork_WithAMapThatLeavesItOut_RanksItselfByTheResidueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:ClaimWork_HeldInboxLeases_AreRenewedOnlyOnPartitionsTheMapGivesItAsync</tests>
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
  p_outbox_run_length INTEGER DEFAULT NULL,
  -- 203 (#1254): the partition assignment the caller has cached, by version. When it is still the published one and
  -- its lease has not run out, this instance's rank and the member count come from it instead of from ranking the
  -- heartbeat rows; otherwise the claim ranks itself as before and raises
  -- NOTICE 'whizbang.partition_assignment_stale=true' so the caller refreshes. NULL: no assignment, rank as before.
  p_assignment_epoch BIGINT DEFAULT NULL,
  p_assignment_revision BIGINT DEFAULT NULL,
  -- 204 (#1286): whether the caller holds its session alive-lock right now. A direct caller holding it is live by
  -- the lock, so its registration is not reported stale between the slow beats of its heartbeat.
  p_alive_lock_held BOOLEAN DEFAULT FALSE,
  -- 229 (#1638): the lifecycle phase the caller announces. When its row records another phase (a transition write that
  -- was lost, or overwritten by a beat built before it), the registration is reported stale and the caller's
  -- registration writes the phase, so a ready instance cannot stay ineligible for work while it claims. NULL (an older
  -- caller): the row is judged by liveness alone, as before.
  p_lifecycle_phase TEXT DEFAULT NULL,
  -- 390 (#1853): whether to lease new inbox rows on this poll. The caller passes FALSE while the inbox rows it holds in
  -- memory are at their byte budget, so rows its drain would only hand back are not leased; the inbox it already holds
  -- is re-offered as before, and every other acquisition is untouched.
  p_acquire_inbox BOOLEAN DEFAULT TRUE,
  -- 740 (#2633): the calling build's derivation scheme, handed to the inbox acquisition, which stamps it on a row's
  -- first claim. NULL (a build before 740) stamps nothing.
  p_derivation_scheme SMALLINT DEFAULT NULL
) RETURNS TABLE(
  source VARCHAR(20),           -- __CATEGORY_OUTBOX__ | __CATEGORY_INBOX__ | 'perspective_stream'
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
  -- 115: coalesce-pending rows are not claim-pump work, they wait for the coalesce worker.


  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL AND coalesce_group IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox_state WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NULL LIMIT 1);
  -- 462: no probe of the receptor-processing table. Nothing writes it, so the probe found nothing on every poll.

  IF NOT v_has_any_work THEN
    RETURN;  -- empty result set; orphan-claim sub-functions never invoked
  END IF;

  -- 207 (#1349): plan the acquisitions below on statistics that describe the tables as they are. A queue table
  -- analyzed while empty and filled since, with its autovacuum worker stuck behind the claim scans, made every
  -- candidate index look free, and one claim read the backlog once per stream it captured. On a healthy database
  -- this is a catalog read per table and analyzes nothing; autovacuum always gets there first.
  PERFORM __SCHEMA__.wh_refresh_stale_queue_statistics();

  -- Non-empty path: claim outbox work and return it.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    -- 203: set when the cached assignment passed the fence and supplied the rank.
    v_assigned BOOLEAN := FALSE;
    -- 300 (#1636): the caller's own row, read once by the registration probe.
    v_caller __SCHEMA__.wh_service_instances%ROWTYPE;
    v_caller_found BOOLEAN;
    v_count INTEGER;
    -- 566 (#2558): the published partition map, read with the rank when the caller's assignment passes the fence; NULL
    -- otherwise, so a claim that ranks itself ranks by modulo as before.
    v_owners SMALLINT[];
    -- v0.661: track per-category RETURN QUERY rowcount so the drain-mode hint
    -- can be derived from ROW_COUNT instead of four fresh COUNT(*) queries.
    v_outbox_rows INTEGER := 0;
    v_inbox_rows INTEGER := 0;
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
    -- 367: whether this instance holds any pending row in each re-offer lane, read once before the walks. Inbox lanes
    -- are indexed bucket * 2 + 1 for commands and bucket * 2 + 2 for events; perspective buckets are bucket + 1.
    v_inbox_lanes_held BOOLEAN[];
    v_perspective_buckets_held BOOLEAN[];
    -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
    v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
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
    -- 204 (#1286): a direct caller that holds its alive-lock is live by the lock, which is how its peers rank it, so a
    -- row older than the cutoff is not stale for it: its heartbeat runs on the slow cadence while it holds the lock.
    -- 229 (#1638): and a row that records another phase than the one the caller announces is stale as well; the same
    -- primary-key probe reads it, so the check costs nothing.
    -- 300 (#1636): the same primary-key probe reads the caller's row once, for the registration check and for whether
    -- the caller may claim at all. No second lookup: the probe already fetched this row.
    SELECT * INTO v_caller FROM __SCHEMA__.wh_service_instances WHERE instance_id = p_instance_id;
    v_caller_found := FOUND;
    IF NOT v_caller_found
       OR NOT __SCHEMA__.wh_instance_liveness(v_caller, p_alive_lock_held)
       OR NOT (p_lifecycle_phase IS NULL OR v_caller.lifecycle_phase = p_lifecycle_phase) THEN
      RAISE NOTICE 'whizbang.instance_registration_stale=true';
    END IF;

    -- 300 (#1636): a caller whose own row says it may not claim (not eligible for work, and not draining) gets nothing.
    -- Its share is already its peers': they rank it out (229) and the assigner leaves it out (229), so refusing it
    -- leaves nothing unclaimed. A caller with no row is served as before: a running instance whose row was reaped, or
    -- one that has not registered yet, re-registers on the notice above and is ranked as live meanwhile.
    IF v_caller_found AND NOT __SCHEMA__.wh_instance_may_claim(v_caller) THEN
      RETURN;
    END IF;

    -- 566 (#2554): in map mode nothing ranks itself. Every acquisition below decides through wh_i_own, the published
    -- map's ownership table, and a claimer with no valid map claims nothing and waits: the map is valid while its lease
    -- runs, and past its lease while every one of its members is live, so an assigner lapse never stalls claims. The
    -- caller's cached version is not consulted: the ownership table is the map, read in this statement.
    IF v_map AND NOT __SCHEMA__.wh_partition_map_is_valid() THEN
      RAISE NOTICE 'whizbang.partition_map_unavailable=true';
      RETURN;
    END IF;

    -- The rank counts this instance as live whatever its row says: it is running this claim. Ranked
    -- as absent it would fall to a solo rank and widen its claim over every peer's partitions until
    -- its registration lands. Peers are ranked as before, on a fresh heartbeat row. Their view of
    -- this instance catches up when the caller registers, which is no later than it did when the
    -- repair ran here: a write inside this transaction was invisible to them until the claim
    -- committed anyway.
    -- 203 (#1254): the fence where the assignment is read. The caller's cached version must still be the published
    -- one (epoch and revision) and its lease unexpired, and this instance a member of it. One read of a one-row table,
    -- which replaces the ranking below rather than adding to it. Deliberately not keyed on the singleton primary key:
    -- the table is one row by its constraint, so a scan of its one page costs one buffer where the key would cost an
    -- index page as well (PartitionAssignmentClaimCostScenarioTests).
    -- 566: the rank below is the legacy mode's alone; in map mode no claim ranks.
    IF NOT v_map AND p_assignment_epoch IS NOT NULL THEN
      SELECT array_position(pa.members, p_instance_id) - 1, cardinality(pa.members), pa.partition_owners
      INTO v_rank, v_count, v_owners
      FROM __SCHEMA__.wh_published_partition_assignment pa
      WHERE pa.epoch = p_assignment_epoch
        AND pa.revision = p_assignment_revision
        AND pa.lease_expires_at > v_now;
      v_assigned := v_rank IS NOT NULL;
      IF NOT FOUND THEN
        -- Superseded or expired: the caller refreshes, and this claim ranks itself. Never stop claiming.
        RAISE NOTICE 'whizbang.partition_assignment_stale=true';
      END IF;
    END IF;

    IF NOT v_map AND NOT v_assigned THEN
    -- 566 (#2558): a claim that ranks itself ranks by modulo, never by a map it is not a member of, so the map read
    -- with a rank it did not keep is cleared in the same statement.
    SELECT ranked.instance_rank, ranked.active_instance_count, NULL::SMALLINT[] INTO v_rank, v_count, v_owners
    FROM (
      SELECT live.instance_id,
             (ROW_NUMBER() OVER (ORDER BY live.instance_id) - 1)::INTEGER AS instance_rank,
             COUNT(*) OVER ()::INTEGER AS active_instance_count
      FROM (
        -- 204 (#1286): the liveness rule. A fresh heartbeat, or a direct instance whose alive-lock is held: the slow
        -- cadence of a lock holder never counts it out, and a direct instance that lost its lock is judged by its
        -- heartbeat at once, like a pooled one. The lock is consulted only for a direct row past the cutoff (the OR
        -- short-circuits), as a hashed subplan built once per claim.
        -- 229 (#1638): and only a peer eligible for work is counted (wh_instance_is_eligible_for_work): alive is not
        -- ready, and a starting or draining peer's share must go to the others at once. Read from the rows this scan
        -- already reads; the caller is counted below whatever its own row says, as it always was.
        SELECT si.instance_id
        FROM __SCHEMA__.wh_service_instances si
        WHERE __SCHEMA__.wh_instance_liveness(si, si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
          AND __SCHEMA__.wh_instance_is_eligible_for_work(si)
        UNION
        SELECT p_instance_id
      ) live
    ) ranked
    WHERE ranked.instance_id = p_instance_id;
    END IF;

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
        TRUE,
        v_owners
      ) a;
    END IF;

    -- Claim orphaned / unowned inbox work, same predicate shape.
    -- 158: two index probes; see the outbox guard above.
    -- 390 (#1853): not while the caller's inbox working set is at its byte budget. Its drain would hand every new row
    -- back, so leasing them is a claim, a fetch and a release on every poll for nothing. The probes are skipped too.
    IF p_acquire_inbox AND (EXISTS (
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
    )) THEN
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
        TRUE,
        v_owners,
        p_derivation_scheme
      ) a;
    END IF;

    -- 462 (#2345): this instance's inbox rows whose lease ran out, on streams it still owns, are renewed and offered
    -- again by the walk below; before, the walk passed over each of them on every poll until acquisition re-leased it.
    -- Ownership is acquisition's rule without stealing: the stream's ledger lease is this instance's and live, or the
    -- row's partition falls to its rank and no live peer holds the stream (the instance count changing moves the
    -- partition, and a stream a peer drains is the peer's). Anything else is left to acquisition, as before. No
    -- attempt is charged and no failure recorded: the row waited, it did not fail. Read oldest lease first through
    -- idx_inbox_state_lease_order, a claim window at a time, so the cost follows the batch; locked SKIP LOCKED, so
    -- the renewal never waits; and the streams join the one ledger pass below, so a stream whose rows this instance
    -- holds under a live lease is its stream in the ledger.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
        AND instance_id = p_instance_id
      LIMIT 1
    ) THEN
      WITH expired AS (
        SELECT i.ctid AS row_ctid, i.stream_id, i.partition_number
        FROM __SCHEMA__.wh_inbox_state i
        WHERE i.lease_expiry < v_now
          AND i.processed_at IS NULL
          AND i.instance_id IS NOT NULL
          AND i.instance_id = p_instance_id
        ORDER BY i.lease_expiry, i.received_at, i.message_id
        LIMIT GREATEST(p_max_streams, COALESCE(p_max_rows, 0), 1) * 8
      ),
      owned AS (
        SELECT e.row_ctid
        FROM expired e
        -- 566 (#2555): in map mode a row is renewed exactly when this instance owns its partition (wh_i_own); the
        -- stored owner and the rank are the legacy mode's.
        WHERE (NOT v_map AND EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_active_streams ast
                WHERE ast.stream_id = e.stream_id
                  AND ast.assigned_instance_id = p_instance_id
                  AND ast.lease_expiry > v_now))
           OR ((e.partition_number IS NULL OR CASE WHEN v_map THEN __SCHEMA__.wh_i_own(e.partition_number, p_instance_id) IS NOT NULL ELSE __SCHEMA__.wh_partition_in_share(e.partition_number, v_rank, v_count, v_owners) END)
               AND (v_map OR NOT EXISTS (
                 SELECT 1
                 FROM __SCHEMA__.wh_active_streams ast
                 JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
                 WHERE ast.stream_id = e.stream_id
                   AND ast.assigned_instance_id <> p_instance_id
                   AND ast.lease_expiry > v_now
                   AND __SCHEMA__.wh_instance_liveness(si, si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h)))))
      ),
      -- By row address, so the lock and the write each reach exactly the rows the window read (a TID scan), never
      -- the table. A row another session changed since this statement's snapshot no longer matches its address
      -- under the lock and is skipped, as SKIP LOCKED skips one it holds: the next poll looks again.
      locked AS (
        SELECT i.ctid AS row_ctid
        FROM __SCHEMA__.wh_inbox_state i
        WHERE i.ctid = ANY (ARRAY(SELECT o.row_ctid FROM owned o))
          AND i.instance_id = p_instance_id
          AND i.processed_at IS NULL
          AND i.lease_expiry < v_now
        FOR UPDATE OF i SKIP LOCKED
      ),
      renewed AS (
        UPDATE __SCHEMA__.wh_inbox_state i
        SET lease_expiry = v_lease_expiry
        WHERE i.ctid = ANY (ARRAY(SELECT l.row_ctid FROM locked l))
        RETURNING i.stream_id AS r_stream_id, i.partition_number AS r_partition_number
      )
      SELECT v_ledger_streams || COALESCE(array_agg(r.r_stream_id), '{}'),
             v_ledger_partitions || COALESCE(array_agg(r.r_partition_number), '{}')
      INTO v_ledger_streams, v_ledger_partitions
      FROM renewed r;
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
        TRUE,
        v_owners
      ) a;
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
    -- 367: a walk runs only for a lane the instance holds a row in. Under plan_cache_mode = force_custom_plan, and on a
    -- pooled connection that is reset between calls, every execution of a statement is planned afresh, so the eight
    -- inbox walks below and the perspective walks after them were a plan each per claim, most of them for lanes holding
    -- nothing (on a busy service the walks returned a row on about one execution in five). One statement asks every
    -- lane of both loops instead: one correlated probe per lane, planned once, each the first entry of its held-lanes
    -- index under (instance, bucket[, kind]), so it costs the same whatever the instance holds. It asks only whether a
    -- pending row is held there, which every row a walk can land on satisfies, so a lane it reports empty is one whose
    -- walk would return nothing; a lane holding only rows a walk passes over (a lapsed lease, an unstored event) is
    -- walked exactly as before. Read after the store pass, which is what makes rows fetchable, and after every
    -- acquisition, in this transaction; nothing between here and the perspective walks writes a perspective event.
    SELECT
      (SELECT array_agg(EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_inbox_state i
                WHERE i.instance_id = p_instance_id
                  AND i.processed_at IS NULL
                  AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = l.bucket
                  AND i.is_event = l.is_event) ORDER BY l.lane)
       FROM (VALUES (1, 0, false), (2, 0, true), (3, 1, false), (4, 1, true),
                    (5, 2, false), (6, 2, true), (7, 3, false), (8, 3, true)) AS l(lane, bucket, is_event)),
      (SELECT array_agg(EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
                WHERE pe.instance_id = p_instance_id
                  AND pe.processed_at IS NULL
                  AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = l.bucket)
                ORDER BY l.bucket)
       FROM (VALUES (0), (1), (2), (3)) AS l(bucket))
    INTO v_inbox_lanes_held, v_perspective_buckets_held;
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
        -- 367: an empty lane is not walked; see the lane probe above.
        CONTINUE WHEN NOT v_inbox_lanes_held[v_bucket * 2 + CASE WHEN v_is_event THEN 2 ELSE 1 END];
        -- 598 (#2580): drawn at random, or the session's measurement seed (_claim_walk_start).
        v_start := __SCHEMA__._claim_walk_start();
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
               -- 545 (#2474): a filter on the held-lanes entry (which covers the lease), never an index condition, so
               -- statistics that say every lease lapsed cannot hand the walk to idx_inbox_state_lease_order.
               AND (i.lease_expiry > v_now) IS TRUE
               -- 349 (#2117): fetch_inbox_batch never returns an event the store pass left unstored, so the
               -- walk does not land on one; a stream whose held rows are all such events is not offered.
               AND (NOT i.is_event OR i.chain_emitted_at IS NOT NULL)
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
               AND (i.lease_expiry > v_now) IS TRUE  -- 545: see above
               -- 349 (#2117): fetch_inbox_batch never returns an event the store pass left unstored, so the
               -- walk does not land on one; a stream whose held rows are all such events is not offered.
               AND (NOT i.is_event OR i.chain_emitted_at IS NOT NULL)
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
      -- 367: an empty bucket is not walked (the lane probe before the inbox walks). Skipped where the walk would run, after the trickle bound above, so the
      -- remaining count, and the perspective row count derived from it below, are what they were.
      CONTINUE WHEN NOT v_perspective_buckets_held[v_bucket + 1];
      -- 598 (#2580): drawn at random, or the session's measurement seed (_claim_walk_start).
      v_start := __SCHEMA__._claim_walk_start();
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
             -- 545 (#2474): a filter on the held-lanes entry (which carries the lease), never an index condition, so
             -- statistics that say every lease lapsed cannot hand the walk to the lease-expiry index.
             AND (pe.lease_expiry > v_now) IS TRUE
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
             AND (pe.lease_expiry > v_now) IS TRUE  -- 545: see above
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
       OR (k.kind = c_source_inbox AND v_inbox_rows > 0)
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
COMMENT ON FUNCTION __SCHEMA__.claim_work(UUID, TEXT, TEXT, INTEGER, INTEGER, INTEGER, INTEGER, DOUBLE PRECISION, INTEGER, BOOLEAN, INTEGER, BOOLEAN, INTERVAL, INTEGER, INTERVAL, INTEGER, INTEGER, BIGINT, BIGINT, BOOLEAN, TEXT, BOOLEAN, SMALLINT) IS
  'The claim poll: acquires orphaned and unowned work for an instance (outbox, inbox, perspective events) and '
  'returns what it holds. 196: never writes the caller''s registration. 202: the acquisitions defer the '
  'stream ledger, and it is written once for every stream they leased (wh_lease_claimed_streams), in one stream_id '
  'pass after the last queue row is locked. 203: a cached partition assignment (epoch, revision) that is still '
  'published and unexpired supplies the rank and member count; otherwise the claim ranks itself and raises '
  'whizbang.partition_assignment_stale. 204: a peer is ranked live by a fresh heartbeat or, when it is direct, by its '
  'held alive-lock (wh_direct_alive_lock_holders); p_alive_lock_held keeps a direct caller holding its lock from being '
  'asked to re-register between the slow beats of its heartbeat. 229: only a peer eligible for work '
  '(wh_instance_is_eligible_for_work) is ranked, and p_lifecycle_phase reports a registration that records another '
  'phase than the caller''s stale. 300: a caller whose own row may not claim (wh_instance_may_claim: not eligible '
  'for work and not draining) gets nothing; a caller with no row is served as before. 349: the inbox re-offer skips '
  'a held event the store pass left unstored, which the drain cannot fetch. 367: a re-offer walk runs only for a lane '
  'the instance holds a pending row in, read for every lane of both walks in one statement before them. 390: '
  'p_acquire_inbox = FALSE leases no new inbox row on the poll (the caller''s inbox working set is at its byte '
  'budget); held inbox is re-offered as before. 462: the inbox rows this instance holds whose lease ran out, on '
  'streams it still owns (its live ledger lease, or its rank''s partition with no live peer holding the stream), are '
  'renewed without an attempt, a claim window at a time, and offered again; their streams join the ledger pass. The '
  'receptor-processing table, which nothing writes, is no longer read. 545: both re-offer walks test the lease as a '
  'filter on their held-lanes entry ((lease_expiry > now) IS TRUE), never as an index condition, so a walk is one '
  'probe per held stream on any statistics. 566 (#2554, #2555): with the service''s switch on (wh_partition_ownership_by_map), decided through wh_i_own alone. 598 (#2580): each walk starts at _claim_walk_start(), a random stream '
  'unless the session set the measurement seed whizbang.claim_walk_seed, which nothing in the framework sets. 740 (#2633): '
  'p_derivation_scheme, the calling build''s derivation scheme, is stamped on an inbox row''s first claim.';

-- ---------------------------------------------------------------------------------------------
-- release_unstarted_leases: copied from its last word (563), with the 740 delta: a refund to no claim un-records.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('release_unstarted_leases');

-- <docs>fundamentals/work-coordinator/claim-loop#releasing-unstarted-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimDerivationSchemeSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReleasedStreamFollowsItsAssignmentSqlTests.cs:AReleasedPerspectiveStream_IsClaimedNextByTheLiveInstanceItsPartitionIsAssignedToAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReleasedStreamFollowsItsAssignmentSqlTests.cs:AReleasedPerspectiveStreamWhosePartitionIsAssignedToTheReleaser_ComesBackToItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReleasedStreamFollowsItsAssignmentSqlTests.cs:AReleaseLeavesAStreamItDoesNotNameWithItsOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BoundedAcquisitionRewriteSqlTests.cs:ReleaseUnstartedLeases_ReturnsOnlyTheNamedStreamsRefundsTheAttemptAndOpensThemToSiblingsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BoundedAcquisitionRewriteSqlTests.cs:ReleaseUnstartedLeases_NeverTouchesAnotherInstancesLeasesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.release_unstarted_leases(
  p_instance_id UUID,
  p_inbox_stream_ids UUID[],
  p_perspective_stream_ids UUID[]
) RETURNS TABLE(inbox_released INTEGER, perspective_released INTEGER) AS $$
DECLARE
  v_inbox INTEGER := 0;
  v_perspective INTEGER := 0;
BEGIN
  IF p_inbox_stream_ids IS NOT NULL AND array_length(p_inbox_stream_ids, 1) > 0 THEN
    -- 162: stream_id is on the lease table as an immutable copy, so the whole predicate and the
    -- whole update are local to it and never touch the wide row.
    UPDATE __SCHEMA__.wh_inbox_state ist
    SET attempts = GREATEST(ist.attempts - 1, 0),
        -- 740 (#2633): a refund back to no claim at all un-records the scheme the refunded claim stamped, since that
        -- claim never ran the row: the row's first run, whichever build it is on, records its own.
        derivation_scheme = CASE WHEN ist.attempts <= 1 THEN NULL ELSE ist.derivation_scheme END,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE ist.instance_id = p_instance_id
      AND ist.processed_at IS NULL
      AND ist.stream_id = ANY(p_inbox_stream_ids);
    GET DIAGNOSTICS v_inbox = ROW_COUNT;
  END IF;
  IF p_perspective_stream_ids IS NOT NULL AND array_length(p_perspective_stream_ids, 1) > 0 THEN
    UPDATE __SCHEMA__.wh_perspective_events pe
    SET attempts = GREATEST(pe.attempts - 1, 0),
        instance_id = NULL,
        lease_expiry = NULL
    WHERE pe.instance_id = p_instance_id
      AND pe.processed_at IS NULL
      AND pe.stream_id = ANY(p_perspective_stream_ids);
    GET DIAGNOSTICS v_perspective = ROW_COUNT;
  END IF;
  -- End this instance's ownership of the released streams so the unowned path opens to siblings.
  -- 563 (#2545): the ledger owner is cleared, not only its lease. Perspective acquisition lets the named owner claim
  -- whatever its lease and keeps every other instance out while that owner is live, so a lease ended alone handed a
  -- released perspective stream straight back to the live instance that released it. deregister_instance (463) clears
  -- the owner the same way. The predicate is unchanged, and with it the plan: the releaser's ledger rows by
  -- idx_active_streams_instance, filtered to the named streams.
  UPDATE __SCHEMA__.wh_active_streams ast
  SET assigned_instance_id = NULL,
      lease_expiry = NULL
  WHERE ast.assigned_instance_id = p_instance_id
    AND ast.stream_id = ANY(COALESCE(p_inbox_stream_ids, ARRAY[]::UUID[]) || COALESCE(p_perspective_stream_ids, ARRAY[]::UUID[]));
  RETURN QUERY SELECT v_inbox, v_perspective;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.release_unstarted_leases(UUID, UUID[], UUID[]) IS
  'Releases the calling instance''s leased-but-not-started inbox and perspective rows in the given streams back to '
  'unassigned (attempt refunded, lease cleared) and ends its ownership of those streams, clearing the stored owner so '
  'the live instance each stream''s partition is assigned to claims it next (145, #724; 563, #2545). Called by the '
  'instance itself (a stuck claim loop, a halt, a failed batch); never by a sibling. 740 (#2633): a refund to no claim un-records the derivation scheme the refunded claim stamped.';

-- ---------------------------------------------------------------------------------------------
-- release_unprocessed_inbox: copied from its last word (162), with the 740 delta: a refund to no claim un-records.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/work-coordinator/claim-loop#releasing-unstarted-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimDerivationSchemeSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs:ReleaseUnprocessed_RefundsTheClaimAttemptAndClearsTheLeaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs:ReleaseUnprocessed_NeverDrivesAttemptsNegativeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs:RepeatedOverClaim_DoesNotAccumulateAttemptsOnUntouchedRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.release_unprocessed_inbox(
  p_instance_id UUID,
  p_message_ids UUID[]
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_released INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET
      -- GREATEST(...,0) keeps the budget from going negative under a duplicated release (retry,
      -- at-least-once flush, a shutdown path that runs twice). A negative budget would make the row
      -- effectively un-dead-letterable, trading this bug for an unbounded-retry one.
      attempts = GREATEST(ist.attempts - 1, 0),
      -- 740 (#2633): a refund back to no claim at all un-records the scheme the refunded claim stamped, since that
      -- claim never ran the row: the row's first run, whichever build it is on, records its own.
      derivation_scheme = CASE WHEN ist.attempts <= 1 THEN NULL ELSE ist.derivation_scheme END,
      -- Clear the lease so the row is immediately claimable again rather than invisible until it
      -- would have expired. Handing work back is the whole point; making the caller wait out a lease
      -- it explicitly relinquished would just reintroduce the stall.
      instance_id = NULL,
      lease_expiry = NULL
  WHERE ist.message_id = ANY(p_message_ids)
    -- Scoped to the caller's OWN claim. A release from an instance that does not hold the row is a
    -- no-op: without this, one worker could unlock a row another instance is actively dispatching
    -- and two workers would handle the same message concurrently. This predicate is also what makes
    -- the call idempotent: after the first release instance_id is NULL, so a repeat matches nothing
    -- and cannot refund twice.
    AND ist.instance_id = p_instance_id
    -- Never disturb rows that already completed.
    AND ist.processed_at IS NULL;

  GET DIAGNOSTICS v_released = ROW_COUNT;
  RETURN v_released;
END;
$$;

-- ---------------------------------------------------------------------------------------------
-- fetch_inbox_batch_with_derivation_scheme: 576's fetch, returning the recorded scheme.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('fetch_inbox_batch_with_derivation_scheme');

-- <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchDerivationSchemeSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchStreamOrderSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_inbox_batch_with_derivation_scheme(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  handler_name VARCHAR(200),
  message_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  error TEXT,
  priority INTEGER,
  stream_position BIGINT,
  derivation_scheme SMALLINT
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- 740 (#2633): fetch_inbox_batch_in_stream_order (576) verbatim, returning the scheme each row's first claim
  -- recorded. A read only: the claim records it.
  --
  -- 576 (#2551): a stream's rows are ranked in its stream order, never by id: its events by stored version (the receive
  -- order 574 stored them in), its commands by arrival, each kind dealt into the arrival slots it holds. The rank is
  -- returned as stream_position, and the drain dispatches by it.
  --
  -- 162: this function needs both tables, because it returns the message and selects on its claim
  -- state. The LEASE table drives the join: every predicate below is on it, so the narrow table is
  -- scanned and the wide row is fetched only for the rows that survive. The previous form scanned
  -- the wide row to apply the same predicate. i.* is enumerated rather than starred because the
  -- moved columns are no longer on wh_inbox and a star would silently change the result shape.
  RETURN QUERY
  -- Materialized, so each row's stored version is probed once however often the ranking below reads it.
  WITH candidates AS MATERIALIZED (
    SELECT
      i.message_id,
      ist.stream_id,
      i.handler_name,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      ist.status,
      ist.attempts,
      ist.partition_number,
      ist.is_event,
      ist.error,
      i.priority,
      i.arrival_position,
      ist.derivation_scheme AS recorded_scheme,
      -- An event's stored version: one primary-key probe per leased row. A command has none.
      (SELECT es.version FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id) AS stored_version
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    -- v0.658 slice 7: mirror of fetch_outbox_batch's Empty/NULL stream handling -- see the matching
    -- comment in the outbox query for the full rationale.
    WHERE (
        ist.stream_id = ANY(p_stream_ids)
        OR ((ist.stream_id IS NULL OR ist.stream_id = __EMPTY_UUID__::uuid)
            AND ist.message_id = ANY(p_stream_ids))
      )
      AND ist.instance_id = p_instance_id
      AND ist.lease_expiry > NOW()
      AND ist.processed_at IS NULL  -- inbox uses processed_at as both production-marker and debug-kept-marker
      -- 230 (#1607): an event is dispatched once it is stored. The emit chain stores a leased event in the claim
      -- that leased it, unless the event waits behind its stream's head; such a row stays leased and unstored,
      -- and completing it would delete it before it ever reached the event store.
      AND (ist.chain_emitted_at IS NOT NULL OR ist.is_event = false OR ist.stream_id IS NULL)
      AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= NOW())
  ),
  -- Each kind's rank in its own order, and the arrival slots each kind holds.
  kinds AS (
    SELECT c.*,
           (c.stored_version IS NOT NULL) AS versioned,
           row_number() OVER (PARTITION BY c.stream_id ORDER BY c.arrival_position) AS slot,
           row_number() OVER (PARTITION BY c.stream_id, (c.stored_version IS NOT NULL)
                              ORDER BY c.stored_version, c.arrival_position) AS kind_rank
    FROM candidates c
  ),
  -- The row of rank n in its kind takes its kind's n-th slot. Both sides are numbered over the batch in (stream, kind,
  -- rank) order, so they meet on one key.
  by_kind AS (
    SELECT k.*, row_number() OVER (ORDER BY k.stream_id, k.versioned, k.kind_rank) AS pair FROM kinds k
  ),
  slots AS (
    SELECT k.slot, row_number() OVER (ORDER BY k.stream_id, k.versioned, k.slot) AS pair FROM kinds k
  ),
  ranked AS (
    SELECT r.*, s.slot AS rank_in_stream
    FROM by_kind r
    JOIN slots s ON s.pair = r.pair
  ),
  -- Running byte total in the SAME order the rows are returned, so the cut is a suffix of the
  -- slice and stream-FIFO is preserved. Measured on the payload columns because those are what
  -- cross the wire and land on the heap; the fixed-width columns are noise by comparison.
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id ORDER BY r.rank_in_stream
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
    WHERE r.rank_in_stream <= p_max_per_stream
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.handler_name::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.error,
    b.priority,
    b.rank_in_stream,
    -- 740 (#2633): the scheme the row's first claim recorded; 0 (shared) for a row a build before 740 claimed first.
    COALESCE(b.recorded_scheme, 0)::SMALLINT
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.rank_in_stream;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_inbox_batch_with_derivation_scheme(UUID[], UUID, INTEGER, BIGINT) IS
  '740 (#2633): fetch_inbox_batch_in_stream_order (576) returning the derivation scheme each row''s first claim recorded, 0 when none (a row a build before the column claimed first). A read only. A new name, because a function''s result never changes under its name (migration 454).';
