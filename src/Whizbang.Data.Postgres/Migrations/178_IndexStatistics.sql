-- Migration: 178_IndexStatistics
-- Date: 2026-09-30
-- Description: A table an index was just created on is analyzed, so the planner has statistics for the
--   index's expression (#1004).
--
--   PostgreSQL has no statistics for a new expression until the table is analyzed, and autovacuum
--   analyzes only once about a tenth of the rows have changed. Until then a predicate on the expression
--   is estimated with a fixed default: for IS NOT NULL the default says nearly every row matches, so a
--   selective predicate over a new index is planned as a scan of the whole table and the index is not
--   used. A perspective table of 87k rows answered in seconds what its new index answers in
--   milliseconds.
--
--   Three pieces:
--   - wh_index_statistics_pending holds each table wh_ensure_index created an index on. The schema pass
--     drains it after its transaction commits and analyzes each table once, outside the transaction.
--   - wh_ensure_index is 174's function, copied verbatim, plus the insert into that queue when it
--     creates an index. An index found existing, or equivalent to one under another name, queues nothing.
--   - wh_tables_needing_index_statistics lists, for the maintenance step, the queued tables and every
--     perspective table with an expression index that has no statistics yet: an index built outside the
--     pass, or queued by a start that died before its ANALYZE. Bounded by its argument.
-- Dependencies: 000 (drop_all_overloads), 174 (wh_ensure_index)
-- Objects: wh_index_statistics_pending, wh_ensure_index, wh_tables_needing_index_statistics
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- One row per table, fully qualified as format('%I.%I') renders it, so the name reads the same on any
-- connection whatever its search_path.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_index_statistics_pending (
  table_name TEXT PRIMARY KEY,
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE __SCHEMA__.wh_index_statistics_pending IS
  'Tables wh_ensure_index created an index on whose ANALYZE has not run yet. Drained by the schema pass '
  'after it commits, and by the index-statistics maintenance step.';

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_ensure_index');

-- <docs>fundamentals/perspectives/perspective-indexes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/EnsureIndexFunctionTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/DocumentIndexInitializationTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/IndexStatisticsInitializationTests.cs:AnIndexThePassCreates_HasStatisticsAndIsChosenForASelectivePredicateAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ensure_index(p_ddl TEXT) RETURNS TEXT AS $$
DECLARE
  -- One identifier: quoted (doubled quotes inside) or bare.
  c_ident CONSTANT TEXT := '(?:"(?:[^"]|"")+"|[^\s".(]+)';
  v_parts TEXT[];
  v_unique TEXT;
  v_name TEXT;
  v_table REGCLASS;
  v_body TEXT;
  v_method TEXT;
  v_probe TEXT;
  v_shape TEXT;
  v_equivalent TEXT;
BEGIN
  v_parts := regexp_match(p_ddl,
    '^\s*CREATE\s+(UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\s+(' || c_ident || ')\s+ON\s+('
      || c_ident || '(?:\.' || c_ident || ')?)\s+(.+)$',
    'i');

  -- Not a shape this compares: run it exactly as before the comparison existed.
  IF v_parts IS NULL THEN
    EXECUTE p_ddl;
    RETURN 'executed';
  END IF;

  v_unique := CASE WHEN v_parts[1] IS NULL THEN '' ELSE 'UNIQUE ' END;
  -- The name as the catalog stores it: a quoted one verbatim, a bare one folded as the parser folds it.
  v_name := CASE
    WHEN left(v_parts[2], 1) = '"' THEN replace(substr(v_parts[2], 2, length(v_parts[2]) - 2), '""', '"')
    ELSE lower(v_parts[2])
  END;
  v_table := v_parts[3]::regclass;
  v_body := btrim(v_parts[4]);

  -- The name is taken: what IF NOT EXISTS would decide, and the common case on every later start.
  IF EXISTS (
    SELECT 1 FROM pg_class c
    WHERE c.relname = v_name
      AND c.relnamespace = (SELECT t.relnamespace FROM pg_class t WHERE t.oid = v_table)
  ) THEN
    RETURN 'exists';
  END IF;

  -- Only an index of the same access method can have the same definition, so a table without one
  -- needs no probe.
  v_method := lower(coalesce(substring(v_body from '(?i)^using\s+(\w+)'), 'btree'));
  IF EXISTS (
    SELECT 1
    FROM pg_index i
    JOIN pg_class ic ON ic.oid = i.indexrelid
    JOIN pg_am am ON am.oid = ic.relam
    WHERE i.indrelid = v_table AND i.indisvalid AND am.amname = v_method
  ) THEN
    BEGIN
      -- The candidate on an empty copy of the table, so PostgreSQL renders its definition
      -- canonically; the copy is dropped before anything else runs.
      v_probe := 'index_probe_' || v_table::oid::text;
      EXECUTE format('CREATE TEMPORARY TABLE %I (LIKE %s)', v_probe, v_table);
      EXECUTE format('CREATE %sINDEX %I ON pg_temp.%I %s', v_unique, v_probe || '_ix', v_probe, v_body);

      SELECT CASE WHEN i.indisunique THEN 'UNIQUE ' ELSE '' END
               || substr(pg_get_indexdef(i.indexrelid), strpos(pg_get_indexdef(i.indexrelid), ' USING '))
        INTO v_shape
      FROM pg_index i
      WHERE i.indrelid = format('pg_temp.%I', v_probe)::regclass;

      EXECUTE format('DROP TABLE pg_temp.%I', v_probe);

      SELECT ic.relname INTO v_equivalent
      FROM pg_index i
      JOIN pg_class ic ON ic.oid = i.indexrelid
      WHERE i.indrelid = v_table
        AND i.indisvalid
        AND CASE WHEN i.indisunique THEN 'UNIQUE ' ELSE '' END
              || substr(pg_get_indexdef(i.indexrelid), strpos(pg_get_indexdef(i.indexrelid), ' USING ')) = v_shape
      ORDER BY ic.relname
      LIMIT 1;
    EXCEPTION WHEN insufficient_privilege THEN
      -- A role without the temporary-table privilege cannot probe. Create as before rather than
      -- fail the schema pass over a comparison.
      v_equivalent := NULL;
    END;
  END IF;

  IF v_equivalent IS NOT NULL THEN
    RAISE WARNING USING MESSAGE = format(
      'wh_ensure_index: %s was not created on %s, because %s has the same definition',
      v_name, v_table, v_equivalent);
    RETURN 'equivalent:' || v_equivalent;
  END IF;

  EXECUTE p_ddl;
  -- The table has no statistics for a new expression until it is analyzed, so it is queued for the
  -- ANALYZE the schema pass runs once it has committed (#1004). Committed with the index, so an index
  -- the pass rolls back is never queued.
  INSERT INTO __SCHEMA__.wh_index_statistics_pending (table_name)
  SELECT format('%I.%I', n.nspname, c.relname)
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.oid = v_table
  ON CONFLICT (table_name) DO NOTHING;
  RETURN 'created';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_ensure_index(TEXT) IS
  'Runs one CREATE [UNIQUE] INDEX IF NOT EXISTS statement unless the table already has a valid index with '
  'the same definition under any name, which it reports with a WARNING and returns as equivalent:<name>. '
  'Returns exists, created, equivalent:<name>, or executed for a statement it does not parse. Never drops. '
  'A table it creates an index on is queued in wh_index_statistics_pending for ANALYZE.';

SELECT __SCHEMA__.drop_all_overloads('wh_tables_needing_index_statistics');

-- The perspective tables whose expression indexes lack statistics, and the queued tables, at most
-- p_limit of them, queued first. An expression index has statistics of its own, under the index's name
-- in pg_stats, once its table has been analyzed with the index in place. A table analyzed while empty
-- has none to gather and is not listed again until it has rows, so an empty table is not analyzed on
-- every cycle.
-- <docs>fundamentals/perspectives/perspective-indexes#statistics-for-a-new-index</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/IndexStatisticsMaintenanceStepTests.cs:AnExpressionIndexWithoutStatistics_IsAnalyzedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/IndexStatisticsMaintenanceStepTests.cs:ARun_AnalyzesNoMoreThanItsLimitAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_tables_needing_index_statistics(p_limit INTEGER)
RETURNS TABLE (table_name TEXT) AS $$
  SELECT candidate.table_name
  FROM (
    SELECT p.table_name, 0 AS priority
    FROM __SCHEMA__.wh_index_statistics_pending p
    WHERE to_regclass(p.table_name) IS NOT NULL
    UNION
    SELECT format('%I.%I', n.nspname, tc.relname), 1
    FROM pg_index i
    JOIN pg_class ic ON ic.oid = i.indexrelid
    JOIN pg_class tc ON tc.oid = i.indrelid
    JOIN pg_namespace n ON n.oid = tc.relnamespace
    WHERE n.nspname = replace('__SCHEMA__', '"', '')
      AND tc.relname LIKE 'wh\_per\_%'
      AND i.indexprs IS NOT NULL
      AND i.indisvalid
      AND tc.reltuples <> 0
      AND NOT EXISTS (
        SELECT 1 FROM pg_stats s WHERE s.schemaname = n.nspname AND s.tablename = ic.relname)
  ) candidate
  GROUP BY candidate.table_name
  ORDER BY min(candidate.priority), candidate.table_name
  LIMIT greatest(p_limit, 0);
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_tables_needing_index_statistics(INTEGER) IS
  'At most p_limit tables to ANALYZE: those queued in wh_index_statistics_pending, then perspective tables '
  'with rows (or never analyzed) whose expression indexes have no statistics in pg_stats.';
