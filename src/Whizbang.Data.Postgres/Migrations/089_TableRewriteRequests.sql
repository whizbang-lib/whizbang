-- 089_TableRewriteRequests.sql
--
-- A table can hold far more space than its live rows need, and it costs on every read: index
-- heap-fetches pull emptier pages and the buffer cache holds fewer useful rows. Two causes:
--
--   1. Churn. The queue tables delete rows constantly. Autovacuum reclaims that space to the
--      free space map for REUSE, but never returns it to the OS, so the file stays large and
--      scans keep paying for the empty pages. Measured on a live queue table: 160 MB with ZERO
--      dead tuples, and a scan of the unpublished rows at 65 ms. After a rewrite: 47 MB, the
--      identical 23,872 rows, 26 ms. Autovacuum tuning cannot fix this — only a rewrite does.
--
--   2. A dropped column. Postgres keeps that column's bytes in every row written before the
--      drop, permanently, and autovacuum can never reclaim them.
--
-- VACUUM FULL / CLUSTER cannot run here: migrations execute inside a transaction and both are
-- forbidden in one. ALTER TABLE ... SET ACCESS METHOD would rewrite in-transaction, but a
-- migration cannot know how large a consumer's table is, and committing an operator to an
-- unbounded ACCESS EXCLUSIVE lock plus ~2x transient disk mid-deploy is not a safe default.
--
-- So a migration RECORDS that a rewrite is owed, and the maintenance worker performs it later
-- under operator policy — outside a transaction, where VACUUM FULL is available.
--
-- Dependencies: 032 (wh_settings)

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description) VALUES
  ('table_rewrite_policy', 'off', 'string',
   'off = detect and report only (default); immediate = maintenance may VACUUM FULL a qualifying table, which takes an ACCESS EXCLUSIVE lock for the duration of the rewrite'),
  ('table_rewrite_bloat_threshold', '3.0', 'string',
   'Heap-bytes-per-row over expected-row-width above which a tracked table is offered for rewrite. ~1.0 is lean.'),
  ('table_rewrite_min_rows', '1000', 'string',
   'Tables below this many live rows are ignored: the per-row average is dominated by page overhead and reports alarming ratios for tables measured in kilobytes.'),
  ('pending_table_rewrites', '', 'string',
   'Comma-separated tables a migration has recorded as owing a rewrite. Cleared per table once done.')
ON CONFLICT (setting_key) DO NOTHING;

-- Records that a table owes a rewrite. Idempotent: re-running the migration that calls this
-- (which happens whenever its content hash changes) will not duplicate the entry.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_request_table_rewrite(p_table TEXT)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_current TEXT;
BEGIN
  SELECT COALESCE(setting_value, '') INTO v_current
  FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites';

  IF v_current IS NULL THEN
    v_current := '';
  END IF;

  IF p_table = ANY(string_to_array(NULLIF(v_current, ''), ',')) THEN
    RETURN;   -- already recorded
  END IF;

  UPDATE __SCHEMA__.wh_settings
  SET setting_value = CASE WHEN v_current = '' THEN p_table ELSE v_current || ',' || p_table END,
      updated_at = NOW()
  WHERE setting_key = 'pending_table_rewrites';
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_request_table_rewrite IS
'Records that a table owes a rewrite (e.g. after a DROP COLUMN, whose bytes Postgres keeps in every pre-existing row). The maintenance worker performs it under operator policy; VACUUM FULL cannot run inside the migration transaction.';

-- What the maintenance worker should rewrite RIGHT NOW.
--
-- Every candidate is re-measured here rather than trusted from the request list. That is what
-- makes the mechanism safe to leave switched on:
--   * a migration whose content hash changed replays and re-requests a table that was already
--     rewritten — re-measuring returns nothing, so no pointless multi-minute lock;
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
  SELECT st.relname::TEXT,
         round((pg_relation_size(st.relid)::NUMERIC / st.n_live_tup) / GREATEST(w.expected, 1), 2),
         (v_requested IS NOT NULL AND st.relname = ANY(v_requested))
  FROM pg_stat_user_tables st
  JOIN LATERAL (
    SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
    FROM pg_stats s
    WHERE s.schemaname = st.schemaname AND s.tablename = st.relname
  ) w ON TRUE
  WHERE st.schemaname = current_schema()
    AND st.relname LIKE 'wh\_%'
    AND st.n_live_tup >= v_min_rows
    AND ((pg_relation_size(st.relid)::NUMERIC / st.n_live_tup) / GREATEST(w.expected, 1)) >= v_threshold
  ORDER BY 2 DESC;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_tables_needing_rewrite IS
'Tables whose heap is disproportionate to their live rows, re-measured on every call so an already-rewritten or never-bloated table is never offered. Union of migration-requested and threshold-detected.';

-- Clears a table's recorded request. Called only after a rewrite is verified to have worked, so
-- an interrupted or ineffective rewrite is retried on the next cycle instead of being forgotten.
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
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_clear_table_rewrite IS
'Removes a table from the pending-rewrite list. Call only after verifying the rewrite reduced the ratio, so an interrupted rewrite is retried rather than lost.';

-- The event store carries this from 078: the inline body columns were dropped there, and their
-- bytes persist in every row written before that migration. Harmless on a fresh deployment (the
-- table was empty, so wh_tables_needing_rewrite() re-measures it as lean and returns nothing).
SELECT __SCHEMA__.wh_request_table_rewrite('wh_event_store');
