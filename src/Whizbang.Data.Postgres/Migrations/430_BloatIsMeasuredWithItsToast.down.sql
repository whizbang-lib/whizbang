-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 430_BloatIsMeasuredWithItsToast.sql (#2319): wh_tables_needing_rewrite goes back to its 213 definition,
-- wh_measure_table_bloat_approx to its 254 definition and wh_tables_owed_fillfactor_rewrite to its 410 definition, each
-- measuring the heap alone with the unweighted pg_stats width and no post-rewrite floor; then the functions 430 added
-- are dropped. Nothing is stored by 430, so nothing is lost; the table_rewrite_regrowth_factor setting row, when the host
-- published one, stays and is read by nothing.

-- <docs>operations/infrastructure/migrations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:RewriteCandidatesAreMeasuredInTheContextsSchemaAsync</tests>
-- What the maintenance worker should rewrite RIGHT NOW.
--
-- Every candidate is re-measured here rather than trusted from the request list. That is what
-- makes the mechanism safe to leave switched on:
--   * a migration whose content hash changed replays and re-requests a table that was already
--     rewritten; re-measuring returns nothing, so no pointless multi-minute lock;
--   * a fresh deployment applies the drop against an empty table and never had residue at all;
--   * a table that churns back into bloat is offered again without anyone re-requesting it.
--
-- Combines both sources: explicitly requested (a migration recorded it) and detected (ratio over
-- the configured threshold). Expected width comes from pg_stats.avg_width, which autoanalyze
-- already maintains, so this is a catalog read rather than a table scan.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_tables_needing_rewrite()
RETURNS TABLE (table_name TEXT, bloat_ratio NUMERIC, requested BOOLEAN)
LANGUAGE plpgsql
AS $$
DECLARE
  v_threshold NUMERIC;
  v_min_rows  BIGINT;
  v_requested TEXT[];
BEGIN
  SELECT COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_bloat_threshold'), '3.0')::NUMERIC
    INTO v_threshold;
  SELECT COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_min_rows'), '1000')::BIGINT
    INTO v_min_rows;
  SELECT string_to_array(NULLIF(COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites'), ''), ''), ',')
    INTO v_requested;

  RETURN QUERY
  -- pg_class.reltuples, NOT pg_stat_user_tables.n_live_tup: the stats collector is
  -- asynchronous, so n_live_tup can lag recent churn and both halves of the rewrite
  -- decision (this candidate ratio, and the post-rewrite re-measure that must beat it)
  -- then race the collector. reltuples is written by VACUUM/ANALYZE transactionally in
  -- the catalog, so both measures read the same synchronous source.
  SELECT c.relname::TEXT,
         round((pg_relation_size(c.oid)::NUMERIC / c.reltuples::NUMERIC) / GREATEST(w.expected, 1), 2),
         (v_requested IS NOT NULL AND c.relname = ANY(v_requested))
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN LATERAL (
    SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
    FROM pg_stats s
    WHERE s.schemaname = n.nspname AND s.tablename = c.relname
  ) w ON TRUE
  WHERE n.nspname = replace('__SCHEMA__', '"', '')
    AND c.relkind = 'r'
    AND c.relname LIKE 'wh\_%'
    AND c.reltuples >= v_min_rows
    AND ((pg_relation_size(c.oid)::NUMERIC / c.reltuples::NUMERIC) / GREATEST(w.expected, 1)) >= v_threshold
  ORDER BY 2 DESC;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_tables_needing_rewrite IS
'Tables whose heap is disproportionate to their live rows, re-measured on every call so an already-rewritten or never-bloated table is never offered. Union of migration-requested and threshold-detected.';

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:Approx_OnAGenuinelyBloatedTable_ConfirmsTheEstimateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:Approx_OnAStaleEstimate_MeasuresTheTableLeanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:Approx_WithoutTheExtension_FailsRatherThanAnsweringAsync</tests>
-- A framework table's bloat ratio measured with pgstattuple_approx, on the scale of the catalog estimate.
--
-- The extension's functions are called in whichever schema it was created in, read from pg_extension, so the call
-- does not depend on the caller's search_path. A missing extension raises rather than answering NULL: the caller asks
-- only while the capability reads Ready, so its absence is a fault to report. NULL when the table does not exist in
-- this schema or holds no live rows.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_measure_table_bloat_approx(p_table TEXT)
RETURNS NUMERIC
LANGUAGE plpgsql
AS $$
DECLARE
  v_extension_schema TEXT;
  v_table REGCLASS;
  v_ratio NUMERIC;
BEGIN
  SELECT n.nspname INTO v_extension_schema
  FROM pg_catalog.pg_extension e
  JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
  WHERE e.extname = 'pgstattuple';

  IF v_extension_schema IS NULL THEN
    RAISE EXCEPTION 'pgstattuple is not installed in this database, so % cannot be measured', p_table
      USING ERRCODE = 'undefined_function';
  END IF;

  v_table := to_regclass('__SCHEMA__.' || quote_ident(p_table));
  IF v_table IS NULL THEN
    RETURN NULL;
  END IF;

  EXECUTE format(
    'SELECT round((a.table_len::NUMERIC / NULLIF(a.approx_tuple_count::NUMERIC, 0)) / GREATEST(w.expected, 1), 2)
     FROM %I.pgstattuple_approx($1) a
     CROSS JOIN LATERAL (
       SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
       FROM pg_catalog.pg_stats s
       WHERE s.schemaname = $2 AND s.tablename = $3
     ) w', v_extension_schema)
    INTO v_ratio
    USING v_table, replace('__SCHEMA__', '"', ''), p_table;

  RETURN v_ratio;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_measure_table_bloat_approx(TEXT) IS
'A framework table''s bloat ratio measured with pgstattuple_approx: heap bytes over live rows times the expected row width, the catalog estimate''s scale with the live rows counted rather than estimated. Raises when pgstattuple is not installed.';

-- <docs>operations/infrastructure/storage-maintenance#fillfactor-decrease</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs:AFillFactorDecrease_OnACompactTable_IsRewrittenOnceOnline_AndAnUpdateOfAnOldRowIsHotAsync</tests>
-- The queued tables owed a rewrite for a lowered fillfactor, in this schema, with the catalog estimate of their bloat
-- ratio (wh_tables_needing_rewrite's measure; 0 for a table never analyzed). A row whose table is no longer queued, or
-- no longer exists, is not offered.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_tables_owed_fillfactor_rewrite()
RETURNS TABLE (table_name TEXT, bloat_ratio NUMERIC)
LANGUAGE sql
STABLE
AS $$
  SELECT c.relname::TEXT,
         COALESCE(round((pg_relation_size(c.oid)::NUMERIC / NULLIF(c.reltuples::NUMERIC, 0)) / GREATEST(w.expected, 1), 2), 0)
  FROM __SCHEMA__.wh_fillfactor_rewrite_requests f
  JOIN pg_class c ON c.relname = f.table_name AND c.relkind = 'r'
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = replace('__SCHEMA__', '"', '')
  JOIN LATERAL (
    SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
    FROM pg_stats s
    WHERE s.schemaname = n.nspname AND s.tablename = c.relname
  ) w ON TRUE
  WHERE f.table_name = ANY (COALESCE(string_to_array(NULLIF(
          (SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites'), ''), ','), ARRAY[]::TEXT[]))
  ORDER BY 1;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_tables_owed_fillfactor_rewrite() IS
'Queued tables owed one rewrite because their fillfactor was lowered while they held rows, whatever their bloat, with the catalog estimate of their bloat ratio.';

SELECT __SCHEMA__.drop_all_overloads('wh_table_rewrite_queue_status');
SELECT __SCHEMA__.drop_all_overloads('_wh_table_rewrite_scan');
SELECT __SCHEMA__.drop_all_overloads('wh_estimate_table_bloat');
SELECT __SCHEMA__.drop_all_overloads('wh_toast_bloat_ratio');
SELECT __SCHEMA__.drop_all_overloads('wh_expected_row_bytes');
