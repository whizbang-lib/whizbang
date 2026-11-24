-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 564_ThePublishedAssignmentCarriesThePartitionMap.sql
-- Date: 2026-11-23
-- Description: The published partition assignment carries an explicit partition-to-member map, so a join or leave
--              moves about 1/N of the partitions instead of nearly all of them (#2558).
--
--              Until now the assignment published only its members, and every claim took partition p as member
--              p % N. One join or leave therefore moved about N/(N+1) of the 10,000 partitions. The assigner now keeps
--              the map and changes it incrementally (PartitionMap): a join takes its share evenly from every member, a
--              leave spreads its share evenly over the rest, and nothing else moves.
--
--              The map is wh_published_partition_assignment.partition_owners, a SMALLINT[10000] whose entry p + 1 is
--              the zero-based index in members of the member partition p belongs to. One owner per partition by
--              construction; one array subscript answers "is this mine"; a fixed 20,000 bytes whatever the member
--              count. The partition count is fixed at 10,000 (owner decision), and wh_publish_partition_assignment
--              refuses a map of any other length or one naming a member it does not list.
--
--              Additive, so an older reader still works:
--                * wh_publish_partition_assignment gains p_owners DEFAULT NULL. An older assigner publishes members
--                  only, which stores a NULL map, and every claim then ranks by modulo exactly as before.
--                * wh_read_partition_assignment gains a trailing partition_owners column; a reader that names its
--                  columns reads what it read before.
--                * One inlinable predicate, wh_partition_in_share(partition, rank, count, owners): the map's member when
--                  a map is given, partition % count otherwise.
--
--              The claim functions read the map through wh_partition_in_share in 566, together with the ownership
--              table's map-mode branch (565, 566), so each claim function is redefined once, with both branches.
--              Numbered before the ownership table, which is written from this map.
--
-- Reversal: reversible (the down file restores each function's previous last word and drops the column; an assigner
--           of this release then cannot publish, because its publish names p_owners)
-- Dependencies: 203 (the assignment), 330 (wh_publish_partition_assignment's last word)
-- Objects: wh_published_partition_assignment, wh_partition_in_share, wh_publish_partition_assignment, wh_read_partition_assignment
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_OUTBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- The map: one member index per partition, NULL for an assignment of the older shape.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE __SCHEMA__.wh_published_partition_assignment ADD COLUMN IF NOT EXISTS partition_owners SMALLINT[] NULL;

COMMENT ON COLUMN __SCHEMA__.wh_published_partition_assignment.partition_owners IS
  '564 (#2558): the explicit partition map. Entry p + 1 is the zero-based index in members of the member partition p '
  'belongs to; 10,000 entries. NULL for an assignment published without one, whose claimers rank by modulo '
  '(members[(p % cardinality(members)) + 1]).';

-- ---------------------------------------------------------------------------------------------
-- wh_partition_in_share: the one partition test every claim applies.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_partition_in_share');

-- A single SQL expression with no SET clause, IMMUTABLE and not STRICT, so the planner inlines it into the claim's
-- statements: each candidate row costs one array subscript (a fixed-width array, so an offset) or one modulo. A partition
-- past the map wraps, as PartitionMap.OwnerOf does, so no partition number is left unowned. A map that is NULL or empty
-- falls back to the modulo; a negative partition matches no rank either way, as before.
-- <docs>fundamentals/work-coordinator/partition-assignment#incremental-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:PartitionInShare_WithAMap_IsTheMapsAnswer_AndWithoutOne_TheResidueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:ClaimWork_Outbox_WithAMap_TakesThePartitionsTheMapGivesIt_NotItsResidueAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_in_share(
  p_partition INTEGER,
  p_rank INTEGER,
  p_count INTEGER,
  p_owners SMALLINT[]
) RETURNS BOOLEAN
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
  SELECT COALESCE(p_owners[(p_partition % NULLIF(cardinality(p_owners), 0)) + 1]::INTEGER, p_partition % p_count) = p_rank
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_partition_in_share(INTEGER, INTEGER, INTEGER, SMALLINT[]) IS
  '564 (#2558): whether partition p_partition is in the share of the member at p_rank: the published map''s answer when '
  'p_owners is given (owners[(p % cardinality) + 1] = p_rank), otherwise p_partition % p_count = p_rank. The only '
  'partition test in the claim functions. Inlined by the planner.';

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: 330's definition, storing the map.
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

-- ---------------------------------------------------------------------------------------------
-- wh_read_partition_assignment: 203's definition, returning the map as well.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_read_partition_assignment');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Read_ReturnsThePublishedAssignmentAndTheTimeLeftOnItsLeaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithAMap_StoresIt_AndTheReadReturnsItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_read_partition_assignment()
RETURNS TABLE(
  epoch BIGINT,
  revision BIGINT,
  assigner_instance_id UUID,
  members UUID[],
  published_at TIMESTAMPTZ,
  lease_expires_at TIMESTAMPTZ,
  -- The lease left, by the database's clock, so a reader never compares the expiry with a clock of its own.
  lease_remaining INTERVAL,
  -- 564 (#2558): the partition map, or NULL for an assignment of the older shape. Last, so a reader that names its
  -- columns reads what it read before.
  partition_owners SMALLINT[]
)
LANGUAGE sql
STABLE
SET timezone = 'UTC'
AS $$
  SELECT pa.epoch, pa.revision, pa.assigner_instance_id, pa.members, pa.published_at, pa.lease_expires_at,
         pa.lease_expires_at - clock_timestamp(), pa.partition_owners
  FROM __SCHEMA__.wh_published_partition_assignment pa
  WHERE pa.singleton;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_read_partition_assignment() IS
  '203 (#1254): the published partition assignment, one primary-key read, with the lease left by the database clock. '
  'Also the operator''s view of the current assignment, its epoch and its assigner. 564 (#2558): and its partition '
  'map (partition_owners), NULL for the older shape.';
