-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 277_PreflightInstanceAndRoleColumns.sql
-- Date: 2026-11-01
-- Description: The columns of the instance tables and the startup stage recorder become Preflight's pre-migration
--              scripts (#1636), so a fresh database's instance row records its stage at registration.
--
--              An instance registers at the end of Preflight, after the bootstrap closure and before Elect, and writes
--              the stage it held until its row existed. On a fresh database that write used to fail: the stage columns
--              (231), the phase the recorder writes beside them (109) and the recorder itself (231) arrived only with the
--              main pass, so the row recorded nothing until Converge and every stage before it was in the log only.
--              This file's bootstrap region gives the closure every column a migration adds to an instance table
--              (109's lifecycle_phase and library_version, 203's connection_mode, 231's three stage columns, 110's
--              evicted_by on the eviction tombstones; 184 already carries the role tables' columns in its own region)
--              and wh_record_startup_stage, so the registration's write lands on every database.
--
--              The columns are added only when the catalog says they are missing, and described as they are added:
--              231 describes its columns only when it is the one adding them, and on a database the closure brought
--              up it no longer is. ADD COLUMN IF NOT EXISTS takes an ACCESS EXCLUSIVE lock even when the column
--              exists, and running instances read and write these tables on every beat and claim; an existing
--              database, on which every column is already there, takes no table lock from this file. The closure is
--              applied again whenever it changes, before the main pass, so the recorder is 231's definition word for
--              word: the closure makes it exist earlier and never makes it do something else, and a later migration
--              that redefines it carries the new definition into a bootstrap region of its own.
--
--              Outside the region nothing runs: the main pass applies this file as the redefinition closure's last word
--              for the recorder and the columns, and finds everything in place.
--
--              Number 277: reserved for this change.
--
-- Dependencies: the core tables (wh_service_instances) and 106's region (wh_instance_evictions), both earlier in the
--               closure; copies 109's, 110's, 203's and 231's columns with their descriptions, and 231's
--               wh_record_startup_stage verbatim (one arity, so no drop_all_overloads guard)
-- Objects: wh_service_instances, wh_instance_evictions, wh_record_startup_stage
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: registration writes the instance's stage through wh_record_startup_stage, onto columns of its own row.

-- ---------------------------------------------------------------------------------------------
-- The instance row's columns, added only when missing.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF (SELECT count(*) FROM pg_attribute
      WHERE attrelid = to_regclass('__SCHEMA__.wh_service_instances')
        AND attname IN ('lifecycle_phase', 'library_version', 'connection_mode', 'startup_stage',
                        'startup_stage_message', 'startup_stage_entered_at')
        AND NOT attisdropped) < 6 THEN
    ALTER TABLE __SCHEMA__.wh_service_instances
      ADD COLUMN IF NOT EXISTS lifecycle_phase TEXT,
      ADD COLUMN IF NOT EXISTS library_version TEXT,
      ADD COLUMN IF NOT EXISTS connection_mode TEXT NULL,
      ADD COLUMN IF NOT EXISTS startup_stage SMALLINT,
      ADD COLUMN IF NOT EXISTS startup_stage_message TEXT,
      ADD COLUMN IF NOT EXISTS startup_stage_entered_at TIMESTAMPTZ;

    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.lifecycle_phase IS
'The instance''s own report of its lifecycle phase (Starting/Connecting/Migrating/Running/StandingBy/...).
Written by record_instance_state on each transition; freshness is bounded by the row''s own heartbeat.';
    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.library_version IS
'The Whizbang library version the instance''s binary runs. During a mixed-version rollout this is
the first question anyone asks, and it is unanswerable from an instance that only knows its own.';
    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.connection_mode IS
      '203 (#1254): how the instance reaches the database, recorded at registration. direct = a dedicated direct '
      'connection, whose session alive-lock is authoritative for liveness with the heartbeat as the fallback; pooled = '
      'pooled connections only, judged by the heartbeat. NULL is judged as pooled.';
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
-- Who issued an eviction, added only when missing.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_instance_evictions')
                   AND attname = 'evicted_by'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_instance_evictions ADD COLUMN IF NOT EXISTS evicted_by UUID;

    COMMENT ON COLUMN __SCHEMA__.wh_instance_evictions.evicted_by IS
'The instance (or operator-driven process) that issued the eviction. NULL for reaper-written
tombstones (cleanup_stale_instances), which are the automatic staleness path.';
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------
-- wh_record_startup_stage: 231's definition, word for word.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/startup/startup-status#startup-stages</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Record_ARegisteredInstance_WritesTheStageMessageTimeAndPhaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Record_AnInstanceWithNoRow_ReportsNothingWrittenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageSqlTests.cs:Record_ProgressWithinAStage_KeepsWhenItWasEntered_AndANewStageStartsItsOwnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:AnEmptyDatabaseRecordsItsStageAtRegistrationAfterTheBootstrapAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PreflightInstanceColumnsTests.cs:TheBootstrapsStageRecorder_IsMigration231sDefinitionAsync</tests>
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
  'yet (expected before it registers). Not a heartbeat: last_heartbeat_at is never touched. 277: created by Preflight''s '
  'pre-migration scripts, so the registration''s own write lands.';

-- @whizbang:bootstrap-end
