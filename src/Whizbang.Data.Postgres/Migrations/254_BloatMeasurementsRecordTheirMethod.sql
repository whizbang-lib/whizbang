-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 254_BloatMeasurementsRecordTheirMethod.sql
-- Date: 2026-10-28
-- Description: A table queued for rewrite records the ratio that queued it and how that ratio was obtained (#1807).
--
--              The bloat ratio is estimated from the catalog: the heap size over the planner's row count times the
--              row width pg_stats reports. That costs nothing and runs every statistics pass, but it guesses: rows
--              added since the last ANALYZE are not in the row count, so a growing table can read as bloated. When
--              the pgstattuple extension is Ready, the maintenance cycle now measures a shortlisted table with
--              pgstattuple_approx before it enters the queue, and does not queue it when the measurement is under
--              the threshold. pgstattuple_approx reads the visibility map and scans only the pages that are not
--              all-visible; the exact pgstattuple scan is never run by the library.
--
--              The queue itself stays the pending_table_rewrites list in wh_settings, which migrations append to
--              through wh_request_table_rewrite. The measurement is a row beside it, keyed by table:
--                * method is 'estimate', 'approx' or 'exact'. It defaults to 'estimate', and a queued table with no
--                  row at all reads as the estimate, which is what every request recorded before this migration was.
--                * bloat_ratio is the ratio the queue is ordered by, largest first.
--                * estimated_ratio is the estimate the measurement confirmed, so both figures are kept.
--              wh_clear_table_rewrite, copied whole from its definition in 089 (README rule 5), now deletes the row
--              with the request, so a later request is never ordered by a stale measurement.
--
--              The measured ratio is on the estimate's scale: the heap size over the live rows times the expected
--              row width, with pgstattuple_approx counting the live rows instead of the planner. It is deliberately
--              not the heap over the live tuples' own bytes: a dropped column's bytes stay inside every
--              pre-existing tuple, so that ratio would call a table carrying them lean, and a rewrite that removed
--              them would not read as an improvement.
--
-- Dependencies: 032 (wh_settings), 089 (wh_request_table_rewrite, wh_clear_table_rewrite)
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_table_rewrite_measurements (
  table_name TEXT PRIMARY KEY,
  bloat_ratio NUMERIC NOT NULL,
  method TEXT NOT NULL DEFAULT 'estimate'
    CONSTRAINT ck_table_rewrite_measurements_method CHECK (method IN ('estimate', 'approx', 'exact')),
  estimated_ratio NUMERIC,
  measured_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE __SCHEMA__.wh_table_rewrite_measurements IS
'The measurement a table in the rewrite queue (pending_table_rewrites) was queued with. A queued table without a row was queued from the catalog estimate.';

COMMENT ON COLUMN __SCHEMA__.wh_table_rewrite_measurements.method IS
'How bloat_ratio was obtained: estimate (pg_stats), approx (pgstattuple_approx) or exact (pgstattuple).';

COMMENT ON COLUMN __SCHEMA__.wh_table_rewrite_measurements.estimated_ratio IS
'The catalog estimate the measurement confirmed; NULL when the row records the estimate itself.';

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

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:AMeasuredRequest_IsQueuedWithItsRatioAndMethod_AndTheQueueOrdersByTheMeasuredRatioAsync</tests>
-- Queues a table for rewrite, as wh_request_table_rewrite does, with the measurement that confirmed it.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_request_measured_table_rewrite(
  p_table TEXT, p_ratio NUMERIC, p_method TEXT, p_estimated_ratio NUMERIC)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  PERFORM __SCHEMA__.wh_request_table_rewrite(p_table);

  INSERT INTO __SCHEMA__.wh_table_rewrite_measurements (table_name, bloat_ratio, method, estimated_ratio, measured_at)
  VALUES (p_table, p_ratio, p_method, p_estimated_ratio, now())
  ON CONFLICT (table_name) DO UPDATE
  SET bloat_ratio = EXCLUDED.bloat_ratio,
      method = EXCLUDED.method,
      estimated_ratio = EXCLUDED.estimated_ratio,
      measured_at = EXCLUDED.measured_at;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_request_measured_table_rewrite(TEXT, NUMERIC, TEXT, NUMERIC) IS
'Records that a table owes a rewrite, with the measured ratio the queue orders it by, how it was measured, and the estimate it confirmed.';

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:AMeasuredRequest_IsQueuedWithItsRatioAndMethod_AndTheQueueOrdersByTheMeasuredRatioAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteRequestSqlTests.cs:RequestIsIdempotent_AndClearRemovesItAsync</tests>
-- Clears a table's recorded request and the measurement it was queued with. Called only after a rewrite is verified
-- to have worked, so an interrupted or ineffective rewrite is retried on the next cycle instead of being forgotten.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_clear_table_rewrite(p_table TEXT)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_remaining TEXT;
BEGIN
  SELECT COALESCE(array_to_string(array_remove(
           string_to_array(NULLIF(COALESCE(setting_value, ''), ''), ','), p_table), ','), '')
    INTO v_remaining
  FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites';

  UPDATE __SCHEMA__.wh_settings
  SET setting_value = COALESCE(v_remaining, ''), updated_at = NOW()
  WHERE setting_key = 'pending_table_rewrites';

  DELETE FROM __SCHEMA__.wh_table_rewrite_measurements WHERE table_name = p_table;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_clear_table_rewrite(TEXT) IS
'Removes a table from the pending-rewrite list, with the measurement it was queued with. Call only after verifying the rewrite reduced the ratio, so an interrupted rewrite is retried rather than lost.';
