-- Migration: 201_IndexEquivalents
-- Date: 2026-10-09
-- Description: Record which existing index stands for a declared one, so the managed-object reconcile does
--   not drop it (#1252).
--
--   wh_ensure_index (174, 178) does not build a declared index when the table already has one with the same
--   definition under another name: the twin it would build is waste. But the managed-object reconcile matches
--   by name, so that existing index read as one Whizbang built and no longer declares, and was dropped; the
--   declared name, never having existed, was then not rebuilt either, and the table lost the index.
--
--   wh_index_equivalents records each such pair as wh_ensure_index finds it, and the reconcile reads the
--   existing name as declared and the declared name as present. A pair is removed once the declared name
--   itself exists. wh_ensure_index is 178's function, copied, plus those two statements.
--
--   A database that already skipped a twin under an earlier release has no record of it, so this migration
--   forgets the perspective schema hashes once: the start that applies it runs every perspective's schema SQL
--   again, which only records the pairs (every statement in it is idempotent), and later starts take the fast
--   path as before.
-- Dependencies: 000 (drop_all_overloads), 174 and 178 (wh_ensure_index, wh_index_statistics_pending)
-- Objects: wh_index_equivalents, wh_ensure_index
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>fundamentals/perspectives/managed-schema-objects</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/IndexEquivalentsTests.cs</tests>
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_index_equivalents (
  table_name    TEXT NOT NULL,
  declared_name TEXT NOT NULL,
  existing_name TEXT NOT NULL,
  recorded_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (table_name, declared_name)
);

COMMENT ON TABLE __SCHEMA__.wh_index_equivalents IS
  'Declared indexes wh_ensure_index did not build because an index with the same definition exists under '
  'another name, and that name. The managed-object reconcile reads the existing index as the declared one.';

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_ensure_index');

-- <docs>fundamentals/perspectives/perspective-indexes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/EnsureIndexFunctionTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/IndexEquivalentsTests.cs</tests>
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
    -- The declared name exists, so no other index stands for it any more.
    DELETE FROM __SCHEMA__.wh_index_equivalents e
    USING pg_class c WHERE c.oid = v_table AND e.table_name = c.relname AND e.declared_name = v_name;
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
    -- Recorded, so the managed-object reconcile reads the existing index as the declared one: it keeps it,
    -- and does not report the declared name missing (#1252).
    INSERT INTO __SCHEMA__.wh_index_equivalents (table_name, declared_name, existing_name)
    SELECT c.relname, v_name, v_equivalent FROM pg_class c WHERE c.oid = v_table
    ON CONFLICT (table_name, declared_name) DO UPDATE SET existing_name = EXCLUDED.existing_name, recorded_at = NOW();
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
  'the same definition under any name, which it reports with a WARNING, records in wh_index_equivalents and '
  'returns as equivalent:<name>. Returns exists, created, equivalent:<name>, or executed for a statement it '
  'does not parse. Never drops. A table it creates an index on is queued in wh_index_statistics_pending for ANALYZE.';

-- Once: every perspective's schema SQL runs again at the start that applies this migration, so a twin an
-- earlier release skipped is recorded above. Idempotent statements only; later starts take the fast path.
DO $$
BEGIN
  IF to_regclass('__SCHEMA__.wh_schema_migrations') IS NOT NULL THEN
    DELETE FROM __SCHEMA__.wh_schema_migrations WHERE file_name LIKE 'perspective:%';
  END IF;
END $$;
