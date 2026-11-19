-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 445_RowCapsPartitionByAModelProperty.sql
-- Date: 2026-11-14
-- Description: A row cap can keep the newest N rows per value of a model property (#2330).
--
--              [RowCap] capped a perspective's rows per (tenant, user) scope ('u') or per tenant ('t'): the registry's
--              row_cap_scope_key named a key of the row's scope. A read model that must keep "the newest N rows per job"
--              could not declare it, so such tables grew without bound. [RowCap(PerProperty = nameof(Model.JobId),
--              Max = N)] registers the key 'data:<json name>', and the cap partitions by that property of the stored
--              document:
--
--                row_cap_scope_key   partition
--                NULL                the whole table
--                'u' / 't'           scope ->> 'u' / scope ->> 't' (unchanged)
--                'data:<name>'       data ->> '<name>'
--
--              _wh_row_cap_partition is the one place that maps the key to the expression; the collection of reap
--              targets and the cap sweep both use it (each copied whole from 213; that is the only change). Eviction
--              is unchanged: the oldest rows by updated_at beyond N in each partition go, journaled, with their cursors
--              and all but their latest snapshot.
--
--              Number 445: reserved for this change.
--
-- Reversal: reversible (both functions go back to 213; the helper goes; a 'data:' key is then read as a scope key,
--           which no row carries, so such a cap keeps N rows over the whole table)
-- Dependencies: 213 (collect_perspective_row_reap_targets, reap_perspective_row_caps), 103 (row_cap_scope_key)
-- Objects: _wh_row_cap_partition, collect_perspective_row_reap_targets, reap_perspective_row_caps
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('_wh_row_cap_partition');

-- The PARTITION BY expression of a row cap from its registry key: '1' for none (the whole table), scope ->> key for a
-- scope key, data ->> name for 'data:<name>' (a model property).
-- <docs>fundamentals/perspectives/row-retention#per-property-caps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Partition_MapsEachKeyForm_ToItsExpressionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_row_cap_partition(p_key TEXT)
RETURNS TEXT
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN p_key IS NULL OR p_key = '' THEN '1'
    WHEN p_key LIKE 'data:%' THEN format('data ->> %L', substr(p_key, 6))
    ELSE format('scope ->> %L', p_key)
  END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_row_cap_partition(TEXT) IS
  'Issue #2330: the PARTITION BY expression of a row cap from wh_perspective_registry.row_cap_scope_key: the whole table '
  '(NULL), a scope key (u, t), or a model property (data:<json name>).';

SELECT __SCHEMA__.drop_all_overloads('reap_perspective_row_caps');

-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:ReapTargetsAreCollectedFromTheContextsSchemaAsync</tests>
-- (1b) Collect re-created from 111 with the cap side ack-gated: offer == destroy, always.
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:CapPerProperty_KeepsTheNewestRowsOfEachKeyValue_AndCleansCompanionsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.collect_perspective_row_reap_targets(
  p_clr_type_names TEXT[],
  p_per_table_limit INTEGER DEFAULT 500)
RETURNS TABLE(o_clr_type_name TEXT, o_table_name TEXT, o_row_id UUID, o_scope JSONB, o_data JSONB, o_reason TEXT) AS $$
DECLARE
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN;
  END IF;

  FOR v_reg IN
    SELECT r.clr_type_name, r.table_name, r.row_ttl_seconds, r.row_max_age_seconds,
           r.row_cap_per_scope, r.row_cap_scope_key, r.retention_enforcement_acknowledged
    FROM __SCHEMA__.wh_perspective_registry r
    WHERE r.row_retention_enrolled AND r.clr_type_name = ANY(p_clr_type_names)
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = replace('__SCHEMA__', '"', '') AND table_name = v_reg.table_name);

    -- BOTH sides gate on acknowledgement now (the cap sweep gained the gate in this migration).
    CONTINUE WHEN NOT v_reg.retention_enforcement_acknowledged;

    -- Expiry ladder.
    RETURN QUERY EXECUTE format(
      'SELECT %L::TEXT, %L::TEXT, id, scope, data, ''ttl''::TEXT FROM %I.%I WHERE
            ((expires_at IS NOT NULL AND expires_at < NOW())
         OR (expires_at IS NULL AND $1 IS NOT NULL
             AND updated_at < NOW() - make_interval(secs => $1))
         OR ($2 IS NOT NULL
             AND created_at < NOW() - make_interval(secs => $2)))
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
       LIMIT $3',
      v_reg.clr_type_name, v_reg.table_name, replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name)
    USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds, p_per_table_limit;

    -- Cap overflow.
    IF v_reg.row_cap_per_scope IS NOT NULL THEN
      -- 445 (#2330): the partition through the one helper, so a per-property cap partitions by the model property.
      v_partition := __SCHEMA__._wh_row_cap_partition(v_reg.row_cap_scope_key);

      RETURN QUERY EXECUTE format(
        'SELECT %L::TEXT, %L::TEXT, t.id, t.scope, t.data, ''cap''::TEXT FROM %I.%I t
         WHERE t.id IN (
           SELECT id FROM (
             SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
             FROM %I.%I
           ) ranked WHERE ranked.rn > $1)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = t.id AND h.hold_until > NOW())
         LIMIT $2',
        v_reg.clr_type_name, v_reg.table_name, replace('__SCHEMA__', '"', ''), v_reg.table_name,
        v_partition, replace('__SCHEMA__', '"', ''), v_reg.table_name,
        replace('__SCHEMA__', '"', ''), v_reg.table_name)
      USING v_reg.row_cap_per_scope, p_per_table_limit;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowCapSqlTests.cs:Cap_KeepsNewestPerScope_AndEvictsTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:CapReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:CapPerProperty_KeepsTheNewestRowsOfEachKeyValue_AndCleansCompanionsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_perspective_row_caps(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start      TIMESTAMPTZ := clock_timestamp();
  v_rows       INTEGER := 0;
  v_deleted    INTEGER := 0;
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
  v_pending    INTEGER := 0;
  v_victims    UUID[];
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN QUERY SELECT
      'reap_perspective_row_caps'::TEXT, 0,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
      'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  FOR v_reg IN
    SELECT table_name, row_cap_per_scope, row_cap_scope_key
    FROM __SCHEMA__.wh_perspective_registry
    WHERE row_retention_enrolled AND retention_enforcement_acknowledged AND row_cap_per_scope IS NOT NULL
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = replace('__SCHEMA__', '"', '') AND table_name = v_reg.table_name);

    -- 445 (#2330): the partition through the one helper, so a per-property cap partitions by the model property.
    v_partition := __SCHEMA__._wh_row_cap_partition(v_reg.row_cap_scope_key);

    EXECUTE format(
      'WITH del AS (
         DELETE FROM %I.%I WHERE id IN (
           SELECT id FROM (
             SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
             FROM %I.%I
           ) ranked WHERE ranked.rn > $1
           LIMIT $2)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
         RETURNING id),
       journaled AS (
         INSERT INTO %I.wh_row_eviction_journal (table_name, row_id)
         SELECT %L, id FROM del
         ON CONFLICT (table_name, row_id) DO NOTHING)
       SELECT ARRAY(SELECT id FROM del)',
      replace('__SCHEMA__', '"', ''), v_reg.table_name, v_partition, replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name)
    INTO v_victims
    USING v_reg.row_cap_per_scope, p_batch_size;

    -- 1483: counted from the rows deleted, and their cursors and all but their latest
    -- snapshots go with them.
    v_deleted := cardinality(v_victims);
    v_rows := v_rows + v_deleted;
    PERFORM __SCHEMA__._wh_evict_perspective_row_companions(v_reg.table_name, v_victims);
    IF v_deleted >= p_batch_size THEN
      v_pending := v_pending + 1;
    END IF;
  END LOOP;

  RETURN QUERY SELECT
    'reap_perspective_row_caps'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_pending > 0
      THEN format('ok (draining: %s perspective(s) hit the batch bound)', v_pending)
      ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_perspective_row_caps(INTEGER) IS
  'Evicts rows ranked beyond a perspective''s per-scope cap, ordered by business time. Gated on '
  'retention_enforcement_acknowledged (a declared cap REPORTS before it removes) and batched so a '
  'first sweep over a large backlog drains across cycles. Holds honored; victims journaled; their '
  'cursors and all but their latest snapshots removed with them. '
  'Issue #2330: partitions through _wh_row_cap_partition, so a cap can be per model property.';
