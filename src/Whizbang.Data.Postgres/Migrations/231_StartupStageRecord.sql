-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 231_StartupStageRecord.sql
-- Date: 2026-10-22
-- Description: Every instance records the startup stage it is in on its own row (#1636).
--
--              Startup had no recorded stages: the lifecycle label was all any instance or operator could see, and it
--              said Migrating while elections, the partition assigner and the alive-lock all ran. Each instance now
--              writes its stage on wh_service_instances as it enters it:
--                * startup_stage: a fixed number per stage, defined in code (StartupStage) and never a list position,
--                  so a query can compare it and a stored value means the same thing in every release. Preflight 10,
--                  Elect 20, Migrate 30, Converge 40, Coordinate 50, Accept commands 60, Running 70, Draining 80,
--                  Stopped 90. The gaps let a later stage slot in without renumbering;
--                * startup_stage_message: what the stage is doing, updated on progress within it;
--                * startup_stage_entered_at: when the stage was entered, by the database's clock. Progress within a
--                  stage keeps it.
--              The instance logs the same transition as "Stage X of 7: <name> - <message>". The row does not exist
--              until the instance registers, so a stage entered before then is held and written at registration.
--
--              wh_record_startup_stage writes the three columns and lifecycle_phase, which stays on the row mapped from
--              the stage for the readers and dashboards that read it. Like record_instance_state it is not a heartbeat:
--              it never touches last_heartbeat_at.
--
--              wh_instance_is_eligible_for_work (229) reads the stage: an instance that records one is eligible for
--              work only in Accept commands (60) or Running (70), so a starting instance is left out until the
--              transition into 60 has been written and a draining one is left out the moment Draining is. The
--              recorded lifecycle phase still has its say, so a paused or standing-by instance (a phase, not a stage)
--              stays out as it did under 229. A row that records no stage (an older build, or a row written before
--              this release's first stage write) is judged by the phase alone, exactly as under 229.
--
--              wh_instance_startup_stages is the operator's query: every registered instance with its stage, its
--              "X of 7" position (a shutdown stage has none), message, time in stage and whether it is eligible.
--
-- Locks: the columns are added only when the catalog says they are missing. ADD COLUMN IF NOT EXISTS takes an ACCESS
-- EXCLUSIVE lock on the table even when the column exists, and the running instances read and write this table on
-- every beat and claim; a re-run of this file (the redefinition closure re-runs it whenever 109 or 229 runs again)
-- must not queue behind them or in front of them. Nothing else here takes a table lock.
--
-- Dependencies: 109 (lifecycle_phase), 229 (wh_instance_is_eligible_for_work); 229's function copied and extended
-- Objects: wh_service_instances, wh_record_startup_stage, wh_instance_is_eligible_for_work, wh_instance_startup_stages

-- ---------------------------------------------------------------------------------------------
-- The stage columns, added only when missing.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF (SELECT count(*) FROM pg_attribute
      WHERE attrelid = to_regclass('__SCHEMA__.wh_service_instances')
        AND attname IN ('startup_stage', 'startup_stage_message', 'startup_stage_entered_at')
        AND NOT attisdropped) < 3 THEN
    ALTER TABLE __SCHEMA__.wh_service_instances
      ADD COLUMN IF NOT EXISTS startup_stage SMALLINT,
      ADD COLUMN IF NOT EXISTS startup_stage_message TEXT,
      ADD COLUMN IF NOT EXISTS startup_stage_entered_at TIMESTAMPTZ;

    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.startup_stage IS
      '231 (#1636): the startup stage the instance is in, a fixed number per stage (Preflight 10, Elect 20, Migrate 30, '
      'Converge 40, Coordinate 50, Accept commands 60, Running 70, Draining 80, Stopped 90). NULL: an older build, or '
      'no stage written yet.';
    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.startup_stage_message IS
      '231 (#1636): what the stage is doing, or why it was skipped; updated on progress within the stage.';
    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.startup_stage_entered_at IS
      '231 (#1636): when the instance entered its current stage, by the database''s clock.';
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------
-- wh_record_startup_stage: the one write of the stage.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_record_startup_stage');

-- <docs>operations/startup/startup-status#startup-stages</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Record_ARegisteredInstance_WritesTheStageMessageTimeAndPhaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Record_AnInstanceWithNoRow_ReportsNothingWrittenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Record_ProgressWithinAStage_KeepsWhenItWasEntered_AndANewStageStartsItsOwnAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_startup_stage(
  p_instance_id UUID,
  p_stage SMALLINT,
  p_message TEXT,
  p_lifecycle_phase TEXT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_service_instances
  SET startup_stage = p_stage,
      startup_stage_message = p_message,
      -- Progress within a stage keeps the time it was entered; a new stage starts its own.
      startup_stage_entered_at = CASE
        WHEN startup_stage IS NOT DISTINCT FROM p_stage AND startup_stage_entered_at IS NOT NULL
          THEN startup_stage_entered_at
        ELSE clock_timestamp()
      END,
      lifecycle_phase = COALESCE(p_lifecycle_phase, lifecycle_phase)
  WHERE instance_id = p_instance_id;

  RETURN FOUND;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_record_startup_stage(UUID, SMALLINT, TEXT, TEXT) IS
  '231 (#1636): records the startup stage an instance is in on its own row, with what it is doing and, when the stage '
  'changed, when it was entered; lifecycle_phase is written from the stage. Returns FALSE when the instance has no row '
  'yet (expected before it registers). Not a heartbeat: last_heartbeat_at is never touched.';

-- ---------------------------------------------------------------------------------------------
-- wh_instance_is_eligible_for_work: last word 229, reading the stage.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Eligible_EachRecordedStage_IsEligibleOnlyInAcceptCommandsOrRunningAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Eligible_ARowThatRecordsNoStage_IsJudgedByItsPhaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Eligible_ARunningInstanceThatIsPaused_IsNotEligibleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Eligible_EachRecordedPhase_IsEligibleOnlyWhenReadyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkEligibilitySqlTests.cs:Eligible_ARowThatRecordsNoPhase_IsEligibleAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_instance_is_eligible_for_work(p_instance __SCHEMA__.wh_service_instances)
RETURNS BOOLEAN
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
  -- 231: a recorded stage must be Accept commands (60) or Running (70). NULL: no stage recorded (an older build), so
  -- the phase decides alone, as under 229.
  SELECT (p_instance.startup_stage IS NULL OR p_instance.startup_stage IN (60, 70))
     -- 229: the recorded phase is AcceptingCommands or Running, or none at all (an older build, eligible whenever
     -- live). A pause or a stand-by is a phase, not a stage, and still keeps an instance out.
     AND (p_instance.lifecycle_phase IS NULL OR p_instance.lifecycle_phase IN ('AcceptingCommands', 'Running'))
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_instance_is_eligible_for_work(__SCHEMA__.wh_service_instances) IS
  '229 (#1638): whether an instance may be given work; alive is a separate test. 231 (#1636): a recorded startup stage '
  'must be Accept commands (60) or Running (70), and the recorded lifecycle phase AcceptingCommands or Running; a row '
  'that records neither (an older build) is eligible whenever live. The one definition read by the partition-assignment '
  'candidates and the claims'' peer rankings.';

-- ---------------------------------------------------------------------------------------------
-- wh_instance_startup_stages: the operator's view of every instance's stage.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_instance_startup_stages');

-- <docs>operations/startup/startup-status#startup-stages</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Stages_ListEveryInstance_WithItsPositionMessageAndTimeInStageAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_instance_startup_stages()
RETURNS TABLE(
  instance_id UUID,
  service_name TEXT,
  host_name TEXT,
  startup_stage SMALLINT,
  stage_name TEXT,
  -- X in "Stage X of Y": the stage's position among the startup stages; NULL for a shutdown stage.
  stage_position INTEGER,
  -- Y in "Stage X of Y": how many startup stages there are.
  stage_count INTEGER,
  startup_stage_message TEXT,
  startup_stage_entered_at TIMESTAMPTZ,
  time_in_stage INTERVAL,
  lifecycle_phase TEXT,
  eligible_for_work BOOLEAN
)
LANGUAGE sql
STABLE
SET timezone = 'UTC'
AS $$
  SELECT si.instance_id,
         si.service_name::TEXT,
         si.host_name::TEXT,
         si.startup_stage,
         stage.name,
         stage.position,
         7,
         si.startup_stage_message,
         si.startup_stage_entered_at,
         clock_timestamp() - si.startup_stage_entered_at,
         si.lifecycle_phase,
         __SCHEMA__.wh_instance_is_eligible_for_work(si)
  FROM __SCHEMA__.wh_service_instances si
  -- The stage list as StartupStages defines it in code; a stage added there is added here in the same release.
  LEFT JOIN (VALUES
      (10::SMALLINT, 'Preflight', 1),
      (20::SMALLINT, 'Elect', 2),
      (30::SMALLINT, 'Migrate', 3),
      (40::SMALLINT, 'Converge', 4),
      (50::SMALLINT, 'Coordinate', 5),
      (60::SMALLINT, 'Accept commands', 6),
      (70::SMALLINT, 'Running', 7),
      (80::SMALLINT, 'Draining', NULL),
      (90::SMALLINT, 'Stopped', NULL)
    ) AS stage(value, name, position) ON stage.value = si.startup_stage
  ORDER BY si.service_name ASC, si.instance_id ASC;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_instance_startup_stages() IS
  '231 (#1636): every registered instance with its startup stage, the stage''s name and "X of Y" position (NULL for '
  'a shutdown stage), message, when it was entered and how long ago, its lifecycle phase, and whether it is eligible '
  'for work. For operators: one row per instance, ordered by service.';
