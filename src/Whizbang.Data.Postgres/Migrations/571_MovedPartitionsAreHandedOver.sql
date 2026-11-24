-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 571_MovedPartitionsAreHandedOver.sql
-- Date: 2026-11-23
-- Description: A rebalance hands each moved partition over, at a batch boundary, instead of moving it under a running
--              owner (#2557; phase 5 of #2540, design 3.4).
--
--              * wh_publish_partition_assignment, in map mode, no longer moves a partition away from a live owner at
--                once. It sets the partition 'handing_over' with the next owner and a deadline (p_handover_deadline, 30
--                seconds unless the assigner names another, judged on the database's clock); the owner and the epoch
--                stay as they were, so nobody claims there (claims need 'active', 566) and the old owner's in-flight
--                writes still land (570). A republish that gives the partition back to its owner cancels the handover;
--                one that names another next owner redirects it, keeping the deadline. A partition whose owner is not
--                live (the one liveness rule) has nothing to finish, and moves at once with its epoch raised. Off, every
--                publish moves a partition at once, as before.
--              * wh_partitions_handing_over(instance): what an old owner has to hand over.
--              * wh_acknowledge_handover(instance, partitions, epochs): the old owner, having finished its in-flight
--                batch and saved its write-behind models, releases its unstarted rows in those partitions (attempt
--                refunded, lease and ledger owner cleared) and flips each to the next owner at epoch + 1, in one call
--                fenced on the old epoch: a stale epoch, a partition not handing over or a caller that does not own it
--                flips nothing.
--              * wh_complete_expired_handovers(role, instance, epoch): the assigner, under its role epoch, flips every
--                partition past its deadline itself and releases what the old owner held there (attempts kept: a row
--                may have been started). The old owner's late writes are fenced by the raised epoch.
--              * wh_renew_held_leases renews a row only while the caller owns its partition, in either state, so a
--                lease renewal never keeps a row of a partition that has moved. Off, nothing changes.
--
--              Cost: the publish reads the live instances once, and only when the map changed. The acknowledgement and
--              the expiry write the partitions they flip and the old owner's rows there, read through the holder
--              indexes; the expiry reads the handing-over rows of a 10,000-row table once per assigner cycle. A renewal
--              adds one primary-key probe of wh_partition_ownership per row it renews.
--
-- Reversal: reversible (the down file restores 565's publish and 255's renewal, and drops the new functions)
-- Dependencies: 565 (wh_partition_ownership, wh_partition_ownership_by_map, wh_publish_partition_assignment's last word), 255
--               (wh_renew_held_leases), 316 (wh_live_instance_ids), 355 (wh_assert_role_epoch)
-- Objects: _wh_release_partition_leases, wh_partitions_handing_over, wh_acknowledge_handover,
--          wh_complete_expired_handovers, wh_publish_partition_assignment, wh_renew_held_leases
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_OUTBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- _wh_release_partition_leases: what a holder held in the given partitions goes back unassigned.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_release_partition_leases');

-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Ack_FlipsToTheNextOwnerAtEpochPlusOne_AndReleasesTheOldOwnersUnstartedRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:ExpiredHandover_PastTheDeadline_TheAssignerFlips_AndReleasesTheOldOwnersRowsWithoutRefundAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_release_partition_leases(
  p_holder UUID,
  p_partitions INTEGER[],
  p_refund BOOLEAN
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  IF p_holder IS NULL OR p_partitions IS NULL OR array_length(p_partitions, 1) IS NULL THEN
    RETURN;
  END IF;

  -- The outbox first, its rows locked in (stream_id, created_at, message_id) order (172), the order every writer of the
  -- outbox uses; then the inbox and perspective work, then the ledger.
  UPDATE __SCHEMA__.wh_outbox o
  SET attempts = CASE WHEN p_refund THEN GREATEST(o.attempts - 1, 0) ELSE o.attempts END,
      instance_id = NULL,
      lease_expiry = NULL
  FROM (
    SELECT l.message_id
    FROM __SCHEMA__.wh_outbox l
    WHERE l.instance_id = p_holder
      AND l.processed_at IS NULL
      AND l.published_at IS NULL
      AND l.partition_number % 10000 = ANY(p_partitions)
    ORDER BY l.stream_id, l.created_at, l.message_id
    FOR UPDATE OF l
  ) locked
  WHERE o.message_id = locked.message_id;

  UPDATE __SCHEMA__.wh_inbox_state s
  SET attempts = CASE WHEN p_refund THEN GREATEST(s.attempts - 1, 0) ELSE s.attempts END,
      instance_id = NULL,
      lease_expiry = NULL
  WHERE s.instance_id = p_holder
    AND s.processed_at IS NULL
    AND s.partition_number % 10000 = ANY(p_partitions);

  UPDATE __SCHEMA__.wh_perspective_events pe
  SET attempts = CASE WHEN p_refund THEN GREATEST(pe.attempts - 1, 0) ELSE pe.attempts END,
      instance_id = NULL,
      lease_expiry = NULL
  WHERE pe.instance_id = p_holder
    AND pe.processed_at IS NULL
    AND pe.partition_number % 10000 = ANY(p_partitions);

  UPDATE __SCHEMA__.wh_active_streams ast
  SET assigned_instance_id = NULL,
      lease_expiry = NULL
  WHERE ast.assigned_instance_id = p_holder
    AND ast.partition_number % 10000 = ANY(p_partitions);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_release_partition_leases(UUID, INTEGER[], BOOLEAN) IS
  '571 (#2557): p_holder''s unprocessed outbox, inbox and perspective rows in the given partitions, and its ledger rows '
  'there, go back unassigned (lease and owner cleared; the attempt refunded when p_refund, for rows that were never '
  'started). Read through the holder indexes.';

-- ---------------------------------------------------------------------------------------------
-- wh_partitions_handing_over: what an old owner has to hand over.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_partitions_handing_over');

-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:HandingOver_ListsWhatTheOldOwnerMustHandOver_AndNothingForAnyoneElseAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partitions_handing_over(p_instance_id UUID)
RETURNS TABLE(partition_number INTEGER, epoch BIGINT, next_owner_instance_id UUID, handover_deadline TIMESTAMPTZ)
LANGUAGE sql
STABLE
AS $$
  SELECT o.partition_number, o.epoch, o.next_owner_instance_id, o.handover_deadline
  FROM __SCHEMA__.wh_partition_ownership o
  WHERE o.owner_instance_id = p_instance_id
    AND o.state = 'handing_over'
  ORDER BY o.partition_number
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_partitions_handing_over(UUID) IS
  '571 (#2557): the partitions p_instance_id owns and is handing over, with the epoch to acknowledge each at, its next '
  'owner and its deadline.';

-- ---------------------------------------------------------------------------------------------
-- wh_acknowledge_handover: the old owner flips what it has finished.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_acknowledge_handover');

-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Ack_FlipsToTheNextOwnerAtEpochPlusOne_AndReleasesTheOldOwnersUnstartedRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Ack_UnderAStaleEpoch_OrByAnInstanceThatIsNotTheOwner_FlipsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Ack_OfAnActivePartition_FlipsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Order_TheNewOwnerTakesTheStreamAfterTheOldOwnersCompletedEvent_AndNotBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_acknowledge_handover(
  p_instance_id UUID,
  p_partitions INTEGER[],
  p_epochs BIGINT[]
) RETURNS TABLE(partition_number INTEGER, epoch BIGINT, owner_instance_id UUID)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
#variable_conflict use_column
DECLARE
  v_partitions INTEGER[];
  v_epochs BIGINT[];
  v_owners UUID[];
BEGIN
  -- The flip, fenced on the old epoch and the caller's ownership: only a partition this instance still owns, handing
  -- over at exactly the epoch it names, moves. The ownership rows are written first, then the work rows, the order the
  -- expiry uses too.
  WITH flipped AS (
    UPDATE __SCHEMA__.wh_partition_ownership o
    SET owner_instance_id = o.next_owner_instance_id,
        epoch = o.epoch + 1,
        state = 'active',
        next_owner_instance_id = NULL,
        handover_deadline = NULL
    FROM unnest(p_partitions, p_epochs) AS a(p, e)
    WHERE o.partition_number = a.p
      AND o.epoch = a.e
      AND o.owner_instance_id = p_instance_id
      AND o.state = 'handing_over'
    RETURNING o.partition_number, o.epoch, o.owner_instance_id
  )
  SELECT array_agg(f.partition_number ORDER BY f.partition_number), array_agg(f.epoch ORDER BY f.partition_number),
         array_agg(f.owner_instance_id ORDER BY f.partition_number)
    INTO v_partitions, v_epochs, v_owners
  FROM flipped f;

  -- What the old owner still holds there was never started (it finished its in-flight batch before acknowledging), so
  -- it goes back with its attempt refunded, to be claimed by the new owner.
  PERFORM __SCHEMA__._wh_release_partition_leases(p_instance_id, v_partitions, TRUE);

  RETURN QUERY
  SELECT u.p, u.e, u.o FROM unnest(v_partitions, v_epochs, v_owners) AS u(p, e, o);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_acknowledge_handover(UUID, INTEGER[], BIGINT[]) IS
  '571 (#2557): the old owner acknowledges the handover of the partitions it has finished: each one it owns, handing '
  'over at the epoch named for it, flips to its next owner at epoch + 1, and the caller''s unstarted rows there are '
  'released (attempt refunded). Returns the partitions flipped with their new epoch and owner; anything else flips nothing.';

-- ---------------------------------------------------------------------------------------------
-- wh_complete_expired_handovers: the assigner completes a handover past its deadline.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_complete_expired_handovers');

-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:ExpiredHandover_PastTheDeadline_TheAssignerFlips_AndReleasesTheOldOwnersRowsWithoutRefundAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:ExpiredHandover_BeforeTheDeadline_FlipsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:ExpiredHandover_ByADeposedAssigner_IsRefusedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_complete_expired_handovers(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS INTEGER
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_flipped INTEGER := 0;
  v_old RECORD;
BEGIN
  -- Only the current holder of the role, at the epoch it was granted, completes a handover (WHF01 otherwise).
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  FOR v_old IN
    WITH expired AS (
      SELECT x.partition_number, x.owner_instance_id AS old_owner
      FROM __SCHEMA__.wh_partition_ownership x
      WHERE x.state = 'handing_over'
        AND x.handover_deadline <= clock_timestamp()
      ORDER BY x.partition_number
      FOR UPDATE OF x
    ), flipped AS (
      UPDATE __SCHEMA__.wh_partition_ownership o
      SET owner_instance_id = o.next_owner_instance_id,
          epoch = o.epoch + 1,
          state = 'active',
          next_owner_instance_id = NULL,
          handover_deadline = NULL
      FROM expired e
      WHERE o.partition_number = e.partition_number
      RETURNING e.partition_number, e.old_owner
    )
    SELECT f.old_owner, array_agg(f.partition_number ORDER BY f.partition_number) AS partitions
    FROM flipped f
    GROUP BY f.old_owner
    ORDER BY f.old_owner
  LOOP
    -- The old owner may have started these rows, so their attempts stand; its held models are dropped on its side.
    PERFORM __SCHEMA__._wh_release_partition_leases(v_old.old_owner, v_old.partitions, FALSE);
    v_flipped := v_flipped + cardinality(v_old.partitions);
  END LOOP;

  RETURN v_flipped;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_complete_expired_handovers(TEXT, UUID, BIGINT) IS
  '571 (#2557): the partition assigner, fenced by its role epoch, flips every partition whose handover deadline has '
  'passed on the database''s clock to its next owner at epoch + 1 and releases what the old owner held there (attempts '
  'kept). Returns how many partitions it flipped.';

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: 565's definition, with the 571 delta: a moved partition hands over.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_publish_partition_assignment');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByTheRoleHolder_WritesTheAssignmentAndAnnouncesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByADeposedAssigner_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_AgainInTheSameTenure_AdvancesTheRevisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByANewTenure_RestartsTheRevisionAtTheNewEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_WritesOneActiveRowPerPartition_OwnedByTheMembersInTurn_AtEpochOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Republish_AdvancesTheEpochOfExactlyThePartitionsWhoseOwnerChangedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Republish_OfTheSameMembers_LeavesEveryEpochAsItWasAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_WithNoMembers_LeavesTheOwnershipAsItWasAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_ByADeposedAssigner_WritesNoOwnershipAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_InMapMode_LeavesOutAMemberThatDoesNotSupportTheModeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithAMap_StoresIt_AndTheReadReturnsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithoutAMap_StoresNone_SoTheClaimsRankByModuloAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_AMapOfAnotherLength_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_AMapNamingAMemberItDoesNotList_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_WritesOwnershipFromTheMap_NotTheResidueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Republish_OfTheSameMap_WritesNothing_AndAMovedPartitionAdvancesAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_InMapMode_AMapNamingAMemberThatDoesNotSupportTheMode_IsRefusedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_InMapMode_AMovedPartitionWithALiveOwner_HandsOver_KeepingTheOwnerAndEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_InMapMode_TheDeadlineIsTheOneThePublishNamesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_InMapMode_AMovedPartitionWhoseOwnerIsNotLive_FlipsAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_SwitchOff_MovesAPartitionAtOnce_AsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Republish_GivingAPartitionBackToItsOwnerWhileHandingOver_CancelsTheHandoverAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Republish_NamingAnotherNextOwnerWhileHandingOver_RedirectsTheHandoverAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Claims_APartitionHandingOver_IsClaimableByNeitherSide_UntilTheFlipAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_publish_partition_assignment(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_members UUID[],
  p_lease INTERVAL,
  -- 564 (#2558): the partition map, 10,000 member indexes. NULL publishes the older shape, whose claimers rank by modulo.
  p_owners SMALLINT[] DEFAULT NULL,
  -- 571 (#2557): how long a moved partition may hand over before the assigner completes the flip itself.
  p_handover_deadline INTERVAL DEFAULT INTERVAL '30 seconds'
) RETURNS BIGINT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_revision BIGINT;
  -- 565: the members and the map as published, and the ones published before, so the ownership table is written only on
  -- a change.
  v_members UUID[] := COALESCE(p_members, ARRAY[]::UUID[]);
  v_previous UUID[];
  v_previous_owners SMALLINT[];
  -- 571 (#2557): whether this service owns by map, and its live instances, read once and only when the ownership changes.
  v_map BOOLEAN;
  v_live UUID[] := ARRAY[]::UUID[];
BEGIN
  -- The fence where the assignment is written: only the current holder of the role, at the epoch it was granted,
  -- may publish. A deposed assigner gets WHF01 and writes nothing. The assert takes the role row FOR SHARE, so a
  -- vote cannot hand the role on between the check and the write below.
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  -- 564 (#2558): a map must give every one of the 10,000 partitions to a listed member, or some partition would be
  -- claimed by nobody. Refused before anything is written. One pass over the map, on a publish, never on a claim.
  IF p_owners IS NOT NULL AND (
       cardinality(p_owners) <> 10000
       OR EXISTS (SELECT 1 FROM unnest(p_owners) o
                  WHERE o IS NULL OR o < 0 OR o >= cardinality(v_members))) THEN
    RAISE EXCEPTION 'a partition map must give each of the 10000 partitions to one of the % listed members',
      cardinality(v_members)
      USING ERRCODE = '22023';
  END IF;

  -- 565 (#2555): once the service owns by map, an instance that does not support the mode owns no partition, so an
  -- older instance that joins after the switch owns nothing and claims nothing. A map's indexes name its members, so a
  -- map is never re-indexed here: the candidates leave such an instance out (wh_partition_assignment_candidates), and a
  -- map that names one anyway is refused. A members-only publish (an assigner of an earlier release) leaves it out of
  -- the members, rank order kept.
  IF __SCHEMA__.wh_partition_ownership_by_map() THEN
    IF p_owners IS NULL THEN
      SELECT COALESCE(array_agg(m.instance_id ORDER BY m.ordinal), ARRAY[]::UUID[])
      INTO v_members
      FROM unnest(v_members) WITH ORDINALITY AS m(instance_id, ordinal)
      WHERE EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = m.instance_id AND __SCHEMA__.wh_supports_ownership_by_map(si.metadata));
    ELSIF EXISTS (
      SELECT 1 FROM unnest(v_members) AS m(instance_id)
      WHERE NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = m.instance_id AND __SCHEMA__.wh_supports_ownership_by_map(si.metadata))) THEN
      RAISE EXCEPTION 'a partition map published under ownership by map must name only instances that support it'
        USING ERRCODE = '22023';
    END IF;
  END IF;

  SELECT pa.members, pa.partition_owners INTO v_previous, v_previous_owners
  FROM __SCHEMA__.wh_published_partition_assignment pa WHERE pa.singleton;

  INSERT INTO __SCHEMA__.wh_published_partition_assignment AS pa
    (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at,
     partition_owners)
  VALUES (TRUE, p_role, p_epoch, 1, p_instance_id, v_members, clock_timestamp(), p_lease, clock_timestamp() + p_lease,
          p_owners)
  ON CONFLICT (singleton) DO UPDATE SET
    role = EXCLUDED.role,
    epoch = EXCLUDED.epoch,
    revision = CASE WHEN pa.epoch = EXCLUDED.epoch THEN pa.revision + 1 ELSE 1 END,
    assigner_instance_id = EXCLUDED.assigner_instance_id,
    members = EXCLUDED.members,
    published_at = EXCLUDED.published_at,
    lease = EXCLUDED.lease,
    lease_expires_at = EXCLUDED.lease_expires_at,
    partition_owners = EXCLUDED.partition_owners
  -- Epochs only rise, so an assignment never goes back to an earlier tenure's.
  WHERE pa.epoch <= EXCLUDED.epoch
  RETURNING pa.revision INTO v_revision;

  IF v_revision IS NULL THEN
    RAISE EXCEPTION 'partition assignment at epoch % is older than the published one', p_epoch
      USING ERRCODE = 'WHF01';
  END IF;

  -- 565 (#2555): the ownership table follows the map, written in this statement so the role epoch fences it too.
  -- Partition p is owned by the member the map gives it (members[owners[p + 1] + 1], #2558); a members-only publish
  -- states no map, and its claimers rank by modulo, so p is owned by members[p mod n], the first map's answer either way.
  -- Only a partition whose owner changes is written, and its epoch advances by one; a new partition starts at epoch 1.
  -- A map with no members owns nothing new, so the table keeps its owners (none of them is live, so none claims) until a
  -- map with members is published. The 10,000 rows are compared only when the members or the map changed: a backstop
  -- republish of the same map writes nothing.
  IF cardinality(v_members) > 0
     AND (v_previous IS DISTINCT FROM v_members
          OR v_previous_owners IS DISTINCT FROM p_owners
          OR NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_partition_ownership o WHERE o.partition_number = 0)) THEN
    -- 571 (#2557): in map mode a partition moving away from a live owner hands over instead of moving at once: it goes
    -- 'handing_over' with the next owner and a deadline, keeping its owner and epoch, until the old owner acknowledges
    -- (wh_acknowledge_handover) or the deadline passes (wh_complete_expired_handovers). A partition handing over that the
    -- map gives back to its owner returns to 'active'; one the map gives to yet another instance is redirected, its
    -- deadline kept. An owner that is not live finishes nothing, so its partitions move at once, epoch raised. Off, every
    -- move is at once, as 565 wrote it.
    v_map := __SCHEMA__.wh_partition_ownership_by_map();
    IF v_map THEN
      SELECT COALESCE(array_agg(l.instance_id), ARRAY[]::UUID[]) INTO v_live FROM __SCHEMA__.wh_live_instance_ids() l;
    END IF;

    INSERT INTO __SCHEMA__.wh_partition_ownership AS o (partition_number, owner_instance_id, epoch, state)
    SELECT g.p, v_members[1 + COALESCE(p_owners[g.p + 1]::INTEGER, g.p % cardinality(v_members))], 1, 'active'
    FROM generate_series(0, 9999) AS g(p)
    ON CONFLICT (partition_number) DO UPDATE SET
      owner_instance_id = CASE WHEN v_map AND o.owner_instance_id = ANY(v_live) THEN o.owner_instance_id
                               ELSE EXCLUDED.owner_instance_id END,
      epoch = CASE WHEN o.owner_instance_id = EXCLUDED.owner_instance_id OR (v_map AND o.owner_instance_id = ANY(v_live))
                   THEN o.epoch ELSE o.epoch + 1 END,
      state = CASE WHEN o.owner_instance_id <> EXCLUDED.owner_instance_id AND v_map AND o.owner_instance_id = ANY(v_live)
                   THEN 'handing_over' ELSE 'active' END,
      next_owner_instance_id = CASE WHEN o.owner_instance_id <> EXCLUDED.owner_instance_id AND v_map
                                         AND o.owner_instance_id = ANY(v_live)
                                    THEN EXCLUDED.owner_instance_id END,
      handover_deadline = CASE WHEN o.owner_instance_id <> EXCLUDED.owner_instance_id AND v_map
                                    AND o.owner_instance_id = ANY(v_live)
                               THEN CASE WHEN o.state = 'handing_over' THEN o.handover_deadline
                                         ELSE clock_timestamp() + p_handover_deadline END END
    WHERE (o.owner_instance_id IS DISTINCT FROM EXCLUDED.owner_instance_id OR o.state = 'handing_over')
      -- A partition already handing over to the owner the map names is left as it is.
      AND NOT (o.state = 'handing_over'
               AND o.next_owner_instance_id = EXCLUDED.owner_instance_id
               AND v_map
               AND o.owner_instance_id = ANY(v_live));
  END IF;

  -- Claimers cache the assignment and refresh it on this; the payload says which version to expect.
  -- 330 (#2046): published on the signal bus. A publish is a decision: recorded in the durable signal log and
  -- announced on wh_partition_assignment at commit, the channel and payload an older release listens for.
  PERFORM __SCHEMA__.wh_publish_signal(
    'wh_partition_assignment', p_epoch::TEXT || ':' || v_revision::TEXT, 'partition-assignment-published');
  RETURN v_revision;
END;
$$;
COMMENT ON FUNCTION __SCHEMA__.wh_publish_partition_assignment(TEXT, UUID, BIGINT, UUID[], INTERVAL, SMALLINT[], INTERVAL) IS
  '203 (#1254): publishes the partition assignment. Fenced by wh_assert_role_epoch (WHF01 for a deposed assigner); '
  'the revision advances within an epoch and restarts at 1 for a new one; an older epoch is refused. Returns the '
  'revision. 330 (#2046): announces epoch:revision on the signal bus (wh_partition_assignment), recorded durably. '
  '564 (#2558): stores the partition map (p_owners), refused with 22023 unless it gives each of the 10,000 partitions '
  'to a listed member; NULL publishes the older shape, whose claimers rank by modulo. 565 (#2555): writes '
  'wh_partition_ownership from the map (members[owners[p + 1] + 1], or members[p mod n] with no map), the epoch of '
  'each changed partition advancing and nothing written when neither the members nor the map changed; once the service '
  'owns by map, a members-only publish leaves out members that do not support the mode and a map naming one is refused. 571 (#2557): in map mode a partition moving away from a live owner hands over (state handing_over, next owner, deadline p_handover_deadline) instead of moving at once; one given back to its owner returns to active; an owner that is not live loses its partitions at once.';

-- ---------------------------------------------------------------------------------------------
-- wh_renew_held_leases: 255's definition, with the 571 delta. Signature unchanged.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/work-coordinator/batched-flushers</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Inbox_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Inbox_TheHolder_RenewsTheRowAndItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Outbox_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Outbox_TheHolder_RenewsTheRowAndItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_PerspectiveEvent_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_PerspectiveEvent_TheHolder_RenewsTheRowAndItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_ARowThatIsGoneOrCompleted_IsReportedNotRenewedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_NoIds_ReportsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_AnUnknownCategory_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.cs:TheHeldLeaseRenewal_TakesEachCategoryBeforeTheLedgerAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Renewal_KeepsARowWhileHandingOver_ButNotOnceThePartitionHasMovedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Renewal_SwitchOff_RenewsAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_renew_held_leases(
  p_category TEXT,
  p_instance_id UUID,
  p_ids UUID[],
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS UUID[] AS $$
DECLARE
  v_new_expiry TIMESTAMPTZ := NOW() + (p_lease_seconds || ' seconds')::INTERVAL;
  v_renewed UUID[];
  v_streams UUID[];
  -- 571 (#2557): whether the service owns by map, read once.
  v_map BOOLEAN;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN ARRAY[]::UUID[];
  END IF;
  v_map := __SCHEMA__.wh_partition_ownership_by_map();

  CASE p_category
    WHEN __CATEGORY_OUTBOX__ THEN
      -- 172's order: outbox rows in (stream_id, created_at, message_id) order, the order every waiting writer of
      -- the outbox uses. The lock re-asserts the holder, so a row another instance claimed is not renewed.
      WITH renewed AS (
        UPDATE __SCHEMA__.wh_outbox o
          SET lease_expiry = v_new_expiry
          FROM (
            SELECT l.message_id
            FROM __SCHEMA__.wh_outbox l
            WHERE l.message_id = ANY(p_ids)
              AND l.instance_id = p_instance_id
              AND l.processed_at IS NULL
              -- 571 (#2557): in map mode, only while this instance owns the row's partition (either state): the
              -- switch is read once, and the ownership is one primary-key probe per row.
              AND (NOT v_map OR l.partition_number IS NULL OR EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_partition_ownership o
                WHERE o.partition_number = l.partition_number % 10000 AND o.owner_instance_id = p_instance_id))
            ORDER BY l.stream_id, l.created_at, l.message_id
            FOR UPDATE OF l
          ) locked
          WHERE o.message_id = locked.message_id
          RETURNING o.message_id, o.stream_id
      )
      SELECT array_agg(r.message_id), array_agg(r.stream_id) FILTER (WHERE r.stream_id IS NOT NULL)
        INTO v_renewed, v_streams
        FROM renewed r;
    WHEN __CATEGORY_INBOX__ THEN
      WITH renewed AS (
        UPDATE __SCHEMA__.wh_inbox_state s
          SET lease_expiry = v_new_expiry
          WHERE s.message_id = ANY(p_ids)
            AND s.instance_id = p_instance_id
            AND s.processed_at IS NULL
            -- 571 (#2557): in map mode, only while this instance owns the row's partition (either state): the
            -- switch is read once, and the ownership is one primary-key probe per row.
            AND (NOT v_map OR s.partition_number IS NULL OR EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_partition_ownership o
              WHERE o.partition_number = s.partition_number % 10000 AND o.owner_instance_id = p_instance_id))
          RETURNING s.message_id, s.stream_id
      )
      SELECT array_agg(r.message_id), array_agg(r.stream_id) FILTER (WHERE r.stream_id IS NOT NULL)
        INTO v_renewed, v_streams
        FROM renewed r;
    WHEN 'perspective_event' THEN
      WITH renewed AS (
        UPDATE __SCHEMA__.wh_perspective_events pe
          SET lease_expiry = v_new_expiry
          WHERE pe.event_work_id = ANY(p_ids)
            AND pe.instance_id = p_instance_id
            AND pe.processed_at IS NULL
            -- 571 (#2557): in map mode, only while this instance owns the row's partition (either state): the
            -- switch is read once, and the ownership is one primary-key probe per row.
            AND (NOT v_map OR pe.partition_number IS NULL OR EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_partition_ownership o
              WHERE o.partition_number = pe.partition_number % 10000 AND o.owner_instance_id = p_instance_id))
          RETURNING pe.event_work_id, pe.stream_id
      )
      SELECT array_agg(r.event_work_id), array_agg(r.stream_id) FILTER (WHERE r.stream_id IS NOT NULL)
        INTO v_renewed, v_streams
        FROM renewed r;
    ELSE
      RAISE EXCEPTION 'wh_renew_held_leases: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;

  -- The stream leases of the renewed rows' streams that this instance is assigned (148), in stream_id order (172).
  IF v_streams IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_active_streams ast
      SET lease_expiry = v_new_expiry
      FROM (
        SELECT a.stream_id
        FROM __SCHEMA__.wh_active_streams a
        WHERE a.stream_id = ANY(v_streams)
          AND a.assigned_instance_id = p_instance_id
        ORDER BY a.stream_id
        FOR UPDATE OF a
      ) locked
      WHERE ast.stream_id = locked.stream_id;
  END IF;

  -- The caller's ids this call did not renew, from the two arrays alone.
  RETURN ARRAY(
    SELECT asked.id FROM unnest(p_ids) AS asked(id)
    EXCEPT
    SELECT renewed.id FROM unnest(v_renewed) AS renewed(id)
  );
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.wh_renew_held_leases(TEXT, UUID, UUID[], INTEGER) IS
  'Batched lease extension per category for the supplied ids that p_instance_id holds and that are not yet processed, '
  'with the stream leases of their streams that it is assigned (148). Returns the ids it did not renew (another '
  'instance holds the row, it completed, or it is gone), which the caller stops holding (#1852). Outbox rows are '
  'locked in (stream_id, created_at, message_id) order and ledger rows in stream_id order (172). 571 (#2557): in map mode a row is renewed only while the caller owns its partition (active or handing over), so a renewal never keeps a row of a partition that has moved.';
