-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 565_PartitionsAreOwnedByThePublishedMap.sql (#2555): the ownership table, its predicate, the switch and
-- the map's validity are dropped, and wh_publish_partition_assignment and wh_partition_assignment_candidates are
-- restored to their previous last words (564 and 317), copied whole.

-- ---------------------------------------------------------------------------------------------
-- wh_partition_assignment_candidates: 317's definition, copied whole.
-- ---------------------------------------------------------------------------------------------
-- The result type changes, so the old definition goes first.
SELECT __SCHEMA__.drop_all_overloads('wh_partition_assignment_candidates');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Candidates_ADirectInstanceHoldingItsAliveLock_ReportsTheLockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Candidates_APooledInstance_IsNeverJudgedByALockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Candidates_AnEvictedInstance_IsLeftOutAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockLivenessSqlTests.cs:Candidates_ADirectInstanceWhoseLockIsReleased_ReportsNoLockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Candidates_ALiveInstanceInMigrating_IsLeftOutAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Candidates_AnInstanceThatReachesAcceptingCommands_BecomesACandidateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Candidates_ADrainingInstanceWithAFreshHeartbeat_IsLeftOutAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Candidates_AnOlderInstanceThatRecordsNoPhase_IsStillACandidateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LivenessRuleReadersSqlTests.cs:Candidates_ReportWhetherTheRuleCallsEachLiveAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_assignment_candidates()
RETURNS TABLE(
  instance_id UUID,
  connection_mode TEXT,
  -- Only ever true for a direct-mode instance: a pooled instance's lock is not consulted (its session cannot be
  -- seen), so it is judged by its heartbeat alone.
  alive_lock_held BOOLEAN,
  -- How long ago the instance last beat, by the database's clock.
  heartbeat_age INTERVAL,
  -- 317 (#2027): whether the one liveness rule (wh_instance_liveness) calls the instance live. The assigner reads this,
  -- and judges nothing itself.
  is_live BOOLEAN
)
LANGUAGE sql
STABLE
SET timezone = 'UTC'
AS $$
  SELECT si.instance_id,
         COALESCE(si.connection_mode, 'pooled'),
         -- 204: the one definition of a held alive-lock. Only ever true for a direct-mode instance.
         si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h),
         clock_timestamp() - si.last_heartbeat_at,
         __SCHEMA__.wh_instance_liveness(si, si.instance_id IN (SELECT h.instance_id FROM __SCHEMA__.wh_direct_alive_lock_holders() h))
  FROM __SCHEMA__.wh_service_instances si
  WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = si.instance_id)
    -- 229 (#1638): only an instance eligible for work is a candidate; a starting or draining one is alive, not ready.
    AND __SCHEMA__.wh_instance_is_eligible_for_work(si)
  ORDER BY si.instance_id;
$$;
COMMENT ON FUNCTION __SCHEMA__.wh_partition_assignment_candidates() IS
  '203 (#1254): every registered, not evicted instance with its connection mode, whether its alive-lock is held '
  '(direct mode only) and the age of its heartbeat. The assigner applies the per-mode strategy to these. 204: the lock '
  'is read through wh_direct_alive_lock_holders. 229: only an instance eligible for work '
  '(wh_instance_is_eligible_for_work) is a candidate. 317 (#2027): and whether the one liveness rule calls it live '
  '(is_live), which is what the assigner reads.';

DELETE FROM __SCHEMA__.wh_settings WHERE setting_key = 'partition_ownership_mode';
SELECT __SCHEMA__.drop_all_overloads('wh_open_partition_ownership_by_map');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_map_is_valid');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_ownership_by_map');
SELECT __SCHEMA__.drop_all_overloads('wh_i_own');
SELECT __SCHEMA__.drop_all_overloads('wh_supports_ownership_by_map');
DROP TABLE IF EXISTS __SCHEMA__.wh_partition_ownership;

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: 564's definition, copied whole.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_publish_partition_assignment');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByTheRoleHolder_WritesTheAssignmentAndAnnouncesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByADeposedAssigner_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_AgainInTheSameTenure_AdvancesTheRevisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByANewTenure_RestartsTheRevisionAtTheNewEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithAMap_StoresIt_AndTheReadReturnsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithoutAMap_StoresNone_SoTheClaimsRankByModuloAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_AMapOfAnotherLength_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_AMapNamingAMemberItDoesNotList_IsRefusedAsync</tests>
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
                  WHERE o IS NULL OR o < 0 OR o >= COALESCE(cardinality(p_members), 0))) THEN
    RAISE EXCEPTION 'a partition map must give each of the 10000 partitions to one of the % listed members',
      COALESCE(cardinality(p_members), 0)
      USING ERRCODE = '22023';
  END IF;

  INSERT INTO __SCHEMA__.wh_published_partition_assignment AS pa
    (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at,
     partition_owners)
  VALUES (TRUE, p_role, p_epoch, 1, p_instance_id, p_members, clock_timestamp(), p_lease, clock_timestamp() + p_lease,
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
  'to a listed member; NULL publishes the older shape, whose claimers rank by modulo.';

