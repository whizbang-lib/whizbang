-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 405_AFillFactorDecreaseIsRewrittenOnce.sql
-- Date: 2026-11-13
-- Description: A rewrite request records when it is owed because a table's fillfactor was lowered, and such a table is
--              offered for one rewrite whatever its bloat (#2300).
--
--              A lowered fillfactor reaches only the pages written from then on. The schema pass already queues a
--              rewrite when it changes a perspective table's fillfactor (#1592), but the queue is a bare list of names
--              (pending_table_rewrites), and wh_tables_needing_rewrite offers a queued table only once it measures as
--              bloated. A compact table that was given fillfactor 90 is never bloated, so it never got the free page
--              space it was given until each row was moved by an update. #1594 decided to wait for bloat until an online
--              rewrite existed; pg_squeeze and pg_repack now run from the maintenance cycle within a byte budget, so one
--              rewrite of such a table is cheap and is now owed.
--
--              The reason is a row beside the queue, as the bloat measurement is (254): wh_fillfactor_rewrite_requests
--              records the fillfactor the pages were packed at before the first pending change and the one in force
--              now, and only while the second is lower. wh_request_fillfactor_rewrite records the request as
--              wh_request_table_rewrite does and keeps the row in step: a later change keeps the first fillfactor, and a
--              change back to it or above leaves no decrease, so the row goes. wh_tables_owed_fillfactor_rewrite lists
--              the queued tables with a decrease, with the catalog estimate of their ratio for the queue's ordering.
--              wh_tables_needing_rewrite is unchanged, so the bloat rule and its readers are as they were; the
--              coordinator reads both, and the offered table says which rule offered it, so a blocking VACUUM FULL is
--              never run for a decrease. wh_clear_table_rewrite, copied whole from 254 (README rule 5), now deletes the
--              row with the request, so a request honored once is not honored again.
--
--              Number 405: develop ends at 401, 403 is held by an open change, and odd numbers are develop's.
--
-- Reversal: lossy (the recorded decreases are dropped; a table still queued then waits for bloat, as before)
-- Dependencies: 032 (wh_settings), 089 (wh_request_table_rewrite), 254 (wh_clear_table_rewrite)
-- Objects: wh_fillfactor_rewrite_requests, wh_request_fillfactor_rewrite, wh_tables_owed_fillfactor_rewrite, wh_clear_table_rewrite
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_fillfactor_rewrite_requests (
  table_name TEXT PRIMARY KEY,
  from_fillfactor INTEGER NOT NULL,
  to_fillfactor INTEGER NOT NULL,
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_fillfactor_rewrite_requests_decrease CHECK (to_fillfactor < from_fillfactor)
);

COMMENT ON TABLE __SCHEMA__.wh_fillfactor_rewrite_requests IS
'Queued rewrite requests (pending_table_rewrites) owed because the table''s fillfactor was lowered while it held rows: the fillfactor its pages were packed at, and the lower one in force. Such a table is rewritten once by an online strategy, whatever its bloat.';

-- <docs>operations/infrastructure/storage-maintenance#fillfactor-decrease</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs:TheRequest_RecordsTheDecreaseFromWhereThePagesWerePackedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs:AFillFactorIncrease_IsNotOffered_ForTheBloatRuleAloneDecidesAsync</tests>
-- Queues a rewrite of a table whose fillfactor changed from p_from to p_to, as wh_request_table_rewrite does, and
-- records the decrease when the pages are packed fuller than the fillfactor now in force.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_request_fillfactor_rewrite(p_table TEXT, p_from INTEGER, p_to INTEGER)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_packed INTEGER;
BEGIN
  PERFORM __SCHEMA__.wh_request_table_rewrite(p_table);

  -- The pages are packed at the fillfactor before the first change still pending, not the one before this change.
  SELECT from_fillfactor INTO v_packed FROM __SCHEMA__.wh_fillfactor_rewrite_requests WHERE table_name = p_table;
  v_packed := COALESCE(v_packed, p_from);

  IF p_to < v_packed THEN
    INSERT INTO __SCHEMA__.wh_fillfactor_rewrite_requests (table_name, from_fillfactor, to_fillfactor, requested_at)
    VALUES (p_table, v_packed, p_to, now())
    ON CONFLICT (table_name) DO UPDATE SET to_fillfactor = EXCLUDED.to_fillfactor, requested_at = EXCLUDED.requested_at;
  ELSE
    DELETE FROM __SCHEMA__.wh_fillfactor_rewrite_requests WHERE table_name = p_table;
  END IF;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_request_fillfactor_rewrite(TEXT, INTEGER, INTEGER) IS
'Records that a table owes a rewrite because its fillfactor changed, and whether the change leaves its pages packed fuller than the fillfactor in force (a decrease, rewritten once online whatever its bloat).';

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

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:AMeasuredRequest_IsQueuedWithItsRatioAndMethod_AndTheQueueOrdersByTheMeasuredRatioAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteRequestSqlTests.cs:RequestIsIdempotent_AndClearRemovesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs:TheRequest_RecordsTheDecreaseFromWhereThePagesWerePackedAsync</tests>
-- Clears a table's recorded request, the measurement it was queued with, and the fillfactor decrease it was owed for.
-- Called only after a rewrite is verified to have worked, so an interrupted or ineffective rewrite is retried on the
-- next cycle instead of being forgotten.
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
  DELETE FROM __SCHEMA__.wh_fillfactor_rewrite_requests WHERE table_name = p_table;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_clear_table_rewrite(TEXT) IS
'Removes a table from the pending-rewrite list, with the measurement it was queued with and the fillfactor decrease it was owed for. Call only after verifying the rewrite worked, so an interrupted rewrite is retried rather than lost.';
