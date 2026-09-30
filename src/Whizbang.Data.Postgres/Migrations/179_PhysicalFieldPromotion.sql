-- Migration: 179_PhysicalFieldPromotion
-- Date: 2026-09-30
-- Description: Promoting a document field to a physical column moves its indexes and catches the rows
--   older instances write during a rolling deploy (#1009).
--
--   Promotion redirects the field's queries to the column. Two things were left behind:
--   - The indexes the schema had built over the field's extraction from the document. No query reads
--     them again, every write still maintains them, and the column had no index of the same kind. The
--     schema pass now drops them through wh_drop_document_index and builds the column's indexes.
--   - Rows an older instance writes after the promoting start filled the column. That instance does not
--     know the column, so the row has the value in the document and null in the column, and a filter,
--     sort or count on the column misses or misplaces it until the row's next event. The schema pass
--     arms the field in wh_physical_column_fills when it adds the column, and the physical-column fill
--     maintenance step fills such rows through wh_fill_physical_columns, a bounded batch at a time, until
--     a run finds none left once the settle window has passed.
--
--   The alternative, reading COALESCE(column, the extraction) for a promoted field, is correct but is a
--   different expression from the column, so a filter on it cannot use the column's index; the column
--   then pays for itself nowhere. Filling the column keeps every query on the plain column.
-- Dependencies: 000 (drop_all_overloads)
-- Objects: wh_physical_column_fills, wh_drop_document_index, wh_fill_physical_columns
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- One row per promoted column still being watched. The table is fully qualified as format('%I.%I')
-- renders it; the extraction is the expression the schema pass backfilled the column with, written by
-- the generated schema from the same function that wrote that backfill.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_physical_column_fills (
  table_name TEXT NOT NULL,
  column_name TEXT NOT NULL,
  json_key TEXT NOT NULL,
  extraction TEXT NOT NULL,
  armed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (table_name, column_name)
);

COMMENT ON TABLE __SCHEMA__.wh_physical_column_fills IS
  'Physical columns added to tables that already had rows, watched for rows written with the value only '
  'in the document. Armed by the schema pass when it adds the column; drained by wh_fill_physical_columns.';

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_drop_document_index');

-- Drops one index the schema built over a promoted field's extraction from the document, identified by
-- the name the schema gave it AND by a definition over that extraction. An index under any other name is
-- never touched, whatever it covers, so an index an operator built stays. So does an index under the
-- name that is not over the document, which is how a column index that took the same name is kept.
-- <docs>fundamentals/perspectives/physical-fields#promoting-an-existing-field</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldPromotionTests.cs:APromotedField_LosesItsDocumentIndexesAndGainsColumnIndexesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldPromotionTests.cs:AnIndexTheSchemaDidNotBuild_IsKeptAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_drop_document_index(p_table TEXT, p_index TEXT, p_json_key TEXT)
RETURNS TEXT AS $$
DECLARE
  v_table REGCLASS;
  v_index OID;
  v_definition TEXT;
BEGIN
  v_table := to_regclass(p_table);
  IF v_table IS NULL THEN
    RETURN 'absent';
  END IF;

  -- The name as PostgreSQL stored it: an identifier longer than 63 bytes was truncated when created.
  SELECT i.indexrelid INTO v_index
  FROM pg_index i
  JOIN pg_class ic ON ic.oid = i.indexrelid
  WHERE i.indrelid = v_table
    AND ic.relname = left(p_index, 63)
    AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid);
  IF v_index IS NULL THEN
    RETURN 'absent';
  END IF;

  v_definition := pg_get_indexdef(v_index);
  IF strpos(v_definition, '(data ->> ' || quote_literal(p_json_key) || '::text)') = 0 THEN
    RETURN 'kept';
  END IF;

  EXECUTE format('DROP INDEX %s', v_index::regclass);
  RETURN 'dropped';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_drop_document_index(TEXT, TEXT, TEXT) IS
  'Drops the index named p_index on p_table when its definition extracts p_json_key from the document. '
  'Returns dropped, kept (the name is over something else), or absent.';

SELECT __SCHEMA__.drop_all_overloads('wh_fill_physical_columns');

-- One bounded batch per armed column: fills up to p_batch rows that have the value in the document and
-- none in the column, never overwriting a column a writer has filled. A column whose batch came up short
-- has nothing left right now; once it has been armed longer than p_settle it is disarmed. A column whose
-- table or column is gone is disarmed. A batch that fails is reported and leaves the column armed.
-- <docs>fundamentals/perspectives/physical-fields#rows-written-during-a-rolling-deploy</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs:ARowWrittenWithOnlyTheDocumentValue_IsFilledAndFoundByAColumnFilterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs:AColumnStaysArmedUntilTheSettleWindowHasPassedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs:ABatchFillsNoMoreThanItsSizeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_fill_physical_columns(p_batch INTEGER, p_settle INTERVAL)
RETURNS TABLE (fill_table TEXT, fill_column TEXT, filled BIGINT, disarmed BOOLEAN) AS $$
DECLARE
  v_fill RECORD;
  v_table REGCLASS;
  v_count BIGINT;
BEGIN
  FOR v_fill IN
    SELECT f.table_name, f.column_name, f.json_key, f.extraction, f.armed_at
    FROM __SCHEMA__.wh_physical_column_fills f
    ORDER BY f.armed_at, f.table_name, f.column_name
  LOOP
    fill_table := v_fill.table_name;
    fill_column := v_fill.column_name;
    v_table := to_regclass(v_fill.table_name);

    IF v_table IS NULL OR NOT EXISTS (
      SELECT 1 FROM pg_attribute a
      WHERE a.attrelid = v_table AND a.attname = v_fill.column_name AND NOT a.attisdropped
    ) THEN
      DELETE FROM __SCHEMA__.wh_physical_column_fills f
      WHERE f.table_name = v_fill.table_name AND f.column_name = v_fill.column_name;
      filled := 0;
      disarmed := TRUE;
      RETURN NEXT;
      CONTINUE;
    END IF;

    BEGIN
      -- The batch is chosen once, in a materialized CTE: a LIMIT inside an IN subquery can be rescanned
      -- by the join and pick further rows. The outer IS NULL re-checks each row, so a column a writer
      -- filled in between is left as the writer wrote it. SKIP LOCKED leaves a row a writer holds to the
      -- next batch.
      EXECUTE format(
        'WITH batch AS MATERIALIZED ('
          || 'SELECT id FROM %1$s WHERE %2$I IS NULL AND jsonb_typeof(data -> %4$L) <> %5$L '
          || 'LIMIT %6$s FOR UPDATE SKIP LOCKED) '
          || 'UPDATE %1$s t SET %2$I = %3$s FROM batch WHERE t.id = batch.id AND t.%2$I IS NULL',
        v_table, v_fill.column_name, v_fill.extraction, v_fill.json_key, 'null', greatest(p_batch, 0));
      GET DIAGNOSTICS v_count = ROW_COUNT;
    EXCEPTION WHEN data_exception THEN
      RAISE WARNING USING MESSAGE = format(
        'wh_fill_physical_columns: could not fill %s.%s from the document: %s',
        v_fill.table_name, v_fill.column_name, SQLERRM);
      filled := NULL;
      disarmed := FALSE;
      RETURN NEXT;
      CONTINUE;
    END;

    filled := v_count;
    disarmed := v_count < p_batch AND v_fill.armed_at < now() - p_settle;
    IF disarmed THEN
      DELETE FROM __SCHEMA__.wh_physical_column_fills f
      WHERE f.table_name = v_fill.table_name AND f.column_name = v_fill.column_name
        AND f.armed_at = v_fill.armed_at;
    END IF;
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_fill_physical_columns(INTEGER, INTERVAL) IS
  'For each column armed in wh_physical_column_fills, fills at most p_batch rows whose value is in the '
  'document and not the column. Disarms a column whose batch came up short once armed longer than '
  'p_settle, and one whose table or column is gone. Returns one row per armed column.';
