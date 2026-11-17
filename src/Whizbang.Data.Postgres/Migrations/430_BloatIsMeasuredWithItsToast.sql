-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 430_BloatIsMeasuredWithItsToast.sql
-- Date: 2026-11-14
-- Description: The bloat estimate measures a table's TOAST relation as well as its heap, weighs each column's width by
--              how often it is present, and holds back a table its last rewrite already left as lean as the estimate
--              can read it (#2319).
--
--              The estimate was the heap's bytes over the planner's row count times the summed pg_stats width, read
--              from the main fork only. On a long-running database that missed most of the reclaimable space:
--                * Large values are stored in the table's TOAST relation, and the chunks an update or delete leaves
--                  dead counted for nothing, so a table with a lean heap and a mostly dead TOAST relation was never
--                  offered.
--                * pg_stats.avg_width averages a column over the rows where it is not NULL, so a wide column present
--                  on few rows counted its full width against every row and made a bloated heap read dense.
--              One estimate, wh_estimate_table_bloat, now answers for every reader: the worse of two ratios.
--                * The heap: its bytes over its rows times the expected row bytes (wh_expected_row_bytes), the sum of
--                  avg_width * (1 - null_frac) plus 28 bytes of tuple header and item pointer, as before.
--                * The TOAST relation: its bytes over its live chunks times block_size / 4 (wh_toast_bloat_ratio).
--                  Postgres sizes a TOAST chunk (TOAST_MAX_CHUNK_SIZE, 1,996 bytes of data at the default 8 kB block)
--                  so that exactly four chunk rows fill a page (TOAST_TUPLES_PER_PAGE = 4), so densely packed live
--                  chunks occupy a quarter page each and a healthy TOAST relation reads at or below 1 (a value's last
--                  chunk is partial). The live chunk count is the TOAST relation's pg_class.reltuples, which VACUUM
--                  keeps; ANALYZE never visits a TOAST relation, so a count VACUUM never took (-1) is no measure and
--                  the heap alone decides. The count is floored at table_rewrite_min_rows, as a heap below that many
--                  rows is not measured, so a small TOAST relation never reads alarmingly bloated.
--              The worse of the two rather than one combined ratio: a rewrite copies both, and either one holding most
--              of its pages empty is what a rewrite returns. Every input is a catalog read; nothing scans a table.
--
--              wh_measure_table_bloat_approx (copied from 254, rule 5) measures the TOAST relation with
--              pgstattuple_approx too, on the same scale: while pgstattuple is Ready it decides whether a shortlisted
--              table is queued, and heap-only it turned away every table the TOAST measure offered.
--
--              The post-rewrite floor: the estimate cannot see page headers, alignment or the fillfactor reserve, so a
--              freshly rewritten table can read 1.5x to 2x and stay there. With a low threshold such a table would be
--              offered again every cycle for nothing. A table whose latest rewrite (wh_table_rewrite_history) left it at
--              bloat_after F is offered only once its ratio reaches F * table_rewrite_regrowth_factor (default 1.5),
--              or the threshold if that is higher: churn on a table whose healthy reading sits above the threshold
--              stops, and real regrowth is still caught. bloat_after is on the estimate's scale whichever method took
--              it (254 keeps the approximate measurement on that scale), so the two compare. A table with no history
--              (or history older than its 90-day bound) is offered as before. A lowered fillfactor
--              (wh_tables_owed_fillfactor_rewrite) is owed one rewrite whatever its bloat and is not held back.
--              wh_table_rewrite_queue_status reports the queue's length, the threshold and how many tables the floor
--              held back, so the online step says what it saw when it has nothing to do.
--
--              wh_tables_needing_rewrite (213) keeps its return shape and its rule that a requested table is offered
--              only while it measures at or above the line: an already rewritten table re-requested by a replayed
--              migration must not be rewritten again, and an external pg_repack job reads this function.
--
--              Number 430: develop ends at 412, and 413 to 429 are held by open changes.
--
-- Reversal: reversible (the heap-only definitions of 213, 254 and 410 are restored and the functions added here dropped)
-- Dependencies: 089 and 213 (wh_tables_needing_rewrite), 254 (wh_measure_table_bloat_approx), 410 (wh_tables_owed_fillfactor_rewrite), 411 (wh_table_rewrite_history)
-- Objects: wh_expected_row_bytes, wh_toast_bloat_ratio, wh_estimate_table_bloat, _wh_table_rewrite_scan, wh_tables_needing_rewrite, wh_table_rewrite_queue_status, wh_tables_owed_fillfactor_rewrite, wh_measure_table_bloat_approx
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AMostlyNullWideColumn_DoesNotHideHeapBloatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AnUntoastedTableWithoutNulls_ReadsAsTheHeapOnlyEstimateDidAsync</tests>
-- The bytes a live row of a table in this schema is expected to occupy: each column's pg_stats width weighed by how
-- often it is present, plus 28 bytes of tuple header and item pointer. NULL-free columns read exactly as before.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_expected_row_bytes(p_table TEXT)
RETURNS NUMERIC
LANGUAGE sql
STABLE
AS $$
  SELECT COALESCE(sum(s.avg_width::NUMERIC * (1 - s.null_frac::NUMERIC)), 0) + 28
  FROM pg_stats s
  WHERE s.schemaname = replace('__SCHEMA__', '"', '') AND s.tablename = p_table;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_expected_row_bytes(TEXT) IS
'The bytes a live row is expected to occupy: the sum of pg_stats avg_width * (1 - null_frac) over the table''s columns, plus 28 bytes of tuple header and item pointer.';

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AMostlyDeadToast_UnderALeanHeap_IsACandidateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:ASmallDeadToast_IsMeasuredAgainstTheMinimumRows_SoItIsNotOfferedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AToastNeverVacuumed_IsNotMeasured_ForItsLiveChunksAreUnknownAsync</tests>
-- A TOAST relation's bloat ratio: its bytes over its live chunks times a quarter page (four chunk rows fill a page),
-- the chunk count floored at table_rewrite_min_rows. NULL when the live chunk count is unknown (negative or NULL).
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_toast_bloat_ratio(p_toast_bytes NUMERIC, p_live_chunks NUMERIC)
RETURNS NUMERIC
LANGUAGE sql
STABLE
AS $$
  SELECT CASE WHEN p_live_chunks >= 0 THEN
    p_toast_bytes / (GREATEST(p_live_chunks,
                              COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings
                                        WHERE setting_key = 'table_rewrite_min_rows'), '1000')::NUMERIC,
                              1)
                     * (current_setting('block_size')::NUMERIC / 4))
  END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_toast_bloat_ratio(NUMERIC, NUMERIC) IS
'A TOAST relation''s bloat ratio: its bytes over its live chunks (floored at table_rewrite_min_rows) times block_size / 4, the space a densely packed chunk occupies. NULL when the live chunk count is unknown.';

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AMostlyDeadToast_UnderALeanHeap_IsACandidateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AHealthyToastedTable_IsNotACandidate_AndItsEstimateReadsLeanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:Approx_IsTheSameScaleAsTheEstimate_OnATableWhoseStatisticsAreCurrentAsync</tests>
-- A table's catalog bloat estimate, in this schema: the worse of its heap ratio and its TOAST relation's ratio,
-- rounded to two places. NULL when the table is not there, or has neither rows nor a measurable TOAST relation.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_estimate_table_bloat(p_table TEXT)
RETURNS NUMERIC
LANGUAGE sql
STABLE
AS $$
  SELECT round(GREATEST(
           (pg_relation_size(c.oid)::NUMERIC / NULLIF(GREATEST(c.reltuples, 0)::NUMERIC, 0))
             / GREATEST(__SCHEMA__.wh_expected_row_bytes(c.relname::TEXT), 1),
           __SCHEMA__.wh_toast_bloat_ratio(pg_relation_size(t.oid)::NUMERIC, t.reltuples::NUMERIC)), 2)
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  LEFT JOIN pg_class t ON t.oid = c.reltoastrelid
  WHERE n.nspname = replace('__SCHEMA__', '"', '') AND c.relname = p_table AND c.relkind = 'r';
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_estimate_table_bloat(TEXT) IS
'A table''s catalog bloat estimate: the worse of its heap bytes over rows times the expected row bytes, and its TOAST relation''s bytes over live chunks times a quarter page. The one estimate the rewrite scan, its queues and the post-rewrite re-measure read.';

-- <docs>operations/infrastructure/storage-maintenance#post-rewrite-floor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RewriteRegrowthFloorSqlTests.cs:ARewrittenTable_BelowItsRegrowthFloor_IsHeldBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AMostlyDeadToast_UnderALeanHeap_IsACandidateAsync</tests>
-- Every framework table in this schema with at least table_rewrite_min_rows rows: its estimate, whether it is queued,
-- and the floor its latest recorded rewrite set (bloat_after * table_rewrite_regrowth_factor), NULL with no history.
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_table_rewrite_scan()
RETURNS TABLE (table_name TEXT, bloat_ratio NUMERIC, requested BOOLEAN, regrowth_floor NUMERIC)
LANGUAGE sql
STABLE
AS $$
  WITH s AS (
    SELECT COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_min_rows'), '1000')::BIGINT AS min_rows,
           COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_regrowth_factor'), '1.5')::NUMERIC AS factor,
           string_to_array(NULLIF(COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites'), ''), ''), ',') AS queued
  ), h AS (
    SELECT DISTINCT ON (r.table_name) r.table_name, r.bloat_after
    FROM __SCHEMA__.wh_table_rewrite_history r
    WHERE r.bloat_after IS NOT NULL
    ORDER BY r.table_name, r.id DESC
  )
  SELECT c.relname::TEXT,
         __SCHEMA__.wh_estimate_table_bloat(c.relname::TEXT),
         (s.queued IS NOT NULL AND c.relname = ANY(s.queued)),
         h.bloat_after * s.factor
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  CROSS JOIN s
  LEFT JOIN h ON h.table_name = c.relname
  WHERE n.nspname = replace('__SCHEMA__', '"', '')
    AND c.relkind = 'r'
    AND c.relname LIKE 'wh\_%'
    AND c.reltuples >= s.min_rows;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_table_rewrite_scan() IS
'Every framework table with at least table_rewrite_min_rows rows: its bloat estimate, whether it is queued, and the floor its latest rewrite set (bloat_after times table_rewrite_regrowth_factor; NULL without history). Read by wh_tables_needing_rewrite and wh_table_rewrite_queue_status.';

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:AMostlyDeadToast_UnderALeanHeap_IsACandidateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RewriteRegrowthFloorSqlTests.cs:ARewrittenTable_BelowItsRegrowthFloor_IsHeldBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RewriteRegrowthFloorSqlTests.cs:ATableWithoutHistory_IsOfferedAtTheThresholdAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteRequestSqlTests.cs:LeanTable_IsNotOfferedForRewrite_EvenWhenRequestedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:RewriteCandidatesAreMeasuredInTheContextsSchemaAsync</tests>
-- What the maintenance worker should rewrite RIGHT NOW. Every candidate is re-measured here rather than trusted from
-- the request list, so a replayed migration that re-requests an already rewritten table offers nothing, and a table
-- that churns back into bloat is offered again without anyone re-requesting it. A table is offered when its estimate
-- reaches the threshold, and, when a recorded rewrite set a floor, the floor too.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_tables_needing_rewrite()
RETURNS TABLE (table_name TEXT, bloat_ratio NUMERIC, requested BOOLEAN)
LANGUAGE plpgsql
AS $$
DECLARE
  v_threshold NUMERIC;
BEGIN
  SELECT COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_bloat_threshold'), '3.0')::NUMERIC
    INTO v_threshold;

  RETURN QUERY
  SELECT s.table_name, s.bloat_ratio, s.requested
  FROM __SCHEMA__._wh_table_rewrite_scan() s
  WHERE s.bloat_ratio >= GREATEST(v_threshold, s.regrowth_floor)
  ORDER BY 2 DESC;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_tables_needing_rewrite IS
'Tables whose heap or TOAST relation is disproportionate to their live rows, re-measured on every call so an already-rewritten or never-bloated table is never offered, and held back until it regrows past the floor its last rewrite set. Union of migration-requested and threshold-detected.';

-- <docs>operations/infrastructure/storage-maintenance#online-rewrite</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RewriteRegrowthFloorSqlTests.cs:TheQueueStatus_CountsTheQueue_AndTheTablesTheFloorHeldBackAsync</tests>
-- The rewrite queue at a glance: how many tables are queued, the threshold, and how many tables reach the threshold
-- but are held back by the floor their last rewrite set.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_table_rewrite_queue_status()
RETURNS TABLE (requested INTEGER, threshold NUMERIC, held_back INTEGER)
LANGUAGE sql
STABLE
AS $$
  WITH t AS (
    SELECT COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_bloat_threshold'), '3.0')::NUMERIC AS line
  )
  SELECT COALESCE(cardinality(string_to_array(NULLIF(
           (SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites'), ''), ',')), 0),
         t.line,
         (SELECT count(*)::INTEGER FROM __SCHEMA__._wh_table_rewrite_scan() s
          WHERE s.bloat_ratio >= t.line AND s.bloat_ratio < s.regrowth_floor)
  FROM t;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_table_rewrite_queue_status() IS
'The rewrite queue''s length, the bloat threshold, and how many tables at or above it are held back by the floor their last rewrite set.';

-- <docs>operations/infrastructure/storage-maintenance#fillfactor-decrease</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:TheFillFactorQueue_OrdersByTheSameEstimateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RewriteRegrowthFloorSqlTests.cs:AFillFactorDecrease_IsNotHeldBackByTheFloorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs:AFillFactorDecrease_OnACompactTable_IsRewrittenOnceOnline_AndAnUpdateOfAnOldRowIsHotAsync</tests>
-- The queued tables owed a rewrite for a lowered fillfactor, in this schema, with their bloat estimate for the queue's
-- ordering (0 for a table never analyzed). A row whose table is no longer queued, or no longer exists, is not offered.
-- The post-rewrite floor does not apply: the rewrite is owed whatever the table's bloat.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_tables_owed_fillfactor_rewrite()
RETURNS TABLE (table_name TEXT, bloat_ratio NUMERIC)
LANGUAGE sql
STABLE
AS $$
  SELECT c.relname::TEXT,
         COALESCE(__SCHEMA__.wh_estimate_table_bloat(c.relname::TEXT), 0)
  FROM __SCHEMA__.wh_fillfactor_rewrite_requests f
  JOIN pg_class c ON c.relname = f.table_name AND c.relkind = 'r'
  JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = replace('__SCHEMA__', '"', '')
  WHERE f.table_name = ANY (COALESCE(string_to_array(NULLIF(
          (SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites'), ''), ','), ARRAY[]::TEXT[]))
  ORDER BY 1;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_tables_owed_fillfactor_rewrite() IS
'Queued tables owed one rewrite because their fillfactor was lowered while they held rows, whatever their bloat, with their bloat estimate.';

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ToastAwareBloatEstimateSqlTests.cs:Approx_MeasuresTheToastRelationToo_SoItConfirmsTheEstimateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:Approx_OnAGenuinelyBloatedTable_ConfirmsTheEstimateAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:Approx_OnAStaleEstimate_MeasuresTheTableLeanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:Approx_WithoutTheExtension_FailsRatherThanAnsweringAsync</tests>
-- A framework table's bloat ratio measured with pgstattuple_approx, on the scale of the catalog estimate: the worse of
-- the heap (bytes over live rows times the expected row bytes) and the TOAST relation (bytes over live chunks times a
-- quarter page), each counted by pgstattuple_approx rather than estimated.
--
-- The extension's functions are called in whichever schema it was created in, read from pg_extension, so the call
-- does not depend on the caller's search_path. A missing extension raises rather than answering NULL: the caller asks
-- only while the capability reads Ready, so its absence is a fault to report. NULL when the table does not exist in
-- this schema or holds nothing to measure.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_measure_table_bloat_approx(p_table TEXT)
RETURNS NUMERIC
LANGUAGE plpgsql
AS $$
DECLARE
  v_extension_schema TEXT;
  v_table REGCLASS;
  v_toast REGCLASS;
  v_heap NUMERIC;
  v_toast_bytes NUMERIC;
  v_toast_chunks NUMERIC;
  v_toast_ratio NUMERIC;
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
    'SELECT (a.table_len::NUMERIC / NULLIF(a.approx_tuple_count::NUMERIC, 0)) / GREATEST($2, 1)
     FROM %I.pgstattuple_approx($1) a', v_extension_schema)
    INTO v_heap
    USING v_table, __SCHEMA__.wh_expected_row_bytes(p_table);

  SELECT NULLIF(c.reltoastrelid, 0)::REGCLASS INTO v_toast FROM pg_catalog.pg_class c WHERE c.oid = v_table;
  IF v_toast IS NOT NULL THEN
    EXECUTE format('SELECT a.table_len::NUMERIC, a.approx_tuple_count::NUMERIC FROM %I.pgstattuple_approx($1) a', v_extension_schema)
      INTO v_toast_bytes, v_toast_chunks
      USING v_toast;
    v_toast_ratio := __SCHEMA__.wh_toast_bloat_ratio(v_toast_bytes, v_toast_chunks);
  END IF;

  RETURN round(GREATEST(v_heap, v_toast_ratio), 2);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_measure_table_bloat_approx(TEXT) IS
'A framework table''s bloat ratio measured with pgstattuple_approx: the worse of heap bytes over live rows times the expected row bytes and TOAST bytes over live chunks times a quarter page, the catalog estimate''s scale with the rows and chunks counted rather than estimated. Raises when pgstattuple is not installed.';
