-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 563_ReleasedStreamsLeaveTheirLedgerOwner.sql (#2545): release_unstarted_leases as 162 defined it,
-- copied whole. A release ends only the ledger lease again, so a released perspective stream returns to a live
-- releaser.

-- ---------------------------------------------------------------------------------------------
-- release_unstarted_leases: 162's body, copied whole.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('release_unstarted_leases');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
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
  v_now TIMESTAMPTZ := NOW();
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
  UPDATE __SCHEMA__.wh_active_streams ast
  SET lease_expiry = v_now
  WHERE ast.assigned_instance_id = p_instance_id
    AND ast.stream_id = ANY(COALESCE(p_inbox_stream_ids, ARRAY[]::UUID[]) || COALESCE(p_perspective_stream_ids, ARRAY[]::UUID[]));
  RETURN QUERY SELECT v_inbox, v_perspective;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.release_unstarted_leases(UUID, UUID[], UUID[]) IS
  'Releases the calling instance''s leased-but-not-started inbox and perspective rows in the given streams back to '
  'unassigned (attempt refunded, lease cleared) and ends its ownership of those streams (145, #724). Called by the '
  'instance itself when its claim loop keeps re-offering the same work set with idle consumers; never by a sibling.';
