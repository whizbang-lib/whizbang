-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 559_ReleasedStreamsLeaveTheirLedgerOwner.sql
-- Date: 2026-11-23
-- Description: A released stream leaves its stored owner, so a released perspective stream goes to the live instance
--              its partition is assigned to (which may be the releaser) instead of back to the releaser regardless
--              (#2545).
--
--              release_unstarted_leases ended the releaser's ownership of the named streams by setting the ledger's
--              lease_expiry to now and leaving assigned_instance_id in place. Inbox acquisition reads the ledger
--              lease, so a released inbox stream did open to a sibling. Perspective acquisition
--              (claim_orphaned_perspective_events, last word 557) does not: the named owner always claims, and every
--              other instance is kept out while that owner is live (wh_live_instance_ids). A released perspective
--              stream therefore went straight back to the live instance that released it, against what the claim
--              loop's stuck-work release (#724), the run-control held-work release (#2045) and the perspective
--              worker's failed-batch release all document.
--
--              The release now clears assigned_instance_id and lease_expiry, as deregister_instance (463) does for
--              every stream of a leaving instance, so the partition assignment decides who claims the stream next. No
--              acquisition path changes, and the stored owner is made no stronger anywhere. The ledger row stays
--              (cleanup_completed_streams still evicts it once the stream has nothing pending) and the next
--              acquisition writes its owner again. The inbox and perspective updates and the ledger predicate are
--              162's, so the ledger update keeps its plan: EXPLAIN (ANALYZE, BUFFERS) over 20,000 ledger rows reads it
--              as an Index Scan using idx_active_streams_instance (Index Cond assigned_instance_id = the releaser,
--              Filter stream_id = ANY the named streams), 2 shared buffers, 0.1 ms. Off the claim path: called by a
--              release, never per poll.
--
-- Reversal: reversible (162's definition restores the lease-only release; a released perspective stream again returns
--           to a live releaser)
-- Dependencies: 162 (release_unstarted_leases' last word), 463 (deregister_instance clears the owner the same way)
-- Objects: release_unstarted_leases

-- ---------------------------------------------------------------------------------------------
-- release_unstarted_leases: copied from its last word (162), with the ledger owner cleared.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('release_unstarted_leases');

-- <docs>fundamentals/work-coordinator/claim-loop#releasing-unstarted-work</docs>
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
  -- 559 (#2545): the ledger owner is cleared, not only its lease. Perspective acquisition lets the named owner claim
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
  'the live instance each stream''s partition is assigned to claims it next (145, #724; 559, #2545). Called by the '
  'instance itself (a stuck claim loop, a halt, a failed batch); never by a sibling.';
