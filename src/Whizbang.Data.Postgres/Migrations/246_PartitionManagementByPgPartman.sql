-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 246_PartitionManagementByPgPartman.sql
-- Date: 2026-10-26
-- Description: pg_partman as the partition manager's creator (#1752, WS-8 slice 8). With
--              Whizbang:Workers:Maintenance:Partitioning:Strategy resolving to pg_partman, each cycle registers a
--              converted table with pg_partman once (create_parent) and then lets it create partitions ahead
--              (run_maintenance). wh_partition_maintain still runs after it, and is what drops expired partitions:
--              pg_partman's own retention is kept off for every table it is given, because the drop must carry the
--              signals a live instance has not consumed first (#1758), reads its window from wh_settings each cycle,
--              keeps every row in debug mode and never waits for a lock. Anything pg_partman did not create, the
--              built-in pass still does.
--
--                _wh_partition_rebound  pg_partman cannot read a partition bounded below by MINVALUE (its maintenance
--                                       casts every lower bound to the key's type, logs a WARNING and skips the whole
--                                       set), which is how wh_partition_convert bounds a converted table's oldest
--                                       partition. This bounds it from the smallest value of its key instead
--                                       ('-infinity', or the empty uuid), the same rows, in three steps that each
--                                       commit and none of which holds a lock writers wait on during a scan: add a
--                                       NOT VALID check of the new range, validate it (SHARE UPDATE EXCLUSIVE), then
--                                       detach and re-attach the partition under the new bound, which the validated
--                                       check lets Postgres accept without scanning, and drop the check.
--                wh_partition_partman   one table's pg_partman pass: the rebound, create_parent once (premake = the
--                                       manager's create-ahead, starting at the first interval no partition covers,
--                                       no default table of its own, automatic maintenance off, the uuid encoder for
--                                       a time-ordered uuid key), the settings it must keep (premake, partitions made
--                                       from the clock rather than from the newest row, no retention), then
--                                       run_maintenance for that table
--
--              Number 246: 245 is taken by work in flight on another branch.
--
-- Dependencies: 226 (_wh_partition_start), 233 (_wh_partition_bound_at, the uuid key), 239 (wh_partition_maintain,
--               which still drops), pg_partman 5.2 or later when the strategy uses it (the uuid encoder arrived in 5.2)
-- Objects: _wh_partition_rebound, wh_partition_partman
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_partition_rebound');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_partman');

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 1: one step of re-bounding p_table's MINVALUE partition. Returns none (no such partition, or the table is not
-- partitioned), rebounding (a step was taken and committed with the caller's statement; call again), rebounded (done)
-- or deferred (a lock was not free; nothing changed). Never waits for a lock.
-- <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:AConvertedTablesOldestPartition_IsReboundedFromTheSmallestKey_InThreeStepsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:AUuidKeyedTablesOldestPartition_IsReboundedFromTheEmptyUuidAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:TheRebound_WhileTheTableIsInUse_IsDeferred_AndChangesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_partition_rebound(p_table TEXT)
RETURNS TEXT AS $$
DECLARE
  v_nsp OID;
  v_schema TEXT;
  v_parent OID;
  v_key TEXT;
  v_type REGTYPE;
  v_part OID;
  v_upper TEXT;
  v_lower TEXT;
  v_default OID;
  v_validated BOOLEAN;
  c_check CONSTANT TEXT := 'wh_partition_lower_bound';
BEGIN
  SELECT c.relnamespace INTO v_nsp FROM pg_class c WHERE c.oid = '__SCHEMA__.wh_settings'::regclass;
  SELECT n.nspname INTO v_schema FROM pg_namespace n WHERE n.oid = v_nsp;
  SELECT c.oid INTO v_parent FROM pg_class c WHERE c.relnamespace = v_nsp AND c.relname = p_table AND c.relkind = 'p';
  IF v_parent IS NULL THEN
    RETURN 'none';
  END IF;
  SELECT a.attname, a.atttypid::REGTYPE INTO v_key, v_type
  FROM pg_partitioned_table pt
  JOIN pg_attribute a ON a.attrelid = pt.partrelid AND a.attnum = pt.partattrs[0]
  WHERE pt.partrelid = v_parent;
  SELECT c.oid, btrim(m[1], '''') INTO v_part, v_upper
  FROM pg_inherits i
  JOIN pg_class c ON c.oid = i.inhrelid
  CROSS JOIN LATERAL regexp_match(pg_get_expr(c.relpartbound, c.oid), '^FOR VALUES FROM \(MINVALUE\) TO \((.+)\)$') m
  WHERE i.inhparent = v_parent AND m IS NOT NULL;
  IF v_part IS NULL THEN
    RETURN 'none';
  END IF;
  -- The smallest value of the key: the rows the partition holds are exactly the rows it held.
  v_lower := CASE WHEN v_type = 'uuid'::REGTYPE THEN __EMPTY_UUID__ ELSE '-infinity' END;
  SELECT con.convalidated INTO v_validated
  FROM pg_constraint con WHERE con.conrelid = v_part AND con.conname = c_check;

  BEGIN
    IF v_validated IS NULL THEN
      -- Step 1: the check, unvalidated. Adding it takes ACCESS EXCLUSIVE on the partition for a moment, never a scan.
      EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE NOWAIT', v_part::regclass);
      EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I CHECK (%I >= %L::%s AND %I < %L::%s) NOT VALID',
        v_part::regclass, c_check, v_key, v_lower, v_type, v_key, v_upper, v_type);
      RETURN 'rebounding';
    ELSIF NOT v_validated THEN
      -- Step 2: the scan, under SHARE UPDATE EXCLUSIVE, which neither readers nor writers wait on.
      EXECUTE format('LOCK TABLE %s IN SHARE UPDATE EXCLUSIVE MODE NOWAIT', v_part::regclass);
      EXECUTE format('ALTER TABLE %s VALIDATE CONSTRAINT %I', v_part::regclass, c_check);
      RETURN 'rebounding';
    END IF;
    -- Step 3: the new bound. The validated check proves it, so the attach does not scan the partition; the default
    -- partition is checked against the unchanged range.
    SELECT i.inhrelid INTO v_default
    FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
    WHERE i.inhparent = v_parent AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT';
    EXECUTE format('LOCK TABLE ONLY %I.%I, %s, %s IN ACCESS EXCLUSIVE MODE NOWAIT',
      v_schema, p_table, COALESCE(v_default, v_part)::regclass, v_part::regclass);
    EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %s', v_schema, p_table, v_part::regclass);
    EXECUTE format('ALTER TABLE %I.%I ATTACH PARTITION %s FOR VALUES FROM (%L) TO (%L)',
      v_schema, p_table, v_part::regclass, v_lower, v_upper);
    EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', v_part::regclass, c_check);
    RETURN 'rebounded';
  EXCEPTION WHEN lock_not_available THEN
    RETURN 'deferred';
  END;
END;
$$ LANGUAGE plpgsql SET timezone = 'UTC';

COMMENT ON FUNCTION __SCHEMA__._wh_partition_rebound(TEXT) IS
  'Issue #1752: takes one step of re-bounding the partition of p_table bounded below by MINVALUE so it is bounded from '
  'the smallest value of its key instead, which pg_partman can read: add a NOT VALID check, validate it, then detach and '
  're-attach under the new bound. Returns none, rebounding, rebounded or deferred. Never waits for a lock.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 2: one table's pg_partman pass. p_partman_schema names the schema pg_partman is installed in; NULL reads it
-- from pg_extension. pg_partman reads the server's clock, not p_now, when it decides what to create; p_now only picks
-- the interval create_parent starts at. Returns one row per thing done: rebounding, rebounded or deferred (from Part
-- 1, which comes first; until it is rebounded the table is not given to pg_partman), registered (create_parent),
-- configured (premake, clock-made partitions or retention put back as the manager needs them) and maintained
-- (run_maintenance), or skipped (the table is not partitioned, or another instance is maintaining it). pg_partman
-- reports a failed maintenance only as a WARNING, which reaches the caller as a notice.
-- <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:TheFirstPass_RegistersTheTableOnce_FromTheFirstUncoveredInterval_AndMaintainsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:AUuidKey_IsRegisteredWithTheVersion7EncoderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:RetentionSetOnTheTable_IsTurnedBackOff_SoTheBuiltInDropCarriesSignalsFirstAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:AConvertedTablesOldestPartition_IsReboundedFromTheSmallestKey_InThreeStepsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:WithoutPgPartman_ThePassRaisesNamingTheSettingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:APlainTable_IsSkippedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PgPartmanPartitionSqlTests.cs:ANegativeAhead_IsRejectedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_partman(
  p_table TEXT,
  p_interval INTERVAL,
  p_ahead INTEGER,
  p_partman_schema TEXT DEFAULT NULL,
  p_now TIMESTAMPTZ DEFAULT NOW()
) RETURNS TABLE(
  action TEXT,
  partition_name TEXT
) AS $$
DECLARE
  v_partman TEXT;
  v_nsp OID;
  v_schema TEXT;
  v_parent OID;
  v_key TEXT;
  v_type REGTYPE;
  v_rebound TEXT;
  v_parent_name TEXT;
  v_registered BOOLEAN;
  v_start TIMESTAMPTZ;
  v_encoding TEXT := '';
  v_changed INTEGER;
BEGIN
  IF p_ahead IS NULL OR p_ahead < 0 THEN
    RAISE EXCEPTION 'p_ahead must be zero or greater (was %)', p_ahead USING ERRCODE = 'invalid_parameter_value';
  END IF;
  v_partman := COALESCE(p_partman_schema,
    (SELECT n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_partman'));
  IF v_partman IS NULL THEN
    RAISE EXCEPTION 'pg_partman is not installed in this database, and Whizbang:Workers:Maintenance:Partitioning:Strategy uses it'
      USING ERRCODE = 'undefined_object',
            HINT = 'Install pg_partman, or set Whizbang:Workers:Maintenance:Partitioning:Strategy to Auto or BuiltIn.';
  END IF;

  -- The table is looked up in the schema wh_settings lives in, never the caller's search_path (README rule 14).
  SELECT c.relnamespace INTO v_nsp FROM pg_class c WHERE c.oid = '__SCHEMA__.wh_settings'::regclass;
  SELECT n.nspname INTO v_schema FROM pg_namespace n WHERE n.oid = v_nsp;
  SELECT c.oid INTO v_parent FROM pg_class c WHERE c.relnamespace = v_nsp AND c.relname = p_table AND c.relkind = 'p';
  IF v_parent IS NULL
     OR NOT pg_try_advisory_xact_lock(hashtextextended('wh_partition:' || v_schema || '.' || p_table, 0)) THEN
    RETURN QUERY SELECT 'skipped'::TEXT, p_table;
    RETURN;
  END IF;

  v_rebound := __SCHEMA__._wh_partition_rebound(p_table);
  IF v_rebound <> 'none' THEN
    RETURN QUERY SELECT v_rebound, p_table;
    IF v_rebound <> 'rebounded' THEN
      RETURN;
    END IF;
  END IF;

  SELECT a.attname, a.atttypid::REGTYPE INTO v_key, v_type
  FROM pg_partitioned_table pt
  JOIN pg_attribute a ON a.attrelid = pt.partrelid AND a.attnum = pt.partattrs[0]
  WHERE pt.partrelid = v_parent;
  v_parent_name := v_schema || '.' || p_table;
  EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I.part_config WHERE parent_table = %L)', v_partman, v_parent_name)
    INTO v_registered;

  IF NOT v_registered THEN
    -- pg_partman starts at the first interval no partition covers yet: a converted table's oldest partition holds the
    -- interval it was converted in, and the built-in manager may already have created some ahead.
    v_start := __SCHEMA__._wh_partition_start(p_now, p_interval);
    WHILE EXISTS (
      SELECT 1 FROM pg_inherits i
      JOIN pg_class c ON c.oid = i.inhrelid
      CROSS JOIN LATERAL regexp_match(pg_get_expr(c.relpartbound, c.oid), '^FOR VALUES FROM \((.+)\) TO \((.+)\)$') m
      WHERE i.inhparent = v_parent
        AND __SCHEMA__._wh_partition_bound_at(m[1], v_type) < v_start + p_interval
        AND __SCHEMA__._wh_partition_bound_at(m[2], v_type) > v_start) LOOP
      v_start := v_start + p_interval;
    END LOOP;
    IF v_type = 'uuid'::REGTYPE THEN
      v_encoding := format(', p_time_encoder := %L, p_time_decoder := %L',
        quote_ident(v_partman) || '.uuid7_time_encoder', quote_ident(v_partman) || '.uuid7_time_decoder');
    END IF;
    -- No default table (the manager keeps its own) and no automatic maintenance (this pass is the maintenance, run by
    -- the instance holding the maintainer duty).
    EXECUTE format('SELECT %I.create_parent(p_parent_table := %L, p_control := %L, p_interval := %L, p_premake := %s, '
      'p_start_partition := %L, p_default_table := false, p_automatic_maintenance := %L, p_jobmon := false%s)',
      v_partman, v_parent_name, v_key, p_interval::TEXT, p_ahead, v_start::TEXT, 'off', v_encoding);
    RETURN QUERY SELECT 'registered'::TEXT, p_table;
  END IF;

  -- Every cycle: premake follows the create-ahead option, partitions are made from the clock even while no row has
  -- reached the newest one, and retention stays off (the built-in drop carries unconsumed signals first).
  EXECUTE format('UPDATE %I.part_config SET premake = %s, infinite_time_partitions = true, retention = NULL '
    'WHERE parent_table = %L AND (premake IS DISTINCT FROM %s OR infinite_time_partitions IS DISTINCT FROM true '
    'OR retention IS NOT NULL)', v_partman, p_ahead, v_parent_name, p_ahead);
  GET DIAGNOSTICS v_changed = ROW_COUNT;
  IF v_changed > 0 AND v_registered THEN
    RETURN QUERY SELECT 'configured'::TEXT, p_table;
  END IF;

  EXECUTE format('SELECT %I.run_maintenance(p_parent_table := %L, p_jobmon := false)', v_partman, v_parent_name);
  RETURN QUERY SELECT 'maintained'::TEXT, p_table;
END;
$$ LANGUAGE plpgsql SET timezone = 'UTC';

COMMENT ON FUNCTION __SCHEMA__.wh_partition_partman(TEXT, INTERVAL, INTEGER, TEXT, TIMESTAMPTZ) IS
  'Issue #1752: one table''s pg_partman pass: re-bounds a MINVALUE partition first (_wh_partition_rebound), registers '
  'the table with create_parent once (premake p_ahead, no default table, automatic maintenance off), keeps premake, '
  'clock-made partitions and retention off in part_config, then calls run_maintenance for it. wh_partition_maintain '
  'still drops expired partitions, carrying unconsumed signals first. Never waits for a lock of its own.';
