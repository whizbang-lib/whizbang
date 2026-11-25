-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 571_MovedPartitionsAreHandedOver.sql (#2557): the publish moves every partition at once again (565),
-- renewals are scoped to the holder alone (255), and the handover functions are dropped. A partition left
-- 'handing_over' is returned to its next owner first, at epoch + 1, so nothing is left owned by nobody.

UPDATE __SCHEMA__.wh_partition_ownership
SET owner_instance_id = next_owner_instance_id, epoch = epoch + 1, state = 'active',
    next_owner_instance_id = NULL, handover_deadline = NULL
WHERE state = 'handing_over' AND next_owner_instance_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: 565's definition, copied whole.
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
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_publish_partition_assignment(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_members UUID[],
  p_lease INTERVAL,
  -- 564 (#2558): the partition map, 10,000 member indexes. NULL publishes the older shape, whose claimers rank by modulo.
  p_owners SMALLINT[] DEFAULT NULL
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
    INSERT INTO __SCHEMA__.wh_partition_ownership AS o (partition_number, owner_instance_id, epoch, state)
    SELECT g.p, v_members[1 + COALESCE(p_owners[g.p + 1]::INTEGER, g.p % cardinality(v_members))], 1, 'active'
    FROM generate_series(0, 9999) AS g(p)
    ON CONFLICT (partition_number) DO UPDATE SET
      owner_instance_id = EXCLUDED.owner_instance_id,
      epoch = o.epoch + 1,
      state = 'active',
      next_owner_instance_id = NULL,
      handover_deadline = NULL
    WHERE o.owner_instance_id IS DISTINCT FROM EXCLUDED.owner_instance_id;
  END IF;

  -- Claimers cache the assignment and refresh it on this; the payload says which version to expect.
  -- 330 (#2046): published on the signal bus. A publish is a decision: recorded in the durable signal log and
  -- announced on wh_partition_assignment at commit, the channel and payload an older release listens for.
  PERFORM __SCHEMA__.wh_publish_signal(
    'wh_partition_assignment', p_epoch::TEXT || ':' || v_revision::TEXT, 'partition-assignment-published');
  RETURN v_revision;
END;
$$;
COMMENT ON FUNCTION __SCHEMA__.wh_publish_partition_assignment(TEXT, UUID, BIGINT, UUID[], INTERVAL, SMALLINT[]) IS
  '203 (#1254): publishes the partition assignment. Fenced by wh_assert_role_epoch (WHF01 for a deposed assigner); '
  'the revision advances within an epoch and restarts at 1 for a new one; an older epoch is refused. Returns the '
  'revision. 330 (#2046): announces epoch:revision on the signal bus (wh_partition_assignment), recorded durably. '
  '564 (#2558): stores the partition map (p_owners), refused with 22023 unless it gives each of the 10,000 partitions '
  'to a listed member; NULL publishes the older shape, whose claimers rank by modulo. 565 (#2555): writes '
  'wh_partition_ownership from the map (members[owners[p + 1] + 1], or members[p mod n] with no map), the epoch of '
  'each changed partition advancing and nothing written when neither the members nor the map changed; once the service '
  'owns by map, a members-only publish leaves out members that do not support the mode and a map naming one is refused.';

-- ---------------------------------------------------------------------------------------------
-- wh_renew_held_leases: 255's definition, copied whole.
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
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN ARRAY[]::UUID[];
  END IF;

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
  'locked in (stream_id, created_at, message_id) order and ledger rows in stream_id order (172).';

SELECT __SCHEMA__.drop_all_overloads('wh_complete_expired_handovers');
SELECT __SCHEMA__.drop_all_overloads('wh_acknowledge_handover');
SELECT __SCHEMA__.drop_all_overloads('wh_partitions_handing_over');
SELECT __SCHEMA__.drop_all_overloads('_wh_release_partition_leases');
