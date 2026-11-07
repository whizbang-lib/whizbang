-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 353_TheHeartbeatStartsAtRegistration.sql
-- Date: 2026-11-07
-- Description: A starting instance beats from its registration, not from schema-ready (#2126).
--
--              An instance registers at the end of Preflight and used to beat only once its schema was ready, at the
--              end of Coordinate. Its row recorded no liveness window, so the one liveness rule (316) judged it by 30
--              seconds, and any live peer's reap evicted an instance still in Elect or Migrate (seen after a 63 second
--              Migrate). The heartbeat now starts at Elect, right after the registration, and keeps beating through
--              Elect, Migrate, Converge and Coordinate.
--
--              Those beats cannot go through record_heartbeat. It is not in the bootstrap closure: on a fresh database
--              it does not exist until the main pass, on an upgrade the database holds the earlier release's signature
--              until the pass replaces it, and its body reaps peers and renews the partition assignment, which need
--              tables the closure does not create. So a starting instance beats through wh_record_startup_heartbeat,
--              defined here in the bootstrap region so it exists from Preflight on:
--                * it refuses an evicted instance (wh_instance_evictions, 106's table, in the closure), as
--                  record_heartbeat does, so an evicted instance learns it the same way;
--                * it refreshes the heartbeat, the connection mode and the liveness window of an existing row, and
--                  nothing else: it never inserts a row (a row without a phase reads as an older build, eligible for
--                  work) and never writes the stage or the lifecycle phase, which only the stage record writes (278);
--                * it does not reap: a starting instance is not yet the fleet's reaper.
--              The registration records the connection mode and the window through it too, so the rule judges the
--              instance by its own window from the moment the row exists. Once the schema is ready the heartbeat goes
--              back to record_heartbeat, which registers, reaps and announces as before.
--
--              Number 353: the next free number after the ones taken on develop and in open pull requests.
--
-- Dependencies: 106 (wh_instance_evictions, in the closure), 277 (connection_mode, in the closure), 316
--               (liveness_window_seconds, in the closure)
-- Objects: wh_record_startup_heartbeat
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: a starting instance beats through this from its registration, before the main pass.

-- ---------------------------------------------------------------------------------------------
-- wh_record_startup_heartbeat: the beat of an instance still starting.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/workers/instance-liveness</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:AnEmptyDatabase_BeatsAStartingInstanceAfterTheBootstrap_BeforeAnyMigrationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:ARegistration_RecordsTheConnectionModeAndTheLivenessWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartingInstanceHeartbeatE2ETests.cs:AStartingInstanceHeldInMigrate_PastThirtySeconds_IsNotEvictedByALivePeersReapAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartingInstanceHeartbeatE2ETests.cs:AStartupBeat_OfAnEvictedInstance_IsRefused_AndOfAnUnregisteredOne_WritesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupHeartbeatTests.cs:AStartupBeat_NeverMovesTheStageOrThePhase_AndRecordsTheModeAndTheWindowAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupHeartbeatTests.cs:AStartupBeat_OfAnEvictedInstance_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupHeartbeatTests.cs:AStartupBeat_OfAnInstanceWithNoRow_InsertsNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_startup_heartbeat(
  p_instance_id UUID,
  p_connection_mode TEXT,
  p_liveness_window_seconds INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  -- An evicted instance is refused, exactly as record_heartbeat refuses it (106): its leases went to its peers, and it
  -- must not believe it is still part of the fleet.
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions WHERE instance_id = p_instance_id) THEN
    RETURN FALSE;
  END IF;

  -- Only an existing row is refreshed, and only its liveness: the stage and the phase are the stage record's (278).
  -- A NULL mode or window keeps what is recorded.
  UPDATE __SCHEMA__.wh_service_instances
  SET last_heartbeat_at = NOW(),
      connection_mode = COALESCE(p_connection_mode, connection_mode),
      liveness_window_seconds = COALESCE(p_liveness_window_seconds, liveness_window_seconds)
  WHERE instance_id = p_instance_id;

  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_record_startup_heartbeat(UUID, TEXT, INTEGER) IS
  '353 (#2126): the beat of an instance still starting, from its registration at the end of Preflight until its schema '
  'is ready. Refreshes the heartbeat, the connection mode and the liveness window of the instance''s existing row (a '
  'NULL keeps the recorded value) and nothing else: no row is inserted, the stage and the lifecycle phase are left to '
  'the stage record (wh_record_instance_transition), and no peer is reaped. Returns FALSE when the instance has been '
  'evicted (wh_instance_evictions), TRUE otherwise, whether or not a row was there to refresh.';

-- @whizbang:bootstrap-end
