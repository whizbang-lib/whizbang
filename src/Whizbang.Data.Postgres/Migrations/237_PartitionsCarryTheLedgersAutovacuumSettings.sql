-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 237_PartitionsCarryTheLedgersAutovacuumSettings.sql
-- Date: 2026-10-24
-- Description: Every partition the partition manager makes carries the autovacuum settings declared for its ledger
--              (#1693, follows #1611 and #1646).
--
--              222 set wh_lifecycle_completions to vacuum after a fixed 10000 dead rows (scale factor 0). Once the
--              manager converts the table (233), those settings stay on the legacy partition alone: a partitioned
--              parent cannot hold autovacuum settings, and wh_partition_maintain created each new daily partition,
--              and the default partition, with none, so they vacuumed at the server's defaults.
--
--                _wh_partition_storage  the storage settings declared for a managed table's partitions, NULL for a
--                                       table that declares none; wh_lifecycle_completions declares 222's values
--                wh_partition_maintain  writes those settings into every partition it creates (CREATE TABLE ...
--                                       WITH), and gives them to a partition that has none of them (the conversion's
--                                       default partition, or a partition made before 237), reported as tuned. A
--                                       partition that already sets any of them was tuned by hand and is left alone;
--                                       one in use by a vacuum is tuned on a later cycle (deferred), never waited for
--
--              Number 237: 236 is the dead-letter re-entry change.
--
-- Dependencies: 222 (wh_lifecycle_completions' settings), 233 (wh_partition_maintain, last word)
-- Objects: _wh_partition_storage, wh_partition_maintain
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_partition_storage');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_maintain');

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 1: the declaration. One row per managed table that has settings, written as pg_class.reloptions stores them,
-- so the manager can both apply them and tell whether a partition already has them.
-- <docs>operations/infrastructure/partitioned-ledgers#autovacuum-settings</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LifecycleCompletionPartitionConversionTests.cs:PartitionsTheManagerCreates_CarryTheLedgersAutovacuumSettingsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:ACycle_CreatesTheCurrentAndAheadPartitions_AndASecondCreatesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_partition_storage(p_table TEXT)
RETURNS TEXT[] AS $$
  SELECT CASE p_table
    -- 222: vacuum after a fixed count of dead rows, not a share of a table that grows with the data.
    WHEN 'wh_lifecycle_completions' THEN ARRAY['autovacuum_vacuum_scale_factor=0', 'autovacuum_vacuum_threshold=10000']
  END;
$$ LANGUAGE sql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_partition_storage(TEXT) IS
  'Issue #1693: the storage settings (as pg_class.reloptions writes them) every partition of p_table carries, or NULL '
  'when the table declares none. wh_lifecycle_completions declares the autovacuum settings migration 222 set.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 2: the manager, last word 233_PartitionSignalsAndLifecycleCompletions.sql, copied whole. The changes: each
-- CREATE TABLE carries the table's declared settings, and partitions that have none of them are given them.
-- <docs>operations/infrastructure/partitioned-ledgers#what-a-cycle-does</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:ACycle_CreatesTheCurrentAndAheadPartitions_AndASecondCreatesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:APartitionWhollyPastTheWindow_IsDropped_OneStraddlingItIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:AnExpiredPartition_IsEmptiedWithoutBlockingAnInsertInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:RowsInTheDefaultPartition_MoveIntoThePartitionCreatedForThemAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:ACycle_CreatesDailyIdPartitions_AndAVersion7IdLandsInItsDayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:APartitionWhollyPastTheWindow_IsDropped_OneStraddlingItIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:IdsInTheDefaultPartition_MoveIntoThePartitionCreatedForThemAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LifecycleCompletionPartitionConversionTests.cs:PartitionsTheManagerCreates_CarryTheLedgersAutovacuumSettingsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LifecycleCompletionPartitionConversionTests.cs:TheConversionsDefaultPartition_GetsTheSettingsOnTheNextCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LifecycleCompletionPartitionConversionTests.cs:APartitionAnOperatorTuned_KeepsItsOwnSettingsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LifecycleCompletionPartitionConversionTests.cs:APartitionInUse_IsTunedOnALaterCycle_WithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionManagerSqlTests.cs:TheLifecycleCompletionsPartitions_CarryTheLedgersAutovacuumSettingsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_maintain(
  p_table TEXT,
  p_interval INTERVAL,
  p_ahead INTEGER,
  p_keep INTERVAL,
  p_now TIMESTAMPTZ DEFAULT NOW()
) RETURNS TABLE(
  action TEXT,
  partition_name TEXT,
  range_from TIMESTAMPTZ,
  range_to TIMESTAMPTZ
) AS $$
DECLARE
  v_nsp OID;
  v_schema TEXT;
  v_parent OID;
  v_kind TEXT;
  v_key TEXT;
  v_type REGTYPE;
  v_default OID;
  v_step DOUBLE PRECISION;
  v_start TIMESTAMPTZ;
  v_lo TIMESTAMPTZ;
  v_hi TIMESTAMPTZ;
  v_name TEXT;
  v_debug BOOLEAN;
  v_part RECORD;
  v_storage TEXT[];
  v_with TEXT;
BEGIN
  v_start := __SCHEMA__._wh_partition_start(p_now, p_interval);
  v_step := EXTRACT(EPOCH FROM p_interval);
  IF p_ahead IS NULL OR p_ahead < 0 THEN
    RAISE EXCEPTION 'p_ahead must be zero or greater (was %)', p_ahead USING ERRCODE = 'invalid_parameter_value';
  END IF;

  -- The table is looked up in the schema wh_settings lives in, never the caller's search_path (README rule 14).
  SELECT c.relnamespace INTO v_nsp FROM pg_class c WHERE c.oid = '__SCHEMA__.wh_settings'::regclass;
  SELECT n.nspname INTO v_schema FROM pg_namespace n WHERE n.oid = v_nsp;
  SELECT c.oid, c.relkind INTO v_parent, v_kind FROM pg_class c WHERE c.relnamespace = v_nsp AND c.relname = p_table;
  IF v_parent IS NULL OR v_kind <> 'p' THEN
    RETURN QUERY SELECT 'skipped'::TEXT, p_table, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
    RETURN;
  END IF;

  SELECT a.attname, a.atttypid::REGTYPE INTO v_key, v_type
  FROM pg_partitioned_table pt
  JOIN pg_attribute a ON a.attrelid = pt.partrelid AND a.attnum = pt.partattrs[0]
  WHERE pt.partrelid = v_parent AND pt.partstrat = 'r' AND pt.partnatts = 1
    AND a.atttypid IN ('timestamptz'::regtype, 'uuid'::regtype);
  IF v_key IS NULL THEN
    RAISE EXCEPTION '%.% is not range-partitioned on one timestamptz or uuid column', v_schema, p_table
      USING ERRCODE = 'invalid_parameter_value';
  END IF;

  -- Two instances never change one table's partitions at once: the loser of this key skips the cycle.
  IF NOT pg_try_advisory_xact_lock(hashtextextended('wh_partition:' || v_schema || '.' || p_table, 0)) THEN
    RETURN QUERY SELECT 'skipped'::TEXT, p_table, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
    RETURN;
  END IF;

  -- 237: the storage settings declared for this table's partitions (a partitioned parent holds none of its own), written
  -- into every CREATE TABLE below, so a new partition never starts on the server's defaults.
  v_storage := __SCHEMA__._wh_partition_storage(p_table);
  v_with := CASE WHEN v_storage IS NULL THEN '' ELSE ' WITH (' || array_to_string(v_storage, ', ') || ')' END;

  -- The default partition catches a row no partition covers, so a writer never fails for want of one.
  SELECT i.inhrelid INTO v_default
  FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
  WHERE i.inhparent = v_parent AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT';
  IF v_default IS NULL THEN
    BEGIN
      EXECUTE format('LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE NOWAIT', v_schema, p_table);
      EXECUTE format('CREATE TABLE %I.%I PARTITION OF %I.%I DEFAULT%s', v_schema, p_table || '_default', v_schema, p_table, v_with);
      v_default := format('%I.%I', v_schema, p_table || '_default')::regclass;
      RETURN QUERY SELECT 'created'::TEXT, p_table || '_default', NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
    EXCEPTION WHEN lock_not_available THEN
      RETURN QUERY SELECT 'deferred'::TEXT, p_table || '_default', NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
    END;
  END IF;

  -- The current interval and p_ahead more. A range any existing partition overlaps is left alone, which is how a
  -- converted table's oldest partition (everything before the interval after its conversion) is respected.
  FOR i IN 0..p_ahead LOOP
    v_lo := to_timestamp(EXTRACT(EPOCH FROM v_start) + i * v_step);
    v_hi := to_timestamp(EXTRACT(EPOCH FROM v_start) + (i + 1) * v_step);
    CONTINUE WHEN EXISTS (
      SELECT 1 FROM pg_inherits i2
      JOIN pg_class c ON c.oid = i2.inhrelid
      CROSS JOIN LATERAL regexp_match(pg_get_expr(c.relpartbound, c.oid), '^FOR VALUES FROM \((.+)\) TO \((.+)\)$') m
      WHERE i2.inhparent = v_parent
        AND __SCHEMA__._wh_partition_bound_at(m[1], v_type) < v_hi
        AND __SCHEMA__._wh_partition_bound_at(m[2], v_type) > v_lo);
    v_name := p_table || '_p' || to_char(v_lo, CASE WHEN v_step::BIGINT % 86400 = 0 THEN 'YYYYMMDD' ELSE 'YYYYMMDD"_"HH24MI' END);
    BEGIN
      IF v_default IS NOT NULL THEN
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE NOWAIT', v_default::regclass);
      END IF;
      EXECUTE format('CREATE TABLE %I.%I (LIKE %I.%I INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE)%s',
        v_schema, v_name, v_schema, p_table, v_with);
      IF v_default IS NOT NULL THEN
        -- Rows the default partition caught for this range move with it; attaching otherwise fails.
        EXECUTE format('WITH moved AS (DELETE FROM %s WHERE %I >= %L::%s AND %I < %L::%s RETURNING *) INSERT INTO %I.%I SELECT * FROM moved',
          v_default::regclass, v_key, __SCHEMA__._wh_partition_bound(v_lo, v_type), v_type,
          v_key, __SCHEMA__._wh_partition_bound(v_hi, v_type), v_type, v_schema, v_name);
      END IF;
      EXECUTE format('ALTER TABLE %I.%I ATTACH PARTITION %I.%I FOR VALUES FROM (%L) TO (%L)',
        v_schema, p_table, v_schema, v_name, __SCHEMA__._wh_partition_bound(v_lo, v_type), __SCHEMA__._wh_partition_bound(v_hi, v_type));
      RETURN QUERY SELECT 'created'::TEXT, v_name, v_lo, v_hi;
    EXCEPTION WHEN lock_not_available THEN
      RETURN QUERY SELECT 'deferred'::TEXT, v_name, v_lo, v_hi;
    END;
  END LOOP;

  -- 237: a partition made before its table declared settings (the conversion's default partition, or one created
  -- before 237) is given them. One that already sets any of them was tuned by hand and is left as it is. Changing a
  -- table's settings takes SHARE UPDATE EXCLUSIVE, which writers do not conflict with but a running vacuum does, so
  -- it is taken only when free and the partition is otherwise tuned on a later cycle.
  IF v_storage IS NOT NULL THEN
    FOR v_part IN
      SELECT c.oid AS relid, c.relname
      FROM pg_inherits i2
      JOIN pg_class c ON c.oid = i2.inhrelid
      WHERE i2.inhparent = v_parent
        AND NOT EXISTS (
          SELECT 1 FROM unnest(c.reloptions) AS r(opt)
          JOIN unnest(v_storage) AS d(opt) ON split_part(r.opt, '=', 1) = split_part(d.opt, '=', 1))
      ORDER BY c.relname
    LOOP
      BEGIN
        EXECUTE format('LOCK TABLE %s IN SHARE UPDATE EXCLUSIVE MODE NOWAIT', v_part.relid::regclass);
        EXECUTE format('ALTER TABLE %s SET (%s)', v_part.relid::regclass, array_to_string(v_storage, ', '));
        RETURN QUERY SELECT 'tuned'::TEXT, v_part.relname::TEXT, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
      EXCEPTION WHEN lock_not_available THEN
        RETURN QUERY SELECT 'deferred'::TEXT, v_part.relname::TEXT, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
      END;
    END LOOP;
  END IF;

  IF p_keep IS NULL THEN
    RETURN;
  END IF;
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'), FALSE)
    INTO v_debug;

  FOR v_part IN
    SELECT p.relid, p.relname, p.lo, p.hi
    FROM (
      SELECT c.oid AS relid, c.relname,
        __SCHEMA__._wh_partition_bound_at(m[1], v_type) AS lo,
        __SCHEMA__._wh_partition_bound_at(m[2], v_type) AS hi
      FROM pg_inherits i2
      JOIN pg_class c ON c.oid = i2.inhrelid
      CROSS JOIN LATERAL regexp_match(pg_get_expr(c.relpartbound, c.oid), '^FOR VALUES FROM \((.+)\) TO \((.+)\)$') m
      WHERE i2.inhparent = v_parent
    ) p
    WHERE p.hi <= p_now - p_keep
    ORDER BY p.hi
  LOOP
    IF v_debug THEN
      -- debug_mode keeps every row for forensics, as the row sweeps do (README rule 7).
      RETURN QUERY SELECT 'kept'::TEXT, v_part.relname::TEXT, v_part.lo, v_part.hi;
      CONTINUE;
    END IF;
    BEGIN
      EXECUTE format('LOCK TABLE ONLY %I.%I, %s, %s IN ACCESS EXCLUSIVE MODE NOWAIT',
        v_schema, p_table, COALESCE(v_default, v_part.relid)::regclass, v_part.relid::regclass);
      EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %s', v_schema, p_table, v_part.relid::regclass);
      EXECUTE format('DROP TABLE %s', v_part.relid::regclass);
      RETURN QUERY SELECT 'dropped'::TEXT, v_part.relname::TEXT, v_part.lo, v_part.hi;
    EXCEPTION WHEN lock_not_available THEN
      -- Something is using the table. Empty the partition, which locks it alone; it is detached on a later cycle.
      BEGIN
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE NOWAIT', v_part.relid::regclass);
        EXECUTE format('TRUNCATE %s', v_part.relid::regclass);
        RETURN QUERY SELECT 'emptied'::TEXT, v_part.relname::TEXT, v_part.lo, v_part.hi;
      EXCEPTION WHEN lock_not_available THEN
        RETURN QUERY SELECT 'deferred'::TEXT, v_part.relname::TEXT, v_part.lo, v_part.hi;
      END;
    END;
  END LOOP;
END;
$$ LANGUAGE plpgsql SET timezone = 'UTC';

COMMENT ON FUNCTION __SCHEMA__.wh_partition_maintain(TEXT, INTERVAL, INTEGER, INTERVAL, TIMESTAMPTZ) IS
  'Issue #1611: creates the partitions of a range-partitioned framework table for the current interval and p_ahead '
  'more, ensures its default partition, and drops (or, while the table is in use, empties) partitions that ended at '
  'or before p_now - p_keep. Never waits for a lock. Called by the partition maintenance step. '
  'Issue #1646: the key is a timestamptz or a time-ordered (version-7) uuid, bounded by the first id of each moment. '
  'Issue #1693: every partition carries the storage settings _wh_partition_storage declares for its table.';

