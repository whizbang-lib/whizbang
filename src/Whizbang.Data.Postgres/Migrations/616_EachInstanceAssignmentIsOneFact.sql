-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 616_EachInstanceAssignmentIsOneFact.sql
-- Date: 2026-11-25
-- Description: Each instance's assignment is one readable fact (#2595, phase 3 of #1328).
--
--              What an instance was given to do sat in three tables (wh_service_instances, wh_role_assignments,
--              wh_partition_ownership) and nothing joined them. wh_instance_assignments() derives it, one row per
--              registered instance, from those tables alone, so it is never stored a second time and never drifts:
--              its roles (the assignments _role_lapse_reason calls valid, with their epochs), its share of the
--              partitions and the published map's revision, and the kind of assignment it has:
--
--                * evicted: tombstoned;
--                * draining: its recorded phase is past Running (pausing, paused, standing by, resuming, stopping,
--                  stopped, faulted, halted);
--                * ineligible: the service claims by the map and the instance does not support it (an older release,
--                  #2555), so a publish gives it nothing;
--                * starting: its recorded stage or phase does not yet make it eligible for work
--                  (wh_instance_is_eligible_for_work);
--                * awaiting-share: eligible, owning no partition. Readiness does not wait for a share (owner decision
--                  on #1328), so this is how a missing one is seen, for example while the assigner is unheld;
--                * role-holder: eligible, owning partitions and holding a role (a standard worker as well);
--                * standard-worker: eligible, owning partitions, holding no role.
--
--              The share: in map mode (wh_partition_ownership_by_map), a count of the ownership rows the instance
--              owns; before it, what a claim ranks by: the published explicit map (564), or the modulo of the member
--              list when none was published (203).
--
--              Cost: read by the status surface and operators, never on a hot path. One grouped count over the
--              10,000-row ownership table (or one pass over the 10,000 map entries before the switch), one probe of
--              the role table per role, and the liveness rule's one scan of the instances.
--
--              Outside the bootstrap region: the fleet read in Preflight asks whether the function exists before it
--              reads it, so a cold boot of this release reads the fleet without it.
--
-- Reversal: reversible (the down file drops the function)
-- Dependencies: 184 (wh_role_assignments), 203 (wh_published_partition_assignment), 231
--               (wh_instance_is_eligible_for_work), 316 (wh_live_instance_ids), 564 (partition_owners), 565
--               (wh_partition_ownership, wh_partition_ownership_by_map, wh_supports_ownership_by_map), 615
--               (_role_lapse_reason)
-- Objects: wh_instance_assignments
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_instance_assignments');

-- ============================================================================
-- wh_instance_assignments: one row per registered instance: what it was given
-- to do, derived from the tables that decide it.
--
-- <docs>operations/startup/startup-status#instance-assignments</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentsSqlTests.cs:Assignments_InMapMode_NameEveryKind_FromTheTablesThatDecideItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentsSqlTests.cs:Assignments_BeforeTheMapSwitch_CountTheShareFromThePublishedMapAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentsSqlTests.cs:Assignments_WithNoMapPublished_AreAwaitingTheirShare_AndALapsedRoleIsNotHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentsSqlTests.cs:Assignments_FollowTheDecidingTables_WithNoWriteOfTheirOwnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentDriverParityTests.cs:BothDrivers_RegisterTheFleetSourceAndTheAssignmentReader_AndAnswerAlikeAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_instance_assignments()
RETURNS TABLE(
  instance_id UUID,
  service_name TEXT,
  host_name TEXT,
  library_version TEXT,
  startup_stage SMALLINT,
  lifecycle_phase TEXT,
  eligible BOOLEAN,
  is_live BOOLEAN,
  evicted BOOLEAN,
  roles TEXT[],
  role_epochs BIGINT[],
  owned_partitions INTEGER,
  map_revision BIGINT,
  assignment TEXT
)
LANGUAGE sql
STABLE
AS $$
  WITH mode AS (
    SELECT __SCHEMA__.wh_partition_ownership_by_map() AS by_map
  ),
  pub AS (
    SELECT p.members, p.partition_owners, p.revision
      FROM __SCHEMA__.wh_published_partition_assignment p
     WHERE p.singleton
  ),
  share AS (
    -- Map mode: the ownership table is the only owner of a partition.
    SELECT o.owner_instance_id AS instance_id, count(*)::INTEGER AS owned
      FROM __SCHEMA__.wh_partition_ownership o
     WHERE (SELECT m.by_map FROM mode m)
     GROUP BY o.owner_instance_id
    UNION ALL
    -- Before it: the member each partition ranks to, by the explicit map or by the modulo.
    SELECT pub.members[CASE WHEN cardinality(pub.partition_owners) > 0 THEN pub.partition_owners[g.p + 1] + 1
                            ELSE (g.p % cardinality(pub.members)) + 1 END],
           count(*)::INTEGER
      FROM pub CROSS JOIN generate_series(0, 9999) AS g(p)
     WHERE NOT (SELECT m.by_map FROM mode m) AND cardinality(pub.members) > 0
     GROUP BY 1
  ),
  held AS (
    SELECT r.holder_instance_id AS instance_id,
           array_agg(r.role ORDER BY r.role) AS roles,
           array_agg(r.epoch ORDER BY r.role) AS epochs
      FROM __SCHEMA__.wh_role_assignments r
     WHERE r.holder_instance_id IS NOT NULL
       AND __SCHEMA__._role_lapse_reason(r) IS NULL
     GROUP BY r.holder_instance_id
  ),
  facts AS (
    SELECT i.instance_id, i.service_name, i.host_name, i.library_version, i.startup_stage, i.lifecycle_phase,
           i.metadata,
           __SCHEMA__.wh_instance_is_eligible_for_work(i) AS eligible,
           l.instance_id IS NOT NULL AS is_live,
           e.instance_id IS NOT NULL AS evicted,
           COALESCE(h.roles, '{}'::TEXT[]) AS roles,
           COALESCE(h.epochs, '{}'::BIGINT[]) AS role_epochs,
           COALESCE((SELECT sum(s.owned)::INTEGER FROM share s WHERE s.instance_id = i.instance_id), 0) AS owned
      FROM __SCHEMA__.wh_service_instances i
      LEFT JOIN __SCHEMA__.wh_instance_evictions e ON e.instance_id = i.instance_id
      LEFT JOIN __SCHEMA__.wh_live_instance_ids() l ON l.instance_id = i.instance_id
      LEFT JOIN held h ON h.instance_id = i.instance_id
  )
  SELECT f.instance_id, f.service_name, f.host_name, f.library_version, f.startup_stage, f.lifecycle_phase,
         f.eligible, f.is_live, f.evicted, f.roles, f.role_epochs, f.owned,
         (SELECT pub.revision FROM pub),
         CASE
           WHEN f.evicted THEN 'evicted'
           WHEN f.lifecycle_phase IN ('Pausing', 'Paused', 'StandingBy', 'Resuming', 'Stopping', 'Stopped', 'Faulted', 'Halted')
             THEN 'draining'
           WHEN (SELECT m.by_map FROM mode m) AND NOT __SCHEMA__.wh_supports_ownership_by_map(f.metadata) THEN 'ineligible'
           WHEN NOT f.eligible THEN 'starting'
           WHEN f.owned = 0 THEN 'awaiting-share'
           WHEN cardinality(f.roles) > 0 THEN 'role-holder'
           ELSE 'standard-worker'
         END
    FROM facts f
   ORDER BY f.instance_id
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_instance_assignments() IS
  '616 (#2595): each registered instance''s assignment as one fact, derived from wh_service_instances, '
  'wh_role_assignments and the partition ownership (or the published map before the switch): its valid roles, its '
  'share, and its kind (starting, standard-worker, role-holder, awaiting-share, draining, ineligible, evicted).';
