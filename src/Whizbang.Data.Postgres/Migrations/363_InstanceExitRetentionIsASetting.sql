-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 363_InstanceExitRetentionIsASetting.sql
-- Date: 2026-11-07
-- Description: How long the record of an instance's exits is kept is an operator setting (#2126).
--
--              362's wh_record_instance_exit removed rows of wh_instance_exits older than a fixed 30 days with each
--              write. The window is now wh_settings.instance_exit_retention_days, published from
--              Whizbang:Workers:Maintenance:Retention:InstanceExitDays like every other retention window, and 30 days
--              when nothing sets it. wh_record_instance_exit is redefined whole with that change, in PL/pgSQL so the
--              settings table can be probed (see the body); its signature and result are 362's.
--
--              In the bootstrap region, as 362's definition is: a starting instance can exit before the main pass, and
--              the closure applies the regions in file order, so this definition is the one it leaves.
--
--              Number 363: above every number on develop (through 355) and in open pull requests (357).
--
-- Dependencies: 362 (wh_instance_exits, wh_record_instance_exit), wh_settings
-- Objects: wh_record_instance_exit
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: an instance can exit, and record it, from its registration on.

-- ---------------------------------------------------------------------------------------------
-- wh_record_instance_exit: one exit's row, with the instance's tombstone read in the same call.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/startup/instance-exits#the-exit-record</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AnExit_IsRecordedWithEveryFact_AndNoTombstoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AnEvictedInstancesExit_RecordsAndReturnsTheTombstoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AWrite_RemovesRowsOlderThanThirtyDaysAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs:AWrite_RemovesRowsOlderThanTheConfiguredRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperInstanceExitTests.cs:AnEvictedInstancesExit_RecordsAndReturnsTheTombstoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_instance_exit(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_exit_cause TEXT,
  p_exit_code INTEGER,
  p_intended BOOLEAN,
  p_reason TEXT,
  p_exception_type TEXT,
  p_exception_message TEXT,
  p_startup_stage TEXT,
  p_lifecycle_phase TEXT,
  p_uptime_seconds DOUBLE PRECISION,
  p_seconds_since_last_heartbeat DOUBLE PRECISION,
  p_held_roles TEXT[],
  p_held_leases INTEGER,
  p_in_hand INTEGER,
  p_library_version TEXT
) RETURNS TABLE(
  recorded_exit_id BIGINT,
  tombstone_evicted_at TIMESTAMPTZ,
  tombstone_evicted_by UUID,
  tombstone_reason TEXT,
  tombstone_last_heartbeat_at TIMESTAMPTZ,
  tombstone_liveness_window_seconds INTEGER
)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_days INTEGER;
BEGIN
  -- Exits are rare, so the retention runs with each write rather than as a maintenance step. The window is the
  -- operator's (363): wh_settings.instance_exit_retention_days, 30 days when unset. PL/pgSQL, and the settings table
  -- probed first, because this definition is in the bootstrap closure, which runs before the migration that creates
  -- wh_settings on a new database.
  IF to_regclass('__SCHEMA__.wh_settings') IS NOT NULL THEN
    SELECT s.setting_value::INTEGER INTO v_days
      FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'instance_exit_retention_days';
  END IF;
  DELETE FROM __SCHEMA__.wh_instance_exits x WHERE x.exited_at < NOW() - make_interval(days => COALESCE(v_days, 30));

  RETURN QUERY
  WITH tombstone AS (
    SELECT e.evicted_at, e.evicted_by, e.reason, e.last_heartbeat_at, e.liveness_window_seconds
    FROM __SCHEMA__.wh_instance_evictions e
    WHERE e.instance_id = p_instance_id
  ),
  written AS (
    INSERT INTO __SCHEMA__.wh_instance_exits
      (instance_id, service_name, host_name, process_id, exit_cause, exit_code, intended, reason, exception_type,
       exception_message, startup_stage, lifecycle_phase, uptime_seconds, seconds_since_last_heartbeat, held_roles,
       held_leases, in_hand, library_version, evicted_at, evicted_by, eviction_reason, evicted_last_heartbeat_at,
       evicted_liveness_window_seconds)
    SELECT p_instance_id, p_service_name, p_host_name, p_process_id, p_exit_cause, p_exit_code, p_intended, p_reason,
           p_exception_type, p_exception_message, p_startup_stage, p_lifecycle_phase, p_uptime_seconds,
           p_seconds_since_last_heartbeat, COALESCE(p_held_roles, ARRAY[]::TEXT[]), COALESCE(p_held_leases, 0),
           COALESCE(p_in_hand, 0), p_library_version, t.evicted_at, t.evicted_by, t.reason, t.last_heartbeat_at,
           t.liveness_window_seconds
    FROM (SELECT 1) AS one
    LEFT JOIN tombstone t ON TRUE
    RETURNING exit_id, evicted_at, evicted_by, eviction_reason, evicted_last_heartbeat_at, evicted_liveness_window_seconds
  )
  SELECT w.exit_id, w.evicted_at, w.evicted_by, w.eviction_reason, w.evicted_last_heartbeat_at,
         w.evicted_liveness_window_seconds
  FROM written w;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_record_instance_exit(UUID, TEXT, TEXT, INTEGER, TEXT, INTEGER, BOOLEAN, TEXT, TEXT, TEXT, TEXT, TEXT, DOUBLE PRECISION, DOUBLE PRECISION, TEXT[], INTEGER, INTEGER, TEXT) IS
  '362, 363 (#2126): writes one row of wh_instance_exits for an exit of p_instance_id, with the instance''s eviction '
  'tombstone when it has one, and returns the row''s id and the tombstone''s facts (NULL when it has none), so the '
  'exit''s log entry can state them. Removes rows older than wh_settings.instance_exit_retention_days (30 when unset).';

-- @whizbang:bootstrap-end
