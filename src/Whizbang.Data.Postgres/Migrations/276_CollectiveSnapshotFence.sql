-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 276_CollectiveSnapshotFence.sql
-- Date: 2026-11-01
-- Description: The fence a snapshot of a row with collectives reads to record how far they are complete (#1963).
--
--              The collective sink applies a collective to its rows a key at a time and a batch at a time, each batch in
--              a transaction of its own, and records the collective complete only after its last batch. So while a
--              collective is in flight a row may or may not hold it, and nothing says which. The sink's update path is
--              left exactly as it is: no write is added to it. Instead the runner reads this fence before it reads the
--              row and again after it writes the row, and takes the snapshot only when no collective of the row was in
--              flight at either read and none completed between them. The snapshot then holds exactly the row's
--              collectives stamped at or below the first read's mark, and records it (275). Otherwise the snapshot is
--              deferred to a later apply.
--
--              One statement, so the mark and the answer come from one view of the database:
--
--                * out_complete_through: the store's highest commit sequence. When nothing of the row is in flight,
--                  every collective of the row stamped at or below it is complete; one committed later is stamped by a
--                  stamper call that starts after it commits, from a sequence that has already passed every value
--                  handed out, so it lands above the mark.
--                * out_in_flight: a collective of the tenant (scope ->> 't', no tenant matching no tenant) and of the
--                  given types that is not complete: its __collective__ work row is unprocessed (it may have applied
--                  some batches and not others), or its event is unstamped (its stamp will land above the mark though
--                  the row may hold it), or, when p_after is given (the read after the row's write), it was stamped past
--                  p_after (it completed between the two reads, so the row read between them may or may not hold it).
--
--              Each probe walks an index that holds only what it looks for: the unprocessed collective work rows
--              (idx_perspective_event_collective_queue, 215), the unstamped events (idx_event_store_unstamped, 046) and
--              the events stamped past p_after (idx_event_store_commit_sequence, 046). Every collective work row whose
--              event is in the store carries the event's version (215's trigger), so the queue index sees all of them.
--
--              Number 276: reserved for this change.
--
-- Dependencies: 046 (commit_sequence and its indexes), 061 (collective routing), 215 (the collective queue index)
-- Objects: wh_collective_snapshot_fence
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_collective_snapshot_fence');

-- <docs>fundamentals/perspectives/snapshots#collectives</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_WithNoCollectiveOfTheRowInFlight_IsQuiet_AndMarksTheStoresHighestCommitSequenceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_WithACollectiveOfTheRowStillQueued_IsInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_WithACollectiveOfTheRowCompleteButUnstamped_IsInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_IgnoresAnotherTenantsCollectives_AndCollectivesOfOtherTypesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_OfARowWithNoTenant_SeesOnlyCollectivesWithNoTenantAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_AfterAMark_IsInFlight_WhenACollectiveOfTheRowWasStampedPastItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs:Fence_OfAnEmptyStore_IsQuiet_WithNoMarkAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_collective_snapshot_fence(
  p_tenant TEXT,
  p_event_types TEXT[],
  p_after BIGINT
) RETURNS TABLE(
  out_complete_through BIGINT,
  out_in_flight BOOLEAN
) AS $$
  SELECT
    (SELECT max(es.commit_sequence)
     FROM __SCHEMA__.wh_event_store es
     WHERE es.commit_sequence IS NOT NULL),
    EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_perspective_events pe
      INNER JOIN __SCHEMA__.wh_event_store es
        ON es.stream_id = pe.stream_id
        AND es.event_id = pe.event_id
      WHERE pe.perspective_name = '__collective__'
        AND pe.processed_at IS NULL
        AND pe.stream_version IS NOT NULL
        AND es.event_type = ANY(p_event_types)
        AND (es.scope ->> 't') IS NOT DISTINCT FROM p_tenant)
    OR EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_event_store es
      WHERE es.commit_sequence IS NULL
        AND es.event_type = ANY(p_event_types)
        AND (es.scope ->> 't') IS NOT DISTINCT FROM p_tenant)
    OR (p_after IS NOT NULL AND EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_event_store es
      WHERE es.commit_sequence IS NOT NULL
        AND es.commit_sequence > p_after
        AND es.event_type = ANY(p_event_types)
        AND (es.scope ->> 't') IS NOT DISTINCT FROM p_tenant));
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_collective_snapshot_fence(TEXT, TEXT[], BIGINT) IS
  'The collective snapshot fence (#1963): the store''s highest commit sequence, and whether a collective of the tenant and '
  'types is in flight (its __collective__ work row unprocessed, its event unstamped, or, when p_after is given, stamped '
  'past it). Read before a snapshot''s row read and after its write; the snapshot records the mark only when neither '
  'read finds one in flight. One statement, so both answers come from one view.';
