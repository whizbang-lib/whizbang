-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 226_PartitionManager.sql
-- Date: 2026-10-21
-- Description: A built-in partition manager for time-ordered framework tables, and the one-time conversion that
--              partitions wh_log by week (#1611, WS-5 slice S4a).
--
--              Retention on an append-only ledger was a batched DELETE, which leaves dead tuples, index bloat and WAL
--              for vacuum to clean up. A table range-partitioned by time drops a whole partition instead. Three
--              functions, called by the partition maintenance step of each driver while
--              Whizbang:Workers:Maintenance:Partitioning:Enabled is true, and only on the instance that holds the
--              maintainer duty:
--
--                _wh_partition_start    the start of the interval holding a moment: intervals are whole hours,
--                                       counted from Monday 2000-01-03 00:00 UTC, so a weekly partition starts on
--                                       a Monday and a daily one at midnight UTC
--                wh_partition_convert   converts a plain table once: the table becomes the oldest partition of a
--                                       new parent of the same name, covering everything before the next interval,
--                                       and a default partition is added. One transaction, so an interruption
--                                       leaves the table as it was and the next cycle starts again; recorded in
--                                       wh_settings as <table>_partition_version = 1, which stops it running again
--                wh_partition_maintain  creates the partitions for the current interval and p_ahead intervals
--                                       forward, ensures the default partition, and removes partitions that ended
--                                       at or before p_now - p_keep (a partition straddling that moment is kept)
--
--              Locks. Inserts route through the parent, holding ROW EXCLUSIVE on it and on the one partition they
--              land in. Creating a partition attaches it (SHARE UPDATE EXCLUSIVE on the parent, which inserts do not
--              conflict with). Removing one is two steps: when the parent, the default partition and the expired
--              partition can all be locked at once (NOWAIT), the partition is detached and dropped, a catalog change
--              of milliseconds; when any of them is in use, the expired partition is only truncated, which locks
--              that partition alone, and it is detached on a later cycle. DETACH ... CONCURRENTLY is not used: it
--              cannot run inside a function's transaction, and PostgreSQL refuses it while a default partition
--              exists. No lock here is ever waited for, so nothing queues writers behind the manager.
--
--              reap_unbounded_ledgers (221) stops deleting wh_log rows once wh_log is partitioned and partition
--              maintenance is enabled (wh_settings partition_maintenance_enabled, published every cycle from the
--              option), because dropping partitions is then the retention. While the table is unconverted, or the
--              option is off, the row delete stays, so a converted table whose maintenance was switched off is still
--              bounded.
--
--              Number 226: 224 is the direct append under the stream lock (#1555); 225 is the direct append's digest fold (#1600).
--
-- Dependencies: 028 (wh_log, wh_settings, log_event), 221 (reap_unbounded_ledgers, last word)
-- Objects: _wh_partition_start, wh_partition_maintain, wh_partition_convert, reap_unbounded_ledgers
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_partition_start');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_maintain');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_convert');

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 1: interval arithmetic. Done in epoch seconds rather than timestamptz + interval, which adds days in the
-- session's time zone and would move a boundary across a daylight-saving change.
-- <docs>operations/infrastructure/partitioned-ledgers#intervals-and-names</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:WeeklyPartitions_StartOnMondayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:AnIntervalThatIsNotWholeHours_IsRejectedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_partition_start(p_at TIMESTAMPTZ, p_interval INTERVAL)
RETURNS TIMESTAMPTZ AS $$
DECLARE
  -- Monday 2000-01-03 00:00:00 UTC, in epoch seconds.
  v_anchor CONSTANT DOUBLE PRECISION := 946857600;
  v_step DOUBLE PRECISION := EXTRACT(EPOCH FROM p_interval);
BEGIN
  IF p_interval IS NULL OR EXTRACT(YEAR FROM p_interval) <> 0 OR EXTRACT(MONTH FROM p_interval) <> 0
     OR v_step < 3600 OR v_step::BIGINT % 3600 <> 0 OR v_step <> v_step::BIGINT THEN
    RAISE EXCEPTION 'A partition interval must be a whole number of hours with no month part (was %)', p_interval
      USING ERRCODE = 'invalid_parameter_value';
  END IF;
  RETURN to_timestamp(floor((EXTRACT(EPOCH FROM p_at) - v_anchor) / v_step) * v_step + v_anchor);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_partition_start(TIMESTAMPTZ, INTERVAL) IS
  'Issue #1611: the start of the partition interval holding p_at, counted in whole hours from Monday 2000-01-03 UTC.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 2: the manager. One call per registered table per maintenance cycle.
-- <docs>operations/infrastructure/partitioned-ledgers#what-a-cycle-does</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:ACycle_CreatesTheCurrentAndAheadPartitions_AndASecondCreatesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:APartitionWhollyPastTheWindow_IsDropped_OneStraddlingItIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:AnExpiredPartition_IsEmptiedWithoutBlockingAnInsertInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:RowsInTheDefaultPartition_MoveIntoThePartitionCreatedForThemAsync</tests>
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
  v_kind "char";
  v_key TEXT;
  v_default OID;
  v_step DOUBLE PRECISION;
  v_start TIMESTAMPTZ;
  v_lo TIMESTAMPTZ;
  v_hi TIMESTAMPTZ;
  v_name TEXT;
  v_debug BOOLEAN;
  v_part RECORD;
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

  SELECT a.attname INTO v_key
  FROM pg_partitioned_table pt
  JOIN pg_attribute a ON a.attrelid = pt.partrelid AND a.attnum = pt.partattrs[0]
  WHERE pt.partrelid = v_parent AND pt.partstrat = 'r' AND pt.partnatts = 1
    AND a.atttypid = 'timestamptz'::regtype;
  IF v_key IS NULL THEN
    RAISE EXCEPTION '%.% is not range-partitioned on one timestamptz column', v_schema, p_table
      USING ERRCODE = 'invalid_parameter_value';
  END IF;

  -- Two instances never change one table's partitions at once: the loser of this key skips the cycle.
  IF NOT pg_try_advisory_xact_lock(hashtextextended('wh_partition:' || v_schema || '.' || p_table, 0)) THEN
    RETURN QUERY SELECT 'skipped'::TEXT, p_table, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ;
    RETURN;
  END IF;

  -- The default partition catches a row no partition covers, so a writer never fails for want of one.
  SELECT i.inhrelid INTO v_default
  FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
  WHERE i.inhparent = v_parent AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT';
  IF v_default IS NULL THEN
    BEGIN
      EXECUTE format('LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE NOWAIT', v_schema, p_table);
      EXECUTE format('CREATE TABLE %I.%I PARTITION OF %I.%I DEFAULT', v_schema, p_table || '_default', v_schema, p_table);
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
        AND (CASE WHEN m[1] = 'MINVALUE' THEN '-infinity'::TIMESTAMPTZ ELSE btrim(m[1], '''')::TIMESTAMPTZ END) < v_hi
        AND (CASE WHEN m[2] = 'MAXVALUE' THEN 'infinity'::TIMESTAMPTZ ELSE btrim(m[2], '''')::TIMESTAMPTZ END) > v_lo);
    v_name := p_table || '_p' || to_char(v_lo, CASE WHEN v_step::BIGINT % 86400 = 0 THEN 'YYYYMMDD' ELSE 'YYYYMMDD"_"HH24MI' END);
    BEGIN
      IF v_default IS NOT NULL THEN
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE NOWAIT', v_default::regclass);
      END IF;
      EXECUTE format('CREATE TABLE %I.%I (LIKE %I.%I INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE)',
        v_schema, v_name, v_schema, p_table);
      IF v_default IS NOT NULL THEN
        -- Rows the default partition caught for this range move with it; attaching otherwise fails.
        EXECUTE format('WITH moved AS (DELETE FROM %s WHERE %I >= $1 AND %I < $2 RETURNING *) INSERT INTO %I.%I SELECT * FROM moved',
          v_default::regclass, v_key, v_key, v_schema, v_name) USING v_lo, v_hi;
      END IF;
      EXECUTE format('ALTER TABLE %I.%I ATTACH PARTITION %I.%I FOR VALUES FROM (%L) TO (%L)',
        v_schema, p_table, v_schema, v_name, v_lo, v_hi);
      RETURN QUERY SELECT 'created'::TEXT, v_name, v_lo, v_hi;
    EXCEPTION WHEN lock_not_available THEN
      RETURN QUERY SELECT 'deferred'::TEXT, v_name, v_lo, v_hi;
    END;
  END LOOP;

  IF p_keep IS NULL THEN
    RETURN;
  END IF;
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'), FALSE)
    INTO v_debug;

  FOR v_part IN
    SELECT c.oid AS relid, c.relname,
      CASE WHEN m[1] = 'MINVALUE' THEN '-infinity'::TIMESTAMPTZ ELSE btrim(m[1], '''')::TIMESTAMPTZ END AS lo,
      btrim(m[2], '''')::TIMESTAMPTZ AS hi
    FROM pg_inherits i2
    JOIN pg_class c ON c.oid = i2.inhrelid
    CROSS JOIN LATERAL regexp_match(pg_get_expr(c.relpartbound, c.oid), '^FOR VALUES FROM \((.+)\) TO \((.+)\)$') m
    WHERE i2.inhparent = v_parent AND m[2] <> 'MAXVALUE'
      AND btrim(m[2], '''')::TIMESTAMPTZ <= p_now - p_keep
    ORDER BY 4
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
  'or before p_now - p_keep. Never waits for a lock. Called by the partition maintenance step.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 3: the one-time conversion of a plain table. The table is renamed <table>_legacy and attached, whole, as the
-- oldest partition (MINVALUE up to the start of the next interval) of a new parent with its name, its columns,
-- defaults, sequences, comments, grants and indexes; its primary key gains the partition column, as PostgreSQL
-- requires of every unique key on a partitioned table. Attaching checks every row against the range, under the
-- table's exclusive lock, once.
-- <docs>operations/infrastructure/partitioned-ledgers#converting-an-existing-table</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:Conversion_KeepsEveryRow_InTheLegacyPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:Conversion_InterruptedPartway_LeavesTheTableAsItWas_AndTheNextCycleCompletesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:TheMarker_StopsTheConversionRunningAgainAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_convert(
  p_table TEXT,
  p_column TEXT,
  p_interval INTERVAL,
  p_now TIMESTAMPTZ DEFAULT NOW()
) RETURNS TEXT AS $$
DECLARE
  v_marker TEXT := p_table || '_partition_version';
  v_legacy TEXT := p_table || '_legacy';
  v_nsp OID;
  v_schema TEXT;
  v_table OID;
  v_kind "char";
  v_owner OID;
  v_bound TIMESTAMPTZ;
  v_pk_name TEXT;
  v_pk_cols TEXT;
  v_pk_has_key BOOLEAN;
  v_index_names TEXT[];
  v_index_defs TEXT[];
  v_comment TEXT;
  v_row RECORD;
BEGIN
  v_bound := to_timestamp(EXTRACT(EPOCH FROM __SCHEMA__._wh_partition_start(p_now, p_interval)) + EXTRACT(EPOCH FROM p_interval));
  IF COALESCE((SELECT s.setting_value FROM __SCHEMA__.wh_settings s WHERE s.setting_key = v_marker), '0') = '1' THEN
    RETURN 'already-converted';
  END IF;

  SELECT c.relnamespace INTO v_nsp FROM pg_class c WHERE c.oid = '__SCHEMA__.wh_settings'::regclass;
  SELECT n.nspname INTO v_schema FROM pg_namespace n WHERE n.oid = v_nsp;
  SELECT c.oid, c.relkind, c.relowner INTO v_table, v_kind, v_owner
  FROM pg_class c WHERE c.relnamespace = v_nsp AND c.relname = p_table;
  IF v_table IS NULL THEN
    RETURN 'missing';
  END IF;

  IF v_kind = 'r' THEN
    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute a
        WHERE a.attrelid = v_table AND a.attname = p_column AND a.atttypid = 'timestamptz'::regtype
          AND a.attnotnull AND NOT a.attisdropped) THEN
      RAISE EXCEPTION '%.%.% must be a NOT NULL timestamptz column to partition by', v_schema, p_table, p_column
        USING ERRCODE = 'invalid_parameter_value';
    END IF;
    IF NOT pg_try_advisory_xact_lock(hashtextextended('wh_partition:' || v_schema || '.' || p_table, 0)) THEN
      RETURN 'busy';
    END IF;
    BEGIN
      EXECUTE format('LOCK TABLE %I.%I IN ACCESS EXCLUSIVE MODE NOWAIT', v_schema, p_table);
    EXCEPTION WHEN lock_not_available THEN
      RETURN 'deferred';
    END;

    SELECT con.conname,
           string_agg(quote_ident(a.attname), ', ' ORDER BY k.ord),
           bool_or(a.attname = p_column)
      INTO v_pk_name, v_pk_cols, v_pk_has_key
    FROM pg_constraint con
    CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
    JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
    WHERE con.conrelid = v_table AND con.contype = 'p'
    GROUP BY con.conname;

    IF EXISTS (
        SELECT 1 FROM pg_index ix
        WHERE ix.indrelid = v_table AND ix.indisunique AND NOT ix.indisprimary
          AND NOT (SELECT a.attnum FROM pg_attribute a WHERE a.attrelid = v_table AND a.attname = p_column) = ANY (ix.indkey)) THEN
      RAISE EXCEPTION '%.% has a unique index without %, which a partitioned table cannot keep', v_schema, p_table, p_column
        USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Every other index is recreated on the parent under its own name; the legacy table's copy is renamed out of
    -- the way first, and the parent's index then adopts it rather than building another.
    SELECT array_agg(ic.relname::TEXT ORDER BY ic.relname),
           array_agg(CASE WHEN ix.indisunique THEN 'UNIQUE ' ELSE '' END || 'INDEX ' || quote_ident(ic.relname) || ' ON '
                     || quote_ident(v_schema) || '.' || quote_ident(p_table) || substring(pg_get_indexdef(ix.indexrelid) FROM ' USING .*$')
                     ORDER BY ic.relname)
      INTO v_index_names, v_index_defs
    FROM pg_index ix JOIN pg_class ic ON ic.oid = ix.indexrelid
    WHERE ix.indrelid = v_table AND NOT ix.indisprimary;

    EXECUTE format('ALTER TABLE %I.%I RENAME TO %I', v_schema, p_table, v_legacy);
    IF v_pk_name IS NOT NULL THEN
      EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I', v_schema, v_legacy, v_pk_name);
    END IF;
    FOR i IN 1..COALESCE(array_length(v_index_names, 1), 0) LOOP
      EXECUTE format('ALTER INDEX %I.%I RENAME TO %I', v_schema, v_index_names[i], left(v_index_names[i], 56) || '_legacy');
    END LOOP;

    EXECUTE format(
      'CREATE TABLE %I.%I (LIKE %I.%I INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE INCLUDING COMMENTS) '
      'PARTITION BY RANGE (%I)', v_schema, p_table, v_schema, v_legacy, p_column);
    EXECUTE format('ALTER TABLE %I.%I OWNER TO %I', v_schema, p_table, pg_get_userbyid(v_owner));
    IF v_pk_name IS NOT NULL THEN
      EXECUTE format('ALTER TABLE %I.%I ADD CONSTRAINT %I PRIMARY KEY (%s)', v_schema, p_table, v_pk_name,
        CASE WHEN v_pk_has_key THEN v_pk_cols ELSE v_pk_cols || ', ' || quote_ident(p_column) END);
    END IF;

    -- A serial column's sequence belongs to the parent now, so dropping the legacy partition keeps it.
    FOR v_row IN
      SELECT a.attname, pg_get_serial_sequence(format('%I.%I', v_schema, v_legacy), a.attname) AS seq
      FROM pg_attribute a WHERE a.attrelid = v_table AND a.attnum > 0 AND NOT a.attisdropped
    LOOP
      IF v_row.seq IS NOT NULL THEN
        EXECUTE format('ALTER SEQUENCE %s OWNED BY %I.%I.%I', v_row.seq, v_schema, p_table, v_row.attname);
      END IF;
    END LOOP;

    v_comment := obj_description(v_table, 'pg_class');
    IF v_comment IS NOT NULL THEN
      EXECUTE format('COMMENT ON TABLE %I.%I IS %L', v_schema, p_table, v_comment);
    END IF;
    -- Writers reach the table through the parent, so the parent carries the legacy table's grants.
    FOR v_row IN
      SELECT a.privilege_type, a.grantee, a.is_grantable
      FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
      WHERE c.oid = v_table AND a.grantee <> c.relowner
    LOOP
      EXECUTE format('GRANT %s ON %I.%I TO %s%s', v_row.privilege_type, v_schema, p_table,
        CASE WHEN v_row.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(v_row.grantee)) END,
        CASE WHEN v_row.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END);
    END LOOP;

    EXECUTE format('ALTER TABLE %I.%I ATTACH PARTITION %I.%I FOR VALUES FROM (MINVALUE) TO (%L)',
      v_schema, p_table, v_schema, v_legacy, v_bound);
    FOR i IN 1..COALESCE(array_length(v_index_names, 1), 0) LOOP
      EXECUTE 'CREATE ' || v_index_defs[i];
    END LOOP;
    EXECUTE format('CREATE TABLE %I.%I PARTITION OF %I.%I DEFAULT', v_schema, p_table || '_default', v_schema, p_table);
  END IF;

  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
  VALUES (v_marker, '1', 'integer',
          'Issue #1611: 1 once ' || p_table || ' is range-partitioned by ' || p_column || ' (wh_partition_convert)')
  ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value, updated_at = NOW();
  RETURN CASE WHEN v_kind = 'r' THEN 'converted' ELSE 'already-converted' END;
END;
$$ LANGUAGE plpgsql SET timezone = 'UTC';

COMMENT ON FUNCTION __SCHEMA__.wh_partition_convert(TEXT, TEXT, INTERVAL, TIMESTAMPTZ) IS
  'Issue #1611: converts a plain framework table to one range-partitioned by p_column, once, in one transaction: the '
  'table becomes the oldest partition and a default partition is added. Recorded as <table>_partition_version = 1 in '
  'wh_settings. Returns converted, already-converted, missing, busy or deferred.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 4: reap_unbounded_ledgers, last word 221_LedgerRetention.sql, copied whole. The only change: the wh_log row
-- delete is skipped while wh_log is partitioned and partition maintenance is enabled.
-- <docs>fundamentals/workers/maintenance-steps#ledger-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_OlderThanTheWindow_IsRemoved_YoungerIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_ConfiguredWindow_IsHonoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:ScheduleRuns_OlderThanTheWindow_AreRemoved_ButTheNewestRunOfEachScheduleIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:StreamPurgeAudit_IsKeptForeverByDefault_AndBoundedWhenConfiguredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:IntegrityLedger_IsTrimmedToTheConfiguredCount_NewestKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:DebugMode_KeepsEveryLedgerRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceConverted_RetentionDropsPartitions_InsteadOfDeletingRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:AConvertedLog_WithPartitioningSwitchedOff_IsStillBoundedByTheRowDeleteAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_unbounded_ledgers(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(
  task_name TEXT,
  rows_affected BIGINT,
  duration_ms DOUBLE PRECISION,
  status TEXT
) AS $$
DECLARE
  v_debug_mode BOOLEAN;
  v_start TIMESTAMPTZ;
  v_rows BIGINT;
  v_batch BIGINT;
  v_log_days INTEGER;
  v_run_days INTEGER;
  v_audit_days INTEGER;
  v_ledger_max INTEGER;
  v_log_partitioned BOOLEAN;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'log_retention_days'), 14)
    INTO v_log_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'schedule_run_retention_days'), 30)
    INTO v_run_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stream_purge_audit_retention_days'), 0)
    INTO v_audit_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_ledger_max_entries'), 100000)
    INTO v_ledger_max;
  -- 226: a partitioned wh_log whose partitions the manager maintains is bounded by dropping partitions, so the row
  -- delete would only leave dead tuples behind in the partition that straddles the window.
  SELECT COALESCE(
      (SELECT c.relkind = 'p' FROM pg_class c WHERE c.oid = to_regclass('__SCHEMA__.wh_log')), FALSE)
    AND COALESCE(
      (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'partition_maintenance_enabled'), FALSE)
    INTO v_log_partitioned;

  -- wh_log, by logged_at (indexed, 028).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND NOT v_log_partitioned THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_log
      WHERE log_id IN (
        SELECT l.log_id FROM __SCHEMA__.wh_log l
        WHERE l.logged_at < NOW() - (v_log_days * INTERVAL '1 day')
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_log_entries'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_log_partitioned THEN 'skipped (partitioned: expired partitions are dropped)'
      ELSE 'ok'
    END::TEXT;

  -- wh_schedule_runs, by fired_at, never a schedule's newest run (the (schedule_id, fired_at DESC) index, 066,
  -- answers "is there a newer run" for each candidate).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_schedule_runs
      WHERE run_id IN (
        SELECT r.run_id FROM __SCHEMA__.wh_schedule_runs r
        WHERE r.fired_at < NOW() - (v_run_days * INTERVAL '1 day')
          AND EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_schedule_runs newer
            WHERE newer.schedule_id = r.schedule_id AND newer.fired_at > r.fired_at)
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_schedule_runs'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- wh_stream_purge_audit, by purged_at, only when an operator set a window.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND v_audit_days > 0 THEN
    DELETE FROM __SCHEMA__.wh_stream_purge_audit a
    WHERE a.purged_at < NOW() - (v_audit_days * INTERVAL '1 day');
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_old_stream_purge_audit'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_audit_days = 0 THEN 'kept (stream_purge_audit_retention_days=0)'
      ELSE 'ok'
    END::TEXT;

  -- wh_integrity_ledger, to its row cap, by the trim 090 shipped.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    v_rows := __SCHEMA__.wh_integrity_ledger_trim(v_ledger_max);
  END IF;
  RETURN QUERY SELECT
    'trim_integrity_ledger'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_unbounded_ledgers(INTEGER) IS
  'Issue #1572: bounds wh_log, wh_schedule_runs (keeping each schedule''s newest run), wh_stream_purge_audit '
  '(opt-in) and wh_integrity_ledger, in batches, from their wh_settings windows. Called by perform_maintenance.';

COMMENT ON FUNCTION __SCHEMA__.reap_unbounded_ledgers(INTEGER) IS
  'Issue #1572: bounds wh_log, wh_schedule_runs (keeping each schedule''s newest run), wh_stream_purge_audit '
  '(opt-in) and wh_integrity_ledger, in batches, from their wh_settings windows. Called by perform_maintenance. '
  'Issue #1611: skips the wh_log row delete while wh_log is partitioned and partition maintenance is enabled.';
