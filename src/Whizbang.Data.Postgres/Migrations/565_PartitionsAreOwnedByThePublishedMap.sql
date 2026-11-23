-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 565_PartitionsAreOwnedByThePublishedMap.sql
-- Date: 2026-11-23
-- Description: The partition ownership table, its one predicate, and the per-service switch that makes the published
--              partition assignment the only owner of a stream (#2555, #2554; phase 3 of #2540).
--
--              * wh_partition_ownership: one row per partition (10,000), naming its owner, the epoch of that
--                ownership (it rises by one every time the owner changes: the fencing token phase 4 checks), its
--                state ('active', or 'handing_over' for the handover protocol of phase 5) and, while handing over, the
--                next owner and the deadline. Ownership is per partition, never per stream.
--              * wh_publish_partition_assignment writes it in the statement that publishes the map, so it is fenced by
--                the same role epoch (wh_assert_role_epoch) and a deposed assigner can write neither. Partition p is
--                owned by members[p mod n], the rank the claims already use; only a partition whose owner changes is
--                written, and its epoch advances. A publish of the members already published writes nothing.
--              * wh_i_own(partition, instance) answers "may this instance claim this partition": the ownership's epoch
--                when the instance owns it and it is active, NULL otherwise. One primary-key probe. Work with no
--                partition (no stream) is anyone's, at epoch 0. A partition number at or past the table's size is owned
--                through its remainder, so a service configured with more partitions still has an owner for each.
--              * wh_partition_ownership_by_map(): the per-service switch, a one-way wh_settings latch
--                ('partition_ownership_mode' = 'map'). wh_open_partition_ownership_by_map, called by the assigner under
--                its role epoch, writes it once a map is published at that epoch and every live instance of the
--                service says in its registry metadata that it supports the mode (partition_ownership_level >= 1;
--                the trim gate's shape, 537). The services that share no schema switch on their own schedules. Once
--                on, a publish gives no partition to an instance that does not support the mode, so an older
--                instance that joins afterwards owns nothing and claims nothing.
--              * wh_partition_map_is_valid(): whether claims may use the published map. Valid while its lease runs,
--                and past its lease while every one of its members is live by the one liveness rule (316): an assigner
--                that lapses never stalls claims, and a map known to name a dead member waits for the next assigner.
--                Judged on the database's clock alone, so no host clock decides it.
--
--              Nothing claims through the table yet (566 does, behind the switch), and the switch stays off: the
--              assigner asks for it only when PartitionAssigner.OwnershipByMap is set, which defaults off until the
--              handover and fencing phases land. Off, every claim path is exactly as before.
--
--              Cost: the publish is rare (a membership change, or the five-minute backstop). It compares the members
--              with the ones published before and writes the 10,000 rows only when they differ, each partition at most
--              once; the predicate is one index-only-sized probe of a 10,000-row table.
--
-- Reversal: reversible (the down file drops the table, the setting and the functions, and restores 330's publish)
-- Dependencies: 203 (wh_published_partition_assignment), 316 (wh_live_instance_ids), 330 (wh_publish_partition_assignment's
--               last word), 355 (wh_assert_role_epoch), 537 (the registry-metadata support marker shape)
-- Objects: wh_partition_ownership, wh_i_own, wh_partition_ownership_by_map, wh_partition_map_is_valid,
--          wh_open_partition_ownership_by_map, wh_publish_partition_assignment
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- ---------------------------------------------------------------------------------------------
-- The ownership table
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_partition_ownership (
  partition_number INTEGER PRIMARY KEY CHECK (partition_number >= 0 AND partition_number < 10000),
  owner_instance_id UUID NOT NULL,
  epoch BIGINT NOT NULL CHECK (epoch >= 1),
  state TEXT NOT NULL DEFAULT 'active' CHECK (state IN ('active', 'handing_over')),
  next_owner_instance_id UUID NULL,
  handover_deadline TIMESTAMPTZ NULL
);

COMMENT ON TABLE __SCHEMA__.wh_partition_ownership IS
  '565 (#2555): who owns each partition, one row per partition, written by the partition assigner under its role epoch '
  'with every publish. epoch rises by one each time the owner changes (the fencing token); state is active, or '
  'handing_over with the next owner and a deadline. Claims decide through wh_i_own once the service''s switch '
  '(wh_partition_ownership_by_map) is on.';

-- ---------------------------------------------------------------------------------------------
-- wh_i_own: the one predicate every claim path decides through in map mode.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_i_own');

-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:IOwn_AnswersTheEpochForTheActiveOwner_AndNullForAnyoneElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:IOwn_APartitionHandingOver_IsOwnedByNobodyNewAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:IOwn_WorkWithoutAPartition_IsAnyonesAtEpochZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:IOwn_APartitionNumberPastTheTable_IsOwnedThroughItsRemainderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_i_own(p_partition_number INTEGER, p_instance_id UUID)
RETURNS BIGINT
LANGUAGE sql
STABLE
AS $$
  SELECT CASE
    WHEN p_partition_number IS NULL THEN 0::BIGINT
    ELSE (
      SELECT o.epoch
      FROM __SCHEMA__.wh_partition_ownership o
      WHERE o.partition_number = p_partition_number % 10000
        AND o.owner_instance_id = p_instance_id
        AND o.state = 'active')
  END
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_i_own(INTEGER, UUID) IS
  '565 (#2555): the epoch of p_instance_id''s ownership of the partition when it owns it and the partition is active, '
  'NULL otherwise; 0 for work without a partition, which is anyone''s. One primary-key probe of wh_partition_ownership. '
  'The one predicate every claim path decides through once the service''s switch is on.';

-- ---------------------------------------------------------------------------------------------
-- wh_partition_ownership_by_map: the per-service switch.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_partition_ownership_by_map');

-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Mode_IsOffUntilTheSwitchIsWrittenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Switch_StaysOffWhileALiveInstanceDoesNotSupportTheMode_AndTurnsOnOnceEveryLiveOneDoesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_ownership_by_map()
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_settings s
    WHERE s.setting_key = 'partition_ownership_mode' AND s.setting_value = 'map');
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_partition_ownership_by_map() IS
  '565 (#2555): whether this service claims by the partition ownership table alone (wh_settings partition_ownership_mode '
  '= map, a one-way latch the assigner writes). One primary-key probe; read once per call by the claim and store paths.';

-- ---------------------------------------------------------------------------------------------
-- wh_partition_map_is_valid: whether claims may use the published map.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_partition_map_is_valid');

-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:MapIsValid_WhileItsLeaseRunsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:MapIsValid_PastItsLease_WhileEveryMemberIsLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:MapIsNotValid_PastItsLease_OnceAMemberIsNotLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:MapIsNotValid_WhenNoneIsPublishedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_map_is_valid()
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  -- The lease is tested first and short-circuits: while the assigner renews its map, the liveness rule (which reads
  -- pg_locks) is never consulted.
  SELECT EXISTS (
    SELECT 1
    FROM __SCHEMA__.wh_published_partition_assignment pa
    WHERE pa.singleton
      AND (pa.lease_expires_at > NOW()
           OR NOT EXISTS (
             SELECT 1 FROM unnest(pa.members) AS m(instance_id)
             WHERE m.instance_id NOT IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l))));
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_partition_map_is_valid() IS
  '565 (#2554): whether the published partition map may be claimed by: published, and its lease unexpired or every '
  'member live by the one liveness rule. An assigner that lapses never stalls claims while the map''s members are live; '
  'a map that names a dead member past its lease waits for the next assigner. Judged on the database''s clock.';

-- ---------------------------------------------------------------------------------------------
-- wh_open_partition_ownership_by_map: the assigner turns the switch on.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_open_partition_ownership_by_map');

-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Switch_StaysOffWhileALiveInstanceDoesNotSupportTheMode_AndTurnsOnOnceEveryLiveOneDoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Switch_AStaleInstanceThatDoesNotSupportTheMode_DoesNotHoldItOffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Switch_WithNoMapPublished_StaysOffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Switch_ByADeposedAssigner_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Switch_OnceOn_StaysOn_WhenAnInstanceThatDoesNotSupportItJoinsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Store_OpenOwnershipByMap_TurnsTheSwitchOn_AndAnswersNullToADeposedAssignerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_open_partition_ownership_by_map(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS TEXT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_live BIGINT;
  v_ready BIGINT;
BEGIN
  -- Only the current holder of the role, at the epoch it was granted, may turn the switch on (WHF01 otherwise).
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  IF __SCHEMA__.wh_partition_ownership_by_map() THEN
    RETURN 'open';
  END IF;

  -- The ownership table is written by the publish, so a map at this tenure's epoch means it holds this tenure's map.
  IF NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_published_partition_assignment pa
    WHERE pa.singleton AND pa.epoch = p_epoch AND cardinality(pa.members) > 0) THEN
    RETURN 'closed (no map is published at this epoch)';
  END IF;

  SELECT count(*),
         count(*) FILTER (WHERE jsonb_typeof(si.metadata -> 'partition_ownership_level') = 'number'
                            AND (si.metadata ->> 'partition_ownership_level')::NUMERIC >= 1)
    INTO v_live, v_ready
  FROM __SCHEMA__.wh_service_instances si
  WHERE si.instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l);

  IF v_live = 0 OR v_ready < v_live THEN
    RETURN 'closed (' || v_ready || ' of ' || v_live || ' live instances support ownership by map)';
  END IF;

  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
  VALUES ('partition_ownership_mode', 'map', 'string',
          'Migration 565 (#2555): every live instance of this service supported ownership by map when this was written, '
          'so every claim decides through the partition ownership table (wh_i_own). One-way: never cleared.',
          NOW(), 'wh_open_partition_ownership_by_map')
  ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'map', updated_at = NOW(), updated_by = EXCLUDED.updated_by;

  RETURN 'opened';
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_open_partition_ownership_by_map(TEXT, UUID, BIGINT) IS
  '565 (#2555): the partition assigner, fenced by its role epoch, latches wh_settings partition_ownership_mode = map '
  '(one-way) once a map is published at its epoch and every live instance''s registry metadata says '
  'partition_ownership_level >= 1. Returns open, opened, or closed (reason).';

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: 330's definition, writing the ownership table with the map.
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
  -- 565: the members as published, and the ones published before, so the ownership table is written only on a change.
  v_members UUID[] := COALESCE(p_members, ARRAY[]::UUID[]);
  v_previous UUID[];
BEGIN
  -- The fence where the assignment is written: only the current holder of the role, at the epoch it was granted,
  -- may publish. A deposed assigner gets WHF01 and writes nothing. The assert takes the role row FOR SHARE, so a
  -- vote cannot hand the role on between the check and the write below.
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  -- 565 (#2555): once the service owns by map, an instance that does not support the mode is given no partition, so an
  -- older instance that joins after the switch owns nothing and claims nothing. Rank order is kept.
  IF __SCHEMA__.wh_partition_ownership_by_map() THEN
    SELECT COALESCE(array_agg(m.instance_id ORDER BY m.ordinal), ARRAY[]::UUID[])
    INTO v_members
    FROM unnest(v_members) WITH ORDINALITY AS m(instance_id, ordinal)
    WHERE EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_service_instances si
      WHERE si.instance_id = m.instance_id
        AND jsonb_typeof(si.metadata -> 'partition_ownership_level') = 'number'
        AND (si.metadata ->> 'partition_ownership_level')::NUMERIC >= 1);
  END IF;

  SELECT pa.members INTO v_previous FROM __SCHEMA__.wh_published_partition_assignment pa WHERE pa.singleton;

  INSERT INTO __SCHEMA__.wh_published_partition_assignment AS pa
    (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at)
  VALUES (TRUE, p_role, p_epoch, 1, p_instance_id, v_members, clock_timestamp(), p_lease, clock_timestamp() + p_lease)
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

  -- 565 (#2555): the ownership table follows the map, written in this statement so the role epoch fences it too.
  -- Partition p is owned by members[p mod n], the rank every claim already uses. Only a partition whose owner changes is
  -- written, and its epoch advances by one; a new partition starts at epoch 1. A map with no members owns nothing new,
  -- so the table keeps its owners (none of them is live, so none claims) until a map with members is published. The
  -- 10,000 rows are compared only when the members changed: a backstop republish of the same members writes nothing.
  IF cardinality(v_members) > 0
     AND (v_previous IS DISTINCT FROM v_members
          OR NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_partition_ownership o WHERE o.partition_number = 0)) THEN
    INSERT INTO __SCHEMA__.wh_partition_ownership AS o (partition_number, owner_instance_id, epoch, state)
    SELECT g.p, v_members[1 + (g.p % cardinality(v_members))], 1, 'active'
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

COMMENT ON FUNCTION __SCHEMA__.wh_publish_partition_assignment(TEXT, UUID, BIGINT, UUID[], INTERVAL) IS
  '203 (#1254): publishes the partition assignment. Fenced by wh_assert_role_epoch (WHF01 for a deposed assigner); '
  'the revision advances within an epoch and restarts at 1 for a new one; an older epoch is refused. Returns the '
  'revision. 330 (#2046): announces epoch:revision on the signal bus (wh_partition_assignment), recorded durably. '
  '565 (#2555): writes wh_partition_ownership with the map (members[p mod n], the epoch of each changed partition '
  'advancing), and once the service owns by map, leaves out members that do not support the mode.';
