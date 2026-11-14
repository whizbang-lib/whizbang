-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 407_ARewriteRecordsWhatCarriedItOut.sql
-- Date: 2026-11-13
-- Description: A completed table rewrite records which strategy carried out its request (#2301).
--
--              A rewrite request can now be carried out four ways: VACUUM FULL at boot, pg_squeeze or pg_repack from the
--              maintenance cycle, and, under TableRewriteStrategy = Online with neither extension usable, a blue-green
--              rebuild of a perspective table. The request itself is deleted once it is done (wh_clear_table_rewrite),
--              so nothing said afterwards what rewrote a table, or why. wh_table_rewrite_history keeps one row per
--              completed rewrite: the table, the strategy, the reason it was owed (Bloat or FillFactorDecrease), and the
--              bloat ratio before and after. wh_complete_table_rewrite writes the row and clears the request in one
--              call, which both drivers' coordinators make wherever a rewrite is verified.
--
--              The history is bounded by the write that adds to it: rows older than 90 days, and all but the newest
--              1,000, are deleted then. A rewrite is rare (a few a day at most), so the trim is a few rows over an index,
--              and the table needs no sweep of its own in the maintenance cycle.
--
--              Number 407: 405 is this change's first migration, and odd numbers are develop's.
--
-- Reversal: lossy (the history is dropped; requests are cleared as before)
-- Dependencies: 405 (wh_clear_table_rewrite)
-- Objects: wh_table_rewrite_history, wh_complete_table_rewrite
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_table_rewrite_history (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  table_name TEXT NOT NULL,
  strategy TEXT NOT NULL,
  reason TEXT NOT NULL,
  bloat_before NUMERIC(12, 2),
  bloat_after NUMERIC(12, 2),
  completed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_table_rewrite_history_completed_at ON __SCHEMA__.wh_table_rewrite_history (completed_at);

COMMENT ON TABLE __SCHEMA__.wh_table_rewrite_history IS
'One row per completed table rewrite: the strategy that carried out the request (VacuumFull, PgSqueeze, PgRepack, BlueGreenRebuild), why it was owed, and the bloat ratio before and after. Bounded to 90 days and 1,000 rows by the write that adds one.';

-- <docs>operations/infrastructure/storage-maintenance#rewrite-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs:TheRewriteHistory_RecordsEachRewrite_AndStaysBoundedAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperTableMaintenanceTests.cs:CompleteTableRewrite_ClearsTheRequest_AndRecordsTheStrategyAsync</tests>
-- Records a completed rewrite and clears its request (wh_clear_table_rewrite), then trims the history to its bounds.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_complete_table_rewrite(
  p_table TEXT, p_strategy TEXT, p_reason TEXT, p_bloat_before NUMERIC, p_bloat_after NUMERIC)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_table_rewrite_history (table_name, strategy, reason, bloat_before, bloat_after)
  VALUES (p_table, p_strategy, p_reason, p_bloat_before, p_bloat_after);

  PERFORM __SCHEMA__.wh_clear_table_rewrite(p_table);

  DELETE FROM __SCHEMA__.wh_table_rewrite_history WHERE completed_at < now() - interval '90 days';
  DELETE FROM __SCHEMA__.wh_table_rewrite_history
  WHERE id < (SELECT h.id FROM __SCHEMA__.wh_table_rewrite_history h ORDER BY h.id DESC OFFSET 999 LIMIT 1);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_complete_table_rewrite(TEXT, TEXT, TEXT, NUMERIC, NUMERIC) IS
'Records a completed table rewrite (its strategy, reason and bloat before and after) and clears the table''s request; keeps the history to 90 days and 1,000 rows.';
