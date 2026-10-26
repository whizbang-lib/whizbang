-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 239_PartitionDropsCarryUnconsumedSignals.sql
-- Date: 2026-10-24
-- Description: Before the partition manager drops a wh_signals partition, it moves the signals a live instance has
--              not consumed into the current partition, then drops the old one (#1758, decided in #1692).
--
--              The tail delivers a signal once its id is past the instance's cursor (wh_signal_cursors), and the row
--              sweep never deletes past the slowest cursor. A partition drop removes a whole day by age and cannot
--              apply that guard, so an instance more than the window behind lost the signals in the dropped day.
--
--                _wh_partition_carry    the rows of one expiring partition its table still needs, moved to the
--                                       partition for the cycle's moment. Only wh_signals has such rows: those with
--                                       an id past the slowest live instance's cursor. An instance is live while it
--                                       is registered in wh_service_instances, which only the stale-instance reap
--                                       removes, so a dead instance's cursor never holds a row. With no live cursor
--                                       or no lag, nothing moves. The move is an UPDATE of created_at through the
--                                       parent, a cross-partition move; the id is unchanged, so the tail's
--                                       id > cursor ORDER BY id keeps its order, and the dead tuples left behind go
--                                       with the partition. A carried signal starts a fresh retention window.
--                wh_partition_maintain  carries before it drops, in the same step and under the same locks, and
--                                       before it empties a partition while the table is in use (the NOWAIT
--                                       fallback); reported as carried, before dropped or emptied
--
--              Number 239: 238 is the wh_log default-partition sweep (#1694).
--
-- Dependencies: 065 (wh_signals, wh_signal_cursors), 237 (wh_partition_maintain, last word), wh_service_instances
-- Objects: _wh_partition_carry, wh_partition_maintain
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_partition_carry');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_maintain');

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 1: what an expiring partition keeps. Moves, through the parent, the rows of p_table between p_from and p_to
-- that the table still needs to the partition for p_now, and returns how many moved. The parent is taken in ROW
-- EXCLUSIVE mode first without waiting, so a caller that holds only the partition (the emptied path) never waits on
-- a writer's stronger lock; a refusal raises lock_not_available for the caller to defer on.
-- <docs>operations/infrastructure/partitioned-ledgers#signals</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:ALiveInstanceBehindTheWindow_KeepsEveryUnconsumedSignalAcrossTheDrop_AndReceivesThemInOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:ADeadInstancesCursor_CarriesNothing_AndThePartitionIsDroppedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:WithNoLag_NothingIsCarried_AndThePartitionIsDroppedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:ALiveInstanceBehind_KeepsItsSignals_WhenTheTableIsInUseAndThePartitionIsOnlyEmptiedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_partition_carry(
  p_table TEXT,
  p_from TIMESTAMPTZ,
  p_to TIMESTAMPTZ,
  p_now TIMESTAMPTZ
) RETURNS BIGINT AS $$
DECLARE
  v_floor BIGINT;
  v_moved BIGINT;
BEGIN
  IF p_table <> 'wh_signals' THEN
    RETURN 0;
  END IF;
  -- The slowest live instance's cursor: a cursor counts only while its instance is registered.
  SELECT min(c.last_delivered_signal_id) INTO v_floor
  FROM __SCHEMA__.wh_signal_cursors c
  JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = c.instance_id;
  IF v_floor IS NULL THEN
    RETURN 0;
  END IF;
  LOCK TABLE ONLY __SCHEMA__.wh_signals IN ROW EXCLUSIVE MODE NOWAIT;
  WITH moved AS (
    UPDATE __SCHEMA__.wh_signals s
       SET created_at = p_now
     WHERE s.created_at >= p_from AND s.created_at < p_to AND s.id > v_floor
    RETURNING 1)
  SELECT count(*) INTO v_moved FROM moved;
  RETURN v_moved;
END;
$$ LANGUAGE plpgsql SET timezone = 'UTC';

COMMENT ON FUNCTION __SCHEMA__._wh_partition_carry(TEXT, TIMESTAMPTZ, TIMESTAMPTZ, TIMESTAMPTZ) IS
  'Issue #1758: moves the rows of p_table between p_from and p_to that the table still needs to the partition for '
  'p_now and returns the count. Only wh_signals has such rows: those past the slowest registered instance''s tail '
  'cursor. Called by wh_partition_maintain before it drops or empties a partition.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 2: the manager, last word 237_PartitionsCarryTheLedgersAutovacuumSettings.sql, copied whole. The change: an
-- expiring partition's rows the table still needs are carried to the current partition before it is dropped or
-- emptied.
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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:ALiveInstanceBehindTheWindow_KeepsEveryUnconsumedSignalAcrossTheDrop_AndReceivesThemInOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:ADeadInstancesCursor_CarriesNothing_AndThePartitionIsDroppedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:WithNoLag_NothingIsCarried_AndThePartitionIsDroppedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionCarryForwardTests.cs:ALiveInstanceBehind_KeepsItsSignals_WhenTheTableIsInUseAndThePartitionIsOnlyEmptiedAsync</tests>
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
  v_carried BIGINT;
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
      -- 239: rows the table still needs move to the current partition first, in the same step as the drop.
      v_carried := __SCHEMA__._wh_partition_carry(p_table, v_part.lo, v_part.hi, p_now);
      EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %s', v_schema, p_table, v_part.relid::regclass);
      EXECUTE format('DROP TABLE %s', v_part.relid::regclass);
      IF v_carried > 0 THEN
        RETURN QUERY SELECT 'carried'::TEXT, v_part.relname::TEXT, v_part.lo, v_part.hi;
      END IF;
      RETURN QUERY SELECT 'dropped'::TEXT, v_part.relname::TEXT, v_part.lo, v_part.hi;
    EXCEPTION WHEN lock_not_available THEN
      -- Something is using the table. Empty the partition, which locks it alone; it is detached on a later cycle.
      -- 239: the rows the table still needs are carried out of it first, as on the drop.
      BEGIN
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE NOWAIT', v_part.relid::regclass);
        v_carried := __SCHEMA__._wh_partition_carry(p_table, v_part.lo, v_part.hi, p_now);
        EXECUTE format('TRUNCATE %s', v_part.relid::regclass);
        IF v_carried > 0 THEN
          RETURN QUERY SELECT 'carried'::TEXT, v_part.relname::TEXT, v_part.lo, v_part.hi;
        END IF;
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
  'Issue #1693: every partition carries the storage settings _wh_partition_storage declares for its table. '
  'Issue #1758: before a partition is dropped or emptied, the rows its table still needs (_wh_partition_carry) move to '
  'the partition for p_now, reported as carried.';
