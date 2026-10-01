-- Migration: 174_EnsureIndex
-- Date: 2026-09-28
-- Description: Create a declared index only when its table has no index with the same definition,
--   whatever that index is called (#975).
--
--   The perspective schema pass created every index with IF NOT EXISTS, which compares names and
--   nothing else. An index an earlier path had built under its own name, with the same definition
--   as one the schema declares, was therefore joined by a twin: one perspective table carried two
--   btree indexes over (scope ->> 't') and further pairs over document extractions, and every write
--   maintained both. The schema pass now hands each index to this function instead.
--
--   The definition is compared the way PostgreSQL itself renders it, not as the statement was
--   written: the candidate is built on an empty temporary copy of the table and read back with
--   pg_get_indexdef, which is canonical in spacing, casts and parentheses, and that rendering (less
--   the name and the table) is compared with each existing valid index on the table. Uniqueness, the
--   access method, operator classes, collations and a partial predicate are all part of it.
--
--   Nothing is ever dropped or renamed. An equivalent under another name is left in place and the
--   declared index is not created, with a WARNING naming both; the operator decides which name to
--   keep. The documented clean-up is operations SQL, never an upgrade step.
--
--   Deliberately best-effort in one direction only. A statement this cannot parse runs as given,
--   and a role that may not create temporary tables skips the comparison and creates the index, so
--   neither can turn a duplicate-avoidance step into a failed schema pass.
-- Dependencies: 000 (drop_all_overloads)
-- Objects: wh_ensure_index
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_ensure_index');

-- <docs>fundamentals/perspectives/perspective-indexes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/EnsureIndexFunctionTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/DocumentIndexInitializationTests.cs</tests>
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
  RETURN 'created';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_ensure_index(TEXT) IS
  'Runs one CREATE [UNIQUE] INDEX IF NOT EXISTS statement unless the table already has a valid index with '
  'the same definition under any name, which it reports with a WARNING and returns as equivalent:<name>. '
  'Returns exists, created, equivalent:<name>, or executed for a statement it does not parse. Never drops.';
