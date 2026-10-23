-- Migration: 231_PartitionSignalsAndLifecycleCompletions.sql
-- Date: 2026-10-22
-- Description: The partition manager (226) keeps a table partitioned by a time-ordered uuid as well as one partitioned
--              by a timestamp, so wh_lifecycle_completions can be partitioned daily by event_id and wh_signals daily
--              by created_at (#1646, WS-5 slice S4b).
--
--              A version-7 uuid begins with its creation time in Unix milliseconds (48 bits). The first id of a moment
--              is that moment's milliseconds in the first twelve hex digits and zeros after, and uuids compare byte
--              by byte, so the ids made in one day are exactly the range from that day's first id to the next day's.
--              The manager takes the key's type from the catalog: a timestamptz key is bounded by the moments
--              themselves, a uuid key by their first ids. Two helpers do the translation both ways:
--
--                _wh_partition_bound     the bound literal for a moment, as the key's type writes it
--                _wh_partition_bound_at  the moment a bound stands for, read back from a partition's bound text
--
--              An id that is not time-ordered (a version-4 id, whose first bits are random) is accepted and lands in
--              the default partition, which nothing drops; the row sweep keeps clearing that partition by its own
--              column (wh_partition_sweep_relation, below).
--
--              wh_partition_convert also stops failing on a row whose key is past the bound of the oldest partition:
--              before attaching the legacy table it moves such rows (a random uuid, or a timestamp from a clock
--              running ahead) into the default partition, which would otherwise make the attach, and so every
--              cycle's conversion, fail.
--
--                wh_partition_sweep_relation  the relation a table's row sweep deletes from: the table itself while it
--                                             is plain or partition maintenance is off, its default partition once it
--                                             is partitioned and maintained (expired partitions are then dropped
--                                             whole), and NULL when there is no default partition to sweep
--
--              The key changes: wh_signals' primary key becomes (id, created_at), as PostgreSQL requires of every
--              unique key on a partitioned table; nothing references wh_signals.id, and the tail's cursor compares ids,
--              which the parent's key index still serves (MAX(id), id > cursor ORDER BY id). wh_lifecycle_completions'
--              key is event_id, the partition column, so it stays as it is and a second completion for one event is
--              still refused.
--
--              Number 231: 226 is the partition manager (#1611); 228 to 230 landed while this was written.
--
-- Dependencies: 226 (_wh_partition_start, wh_partition_maintain, wh_partition_convert), 028 (wh_settings), 035
--               (wh_lifecycle_completions), 065 (wh_signals)
-- Objects: _wh_partition_bound, _wh_partition_bound_at, wh_partition_maintain, wh_partition_convert, wh_partition_sweep_relation
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_partition_bound');
SELECT __SCHEMA__.drop_all_overloads('_wh_partition_bound_at');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_maintain');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_convert');
SELECT __SCHEMA__.drop_all_overloads('wh_partition_sweep_relation');

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 1: bounds. A moment before the Unix epoch has no version-7 id, so it bounds at the smallest uuid.
-- <docs>operations/infrastructure/partitioned-ledgers#time-ordered-ids</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:AUuidBound_IsTheFirstIdOfItsMoment_AndReadsBackAsThatMomentAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_partition_bound(p_at TIMESTAMPTZ, p_type REGTYPE)
RETURNS TEXT AS $$
DECLARE
  v_ms BIGINT;
  v_hex TEXT;
BEGIN
  IF p_type = 'uuid'::REGTYPE THEN
    v_ms := floor(EXTRACT(EPOCH FROM p_at) * 1000)::BIGINT;
    IF v_ms < 0 THEN
      RETURN __EMPTY_UUID__;
    END IF;
    v_hex := lpad(to_hex(v_ms), 12, '0');
    RETURN substr(v_hex, 1, 8) || '-' || substr(v_hex, 9, 4) || '-0000-0000-000000000000';
  END IF;
  RETURN p_at::TEXT;
END;
$$ LANGUAGE plpgsql STABLE SET timezone = 'UTC';

COMMENT ON FUNCTION __SCHEMA__._wh_partition_bound(TIMESTAMPTZ, REGTYPE) IS
  'Issue #1646: the partition bound for p_at as a key of type p_type writes it: the moment itself for timestamptz, '
  'the first version-7 id of that millisecond for uuid.';

-- <docs>operations/infrastructure/partitioned-ledgers#time-ordered-ids</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:AUuidBound_IsTheFirstIdOfItsMoment_AndReadsBackAsThatMomentAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_partition_bound_at(p_bound TEXT, p_type REGTYPE)
RETURNS TIMESTAMPTZ AS $$
BEGIN
  IF p_bound = 'MINVALUE' THEN
    RETURN '-infinity'::TIMESTAMPTZ;
  ELSIF p_bound = 'MAXVALUE' THEN
    RETURN 'infinity'::TIMESTAMPTZ;
  ELSIF p_type = 'uuid'::REGTYPE THEN
    RETURN to_timestamp(('x' || substr(replace(btrim(p_bound, ''''), '-', ''), 1, 12))::BIT(48)::BIGINT / 1000.0);
  END IF;
  RETURN btrim(p_bound, '''')::TIMESTAMPTZ;
END;
$$ LANGUAGE plpgsql STABLE SET timezone = 'UTC';

COMMENT ON FUNCTION __SCHEMA__._wh_partition_bound_at(TEXT, REGTYPE) IS
  'Issue #1646: the moment a partition bound (as pg_get_expr writes it) stands for, for a key of type p_type: '
  'MINVALUE and MAXVALUE are -infinity and infinity, a uuid bound is the time in its first 48 bits.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 2: the manager, last word 226_PartitionManager.sql, copied whole. The changes: the key may be a uuid as well
-- as a timestamptz, and every bound is written and read through the two helpers above.
-- <docs>operations/infrastructure/partitioned-ledgers#what-a-cycle-does</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:ACycle_CreatesTheCurrentAndAheadPartitions_AndASecondCreatesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:APartitionWhollyPastTheWindow_IsDropped_OneStraddlingItIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:AnExpiredPartition_IsEmptiedWithoutBlockingAnInsertInFlightAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintainSqlTests.cs:RowsInTheDefaultPartition_MoveIntoThePartitionCreatedForThemAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:ACycle_CreatesDailyIdPartitions_AndAVersion7IdLandsInItsDayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:APartitionWhollyPastTheWindow_IsDropped_OneStraddlingItIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:IdsInTheDefaultPartition_MoveIntoThePartitionCreatedForThemAsync</tests>
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
  v_type REGTYPE;
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
        AND __SCHEMA__._wh_partition_bound_at(m[1], v_type) < v_hi
        AND __SCHEMA__._wh_partition_bound_at(m[2], v_type) > v_lo);
    v_name := p_table || '_p' || to_char(v_lo, CASE WHEN v_step::BIGINT % 86400 = 0 THEN 'YYYYMMDD' ELSE 'YYYYMMDD"_"HH24MI' END);
    BEGIN
      IF v_default IS NOT NULL THEN
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE NOWAIT', v_default::regclass);
      END IF;
      EXECUTE format('CREATE TABLE %I.%I (LIKE %I.%I INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE)',
        v_schema, v_name, v_schema, p_table);
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
  'Issue #1646: the key is a timestamptz or a time-ordered (version-7) uuid, bounded by the first id of each moment.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 3: the one-time conversion, last word 226_PartitionManager.sql, copied whole. The changes: the column may be a
-- uuid as well as a timestamptz; the default partition is created before the legacy table is attached, and rows whose
-- key is at or past the legacy partition's bound move into it first, so one such row no longer fails the attach; the
-- legacy table's key is dropped only after that move, so the move reads it through the key's index.
-- <docs>operations/infrastructure/partitioned-ledgers#converting-an-existing-table</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:Conversion_KeepsEveryRow_InTheLegacyPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:Conversion_InterruptedPartway_LeavesTheTableAsItWas_AndTheNextCycleCompletesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:TheMarker_StopsTheConversionRunningAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionConversionTests.cs:Conversion_KeepsEverySignal_AndTheKeyGainsCreatedAtAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LifecycleCompletionPartitionConversionTests.cs:Conversion_KeepsEveryCompletion_AndAVersion4IdMovesToTheDefaultPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:AConversion_ByUuid_MovesIdsPastItsBoundToTheDefault_InsteadOfFailingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/UuidPartitionSqlTests.cs:ATimestampConversion_MovesRowsPastItsBoundToTheDefaultAsync</tests>
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
  v_type REGTYPE;
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
    SELECT a.atttypid::REGTYPE INTO v_type
    FROM pg_attribute a
    WHERE a.attrelid = v_table AND a.attname = p_column AND a.atttypid IN ('timestamptz'::regtype, 'uuid'::regtype)
      AND a.attnotnull AND NOT a.attisdropped;
    IF v_type IS NULL THEN
      RAISE EXCEPTION '%.%.% must be a NOT NULL timestamptz or uuid column to partition by', v_schema, p_table, p_column
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
    FOR i IN 1..COALESCE(array_length(v_index_names, 1), 0) LOOP
      EXECUTE format('ALTER INDEX %I.%I RENAME TO %I', v_schema, v_index_names[i], left(v_index_names[i], 56) || '_legacy');
    END LOOP;

    EXECUTE format(
      'CREATE TABLE %I.%I (LIKE %I.%I INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE INCLUDING COMMENTS) '
      'PARTITION BY RANGE (%I)', v_schema, p_table, v_schema, v_legacy, p_column);
    EXECUTE format('ALTER TABLE %I.%I OWNER TO %I', v_schema, p_table, pg_get_userbyid(v_owner));

    -- The default partition comes first, so rows the legacy partition's range cannot hold (a key at or past its
    -- bound: an id that is not time-ordered, or a time from a clock running ahead) have somewhere to go before the
    -- attach checks every row against that range.
    EXECUTE format('CREATE TABLE %I.%I PARTITION OF %I.%I DEFAULT', v_schema, p_table || '_default', v_schema, p_table);
    EXECUTE format('WITH moved AS (DELETE FROM %I.%I WHERE %I >= %L::%s RETURNING *) INSERT INTO %I.%I SELECT * FROM moved',
      v_schema, v_legacy, p_column, __SCHEMA__._wh_partition_bound(v_bound, v_type), v_type, v_schema, p_table);

    IF v_pk_name IS NOT NULL THEN
      EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I', v_schema, v_legacy, v_pk_name);
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
      v_schema, p_table, v_schema, v_legacy, __SCHEMA__._wh_partition_bound(v_bound, v_type));
    FOR i IN 1..COALESCE(array_length(v_index_names, 1), 0) LOOP
      EXECUTE 'CREATE ' || v_index_defs[i];
    END LOOP;
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
  'wh_settings. Returns converted, already-converted, missing, busy or deferred. '
  'Issue #1646: p_column may be a time-ordered uuid; rows past the oldest partition''s bound move to the default partition.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 4: where a row sweep deletes. Partition maintenance is the retention of a partitioned table only while it is
-- enabled (wh_settings partition_maintenance_enabled, published every cycle from the option); otherwise the sweep
-- keeps the whole table, so a converted table whose maintenance was switched off is still bounded. Once maintained,
-- only the default partition is the sweep's: it holds rows no partition covers, and no drop ever reaches it.
-- <docs>operations/infrastructure/partitioned-ledgers#row-sweeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LifecycleCompletionPartitionConversionTests.cs:TheSweepRelation_IsTheTable_UntilItIsPartitionedAndManaged_ThenItsDefaultPartitionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/SignalPartitionConversionTests.cs:OnceManaged_TheSweepLeavesPartitionsToTheDrop_AndClearsOnlyTheDefaultAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorLifecycleAndJanitorTests.cs:CleanupLifecycleCompletionsAsync_OncePartitionedAndManaged_SweepsOnlyTheDefaultPartitionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_partition_sweep_relation(p_table TEXT)
RETURNS TEXT AS $$
DECLARE
  v_nsp OID;
  v_schema TEXT;
  v_table OID;
  v_kind "char";
  v_default TEXT;
BEGIN
  SELECT c.relnamespace INTO v_nsp FROM pg_class c WHERE c.oid = '__SCHEMA__.wh_settings'::regclass;
  SELECT n.nspname INTO v_schema FROM pg_namespace n WHERE n.oid = v_nsp;
  SELECT c.oid, c.relkind INTO v_table, v_kind FROM pg_class c WHERE c.relnamespace = v_nsp AND c.relname = p_table;
  IF v_table IS NULL THEN
    RETURN NULL;
  END IF;
  IF v_kind <> 'p' OR NOT COALESCE(
      (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'partition_maintenance_enabled'), FALSE) THEN
    RETURN format('%I.%I', v_schema, p_table);
  END IF;
  SELECT c.relname INTO v_default
  FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
  WHERE i.inhparent = v_table AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT';
  RETURN CASE WHEN v_default IS NULL THEN NULL ELSE format('%I.%I', v_schema, v_default) END;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_partition_sweep_relation(TEXT) IS
  'Issue #1646: the relation a row sweep of p_table deletes from: the table while it is plain or partition maintenance '
  'is off, its default partition once it is partitioned and maintained, NULL when there is none.';
