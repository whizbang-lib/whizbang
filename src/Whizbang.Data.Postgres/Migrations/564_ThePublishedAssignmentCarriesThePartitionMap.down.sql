-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 564_ThePublishedAssignmentCarriesThePartitionMap.sql (#2558): the publish and the read as their
-- previous last words defined them, copied whole, then the predicate and the map column dropped. An assigner of the
-- release that publishes a map can no longer publish.

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: 330's definition, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_publish_partition_assignment');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByTheRoleHolder_WritesTheAssignmentAndAnnouncesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByADeposedAssigner_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_AgainInTheSameTenure_AdvancesTheRevisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByANewTenure_RestartsTheRevisionAtTheNewEpochAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_publish_partition_assignment(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_members UUID[],
  p_lease INTERVAL
) RETURNS BIGINT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_revision BIGINT;
BEGIN
  -- The fence where the assignment is written: only the current holder of the role, at the epoch it was granted,
  -- may publish. A deposed assigner gets WHF01 and writes nothing. The assert takes the role row FOR SHARE, so a
  -- vote cannot hand the role on between the check and the write below.
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  INSERT INTO __SCHEMA__.wh_published_partition_assignment AS pa
    (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at)
  VALUES (TRUE, p_role, p_epoch, 1, p_instance_id, p_members, clock_timestamp(), p_lease, clock_timestamp() + p_lease)
  ON CONFLICT (singleton) DO UPDATE SET
    role = EXCLUDED.role,
    epoch = EXCLUDED.epoch,
    revision = CASE WHEN pa.epoch = EXCLUDED.epoch THEN pa.revision + 1 ELSE 1 END,
    assigner_instance_id = EXCLUDED.assigner_instance_id,
    members = EXCLUDED.members,
    published_at = EXCLUDED.published_at,
    lease = EXCLUDED.lease,
    lease_expires_at = EXCLUDED.lease_expires_at
  -- Epochs only rise, so an assignment never goes back to an earlier tenure's.
  WHERE pa.epoch <= EXCLUDED.epoch
  RETURNING pa.revision INTO v_revision;

  IF v_revision IS NULL THEN
    RAISE EXCEPTION 'partition assignment at epoch % is older than the published one', p_epoch
      USING ERRCODE = 'WHF01';
  END IF;

  -- Claimers cache the assignment and refresh it on this; the payload says which version to expect.
  -- 330 (#2046): published on the signal bus. A publish is a decision: recorded in the durable signal log and
  -- announced on wh_partition_assignment at commit, the channel and payload an older release listens for.
  PERFORM __SCHEMA__.wh_publish_signal(
    'wh_partition_assignment', p_epoch::TEXT || ':' || v_revision::TEXT, 'partition-assignment-published');
  RETURN v_revision;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_publish_partition_assignment(TEXT, UUID, BIGINT, UUID[], INTERVAL) IS
  '203 (#1254): publishes the partition assignment. Fenced by wh_assert_role_epoch (WHF01 for a deposed assigner); '
  'the revision advances within an epoch and restarts at 1 for a new one; an older epoch is refused. Returns the '
  'revision. 330 (#2046): announces epoch:revision on the signal bus (wh_partition_assignment), recorded durably.';

-- ---------------------------------------------------------------------------------------------
-- wh_read_partition_assignment: 203's definition, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_read_partition_assignment');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Read_ReturnsThePublishedAssignmentAndTheTimeLeftOnItsLeaseAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_read_partition_assignment()
RETURNS TABLE(
  epoch BIGINT,
  revision BIGINT,
  assigner_instance_id UUID,
  members UUID[],
  published_at TIMESTAMPTZ,
  lease_expires_at TIMESTAMPTZ,
  -- The lease left, by the database's clock, so a reader never compares the expiry with a clock of its own.
  lease_remaining INTERVAL
)
LANGUAGE sql
STABLE
SET timezone = 'UTC'
AS $$
  SELECT pa.epoch, pa.revision, pa.assigner_instance_id, pa.members, pa.published_at, pa.lease_expires_at,
         pa.lease_expires_at - clock_timestamp()
  FROM __SCHEMA__.wh_published_partition_assignment pa
  WHERE pa.singleton;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_read_partition_assignment() IS
  '203 (#1254): the published partition assignment, one primary-key read, with the lease left by the database clock. '
  'Also the operator''s view of the current assignment, its epoch and its assigner.';
-- ---------------------------------------------------------------------------------------------
-- The predicate and the map.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_partition_in_share');

-- RECLAIM: not required: wh_published_partition_assignment holds one row (its singleton constraint), so the dropped
--          map is at most 20,000 bytes and the next publish rewrites the row.
ALTER TABLE __SCHEMA__.wh_published_partition_assignment DROP COLUMN IF EXISTS partition_owners;
