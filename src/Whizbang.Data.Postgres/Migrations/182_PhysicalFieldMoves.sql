-- Migration: 182_PhysicalFieldMoves
-- Date: 2026-10-01
-- Description: Moving a perspective field's storage keeps its data, during the deploy and after it
--   (#1021, #1022, #1010).
--
--   Migration 179 filled a newly promoted scalar column of an Extracted model. This extends the same
--   ledger, wh_physical_column_fills, to every move a field's storage can make:
--   - Promotion of any kind of column the document can fill (scalars, enumerations, arrays, jsonb,
--     a type the author chose) on an Extracted or a Split model: armed as direction 'to_column'.
--   - A column the framework created for a promoted field, recorded with its field: direction 'recorded',
--     written by every schema pass and kept, and seeded from the perspective registry for columns promoted
--     before this migration. Only a recorded column is ever demoted, so a column an operator added under a
--     field's name is never copied over the document.
--   - Demotion (a field that is no longer physical, its recorded column left behind): the column's values
--     are copied into the document, direction 'to_document'. The column is never dropped; the row stays,
--     settled, so a later start never copies the column over the document again.
--   - A column the document cannot fill, or a demoted column whose type the document cannot hold:
--     direction 'rebuild', taken once by the maintenance step, which logs that the perspective needs a
--     rebuild and names the command.
--
--   A move where the two releases of a rolling deploy read the field from different places (a Split
--   promotion: the new release reads the column, the old one the document; and every demotion, the
--   reverse) is armed with sync_writes. Its table then carries, until the settle window has passed, two
--   row triggers that keep the column and the document in agreement on every write, whichever release
--   made it: a write that sets the column copies it into the document, and a write that does not set it
--   has the column follow the document. Neither release can then read a value the other wrote in the
--   place it does not look, which a background fill alone could not prevent: a row one release writes
--   could be read and written back by the other before the fill reached it. The maintenance step drops
--   the triggers when it disarms or settles the move.
-- Dependencies: 000 (drop_all_overloads), 179 (wh_physical_column_fills, wh_fill_physical_columns)
-- Objects: wh_physical_column_fills, wh_physical_column_forms, _wh_physical_move_sync_name, _wh_drop_physical_move_sync, wh_sync_physical_moves, wh_arm_physical_column, wh_demote_physical_columns, wh_record_registered_physical_columns, wh_fill_physical_columns, wh_settle_physical_moves, wh_take_physical_rebuild_notices
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_physical_column_fills
  ADD COLUMN IF NOT EXISTS direction TEXT NOT NULL DEFAULT 'to_column',
  ADD COLUMN IF NOT EXISTS document_form TEXT,
  ADD COLUMN IF NOT EXISTS sync_writes BOOLEAN NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS settled_at TIMESTAMPTZ;

-- A rebuild notice has no extraction: it exists because there is none. A column recorded from the perspective
-- registry, before the schema pass first recorded it, has no field name yet.
ALTER TABLE __SCHEMA__.wh_physical_column_fills ALTER COLUMN extraction DROP NOT NULL;
ALTER TABLE __SCHEMA__.wh_physical_column_fills ALTER COLUMN json_key DROP NOT NULL;

COMMENT ON TABLE __SCHEMA__.wh_physical_column_fills IS
  'Moves of a perspective field between its document and a column. recorded: a column the framework created '
  'for a promoted field, kept so only such a column is ever demoted. to_column: a column added to '
  'a table that had rows, filled from the document (wh_fill_physical_columns); recorded once it settles. to_document: a column whose '
  'field is no longer physical, copied into the document; kept once settled so it is never copied again. '
  'rebuild: a column neither side can fill, taken once by the maintenance step, which logs it. sync_writes: '
  'the table carries triggers keeping the column and the document in agreement until the move settles.';

SELECT __SCHEMA__.drop_all_overloads('wh_physical_column_forms');

-- How a column's values are read out of the document (extraction, over data) and written into it
-- (document_form, over the column), derived from the column's type, or nulls for a type the document
-- cannot hold exactly. The document stores dates and times as microsecond counts (see migration 153), so
-- those are rebuilt and rendered by exact integer arithmetic. A domain is read as its base type and cast
-- back to the domain.
-- <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFormsTests.cs:EachSupportedType_RoundTripsThroughTheDocumentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFormsTests.cs:ATypeTheDocumentCannotHold_HasNoFormsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_physical_column_forms(
    p_table REGCLASS, p_column TEXT, p_json_key TEXT, OUT extraction TEXT, OUT document_form TEXT) AS $$
DECLARE
  v_type OID;
  v_typmod INTEGER;
  v_base OID;
  v_declared TEXT;
  v_element OID;
  v_key TEXT := quote_literal(p_json_key);
  v_column TEXT := quote_ident(p_column);
  v_text TEXT := format('(data ->> %s)', quote_literal(p_json_key));
  v_micros TEXT := format('(data ->> %s)::bigint * INTERVAL ''1 microsecond''', quote_literal(p_json_key));
  v_category "char";
BEGIN
  SELECT a.atttypid, a.atttypmod INTO v_type, v_typmod
  FROM pg_attribute a
  WHERE a.attrelid = p_table AND a.attname = p_column AND a.attnum > 0 AND NOT a.attisdropped;
  IF v_type IS NULL THEN
    RETURN;
  END IF;

  v_declared := format_type(v_type, v_typmod);
  v_base := v_type;
  WHILE (SELECT t.typtype FROM pg_type t WHERE t.oid = v_base) = 'd' LOOP
    v_base := (SELECT t.typbasetype FROM pg_type t WHERE t.oid = v_base);
  END LOOP;

  -- An array of scalars: element by element, in document order.
  SELECT t.typelem INTO v_element FROM pg_type t WHERE t.oid = v_base AND t.typcategory = 'A' AND t.typelem <> 0;
  IF v_element IS NOT NULL THEN
    SELECT t.typcategory INTO v_category FROM pg_type t WHERE t.oid = v_element;
    IF v_category IN ('S', 'B') OR v_element IN ('uuid'::regtype, 'int2'::regtype, 'int4'::regtype, 'int8'::regtype,
        'numeric'::regtype, 'float4'::regtype, 'float8'::regtype) THEN
      -- A JSON null, or no key, is a null array; a value that is not an array is an error, as a bad scalar is.
      extraction := format(
        'CASE WHEN jsonb_typeof(data -> %2$s) <> ''null'' THEN ARRAY(SELECT wh_e.v::%1$s FROM jsonb_array_elements_text(data -> %2$s) '
          || 'WITH ORDINALITY AS wh_e(v, n) ORDER BY wh_e.n)::%3$s END',
        format_type(v_element, NULL), v_key, v_declared);
      document_form := format('to_jsonb(%s::%s)', v_column, format_type(v_base, NULL));
    END IF;
    RETURN;
  END IF;

  SELECT t.typcategory INTO v_category FROM pg_type t WHERE t.oid = v_base;
  IF v_base IN ('jsonb'::regtype, 'json'::regtype) THEN
    -- A JSON null in the document is the null the writer stores in the column.
    extraction := format('NULLIF(data -> %s, ''null''::jsonb)::%s', v_key, v_declared);
    document_form := format('%s::jsonb', v_column);
  ELSIF v_category IN ('S', 'B') OR v_base IN ('uuid'::regtype, 'int2'::regtype, 'int4'::regtype, 'int8'::regtype,
      'numeric'::regtype, 'float4'::regtype, 'float8'::regtype) THEN
    extraction := format('%s::%s', v_text, v_declared);
    -- Rendered as its base type: a string-like type (citext, a domain over text) becomes a JSON string.
    document_form := format('to_jsonb(%s::%s)', v_column,
      CASE WHEN v_category = 'S' THEN 'text' ELSE format_type(v_base, NULL) END);
  ELSIF v_base = 'timestamptz'::regtype THEN
    extraction := format('(TIMESTAMPTZ ''epoch'' + %s)::%s', v_micros, v_declared);
    document_form := format('to_jsonb((EXTRACT(EPOCH FROM %s) * 1000000)::bigint)', v_column);
  ELSIF v_base = 'timestamp'::regtype THEN
    extraction := format('(TIMESTAMP ''epoch'' + %s)::%s', v_micros, v_declared);
    document_form := format('to_jsonb((EXTRACT(EPOCH FROM %s) * 1000000)::bigint)', v_column);
  ELSIF v_base = 'date'::regtype THEN
    extraction := format('(TIMESTAMP ''epoch'' + %s)::date::%s', v_micros, v_declared);
    document_form := format('to_jsonb((%s - DATE ''1970-01-01'')::bigint * 86400000000)', v_column);
  ELSIF v_base = 'time'::regtype THEN
    extraction := format('(TIME ''00:00'' + %s)::%s', v_micros, v_declared);
    document_form := format('to_jsonb((EXTRACT(EPOCH FROM %s) * 1000000)::bigint)', v_column);
  END IF;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_physical_column_forms(REGCLASS, TEXT, TEXT) IS
  'The expressions that read p_column''s value out of the document key p_json_key (extraction, over data) and '
  'write it into the document (document_form, over the column), from the column''s type; nulls when the '
  'document cannot hold that type exactly.';

SELECT __SCHEMA__.drop_all_overloads('_wh_physical_move_sync_name');

-- The name the sync triggers and their functions of one move share: short, and stable for the table's
-- name and the column, so the step that drops them finds them from the ledger row alone.
-- <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPromotionTests.cs:TheSyncTriggers_AreDroppedWhenTheMoveSettlesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_physical_move_sync_name(p_table TEXT, p_column TEXT)
RETURNS TEXT AS $$
  SELECT 'wh_mv_' || left(md5(p_table || ':' || p_column), 20);
$$ LANGUAGE sql IMMUTABLE;

SELECT __SCHEMA__.drop_all_overloads('_wh_drop_physical_move_sync');

-- Drops one move's sync triggers and their functions, if present. A table that is gone has no triggers left.
-- <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPromotionTests.cs:TheSyncTriggers_AreDroppedWhenTheMoveSettlesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:ASettledDemotion_DropsItsTriggersAndIsNeverCopiedAgainAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_drop_physical_move_sync(p_table TEXT, p_column TEXT)
RETURNS VOID AS $$
DECLARE
  v_name TEXT := __SCHEMA__._wh_physical_move_sync_name(p_table, p_column);
  v_table REGCLASS := to_regclass(p_table);
BEGIN
  IF v_table IS NOT NULL THEN
    EXECUTE format('DROP TRIGGER IF EXISTS %I ON %s', v_name || '_a', v_table);
    EXECUTE format('DROP TRIGGER IF EXISTS %I ON %s', v_name || '_b', v_table);
  END IF;
  EXECUTE format('DROP FUNCTION IF EXISTS __SCHEMA__.%I()', v_name || '_a');
  EXECUTE format('DROP FUNCTION IF EXISTS __SCHEMA__.%I()', v_name || '_b');
END;
$$ LANGUAGE plpgsql;

SELECT __SCHEMA__.drop_all_overloads('wh_sync_physical_moves');

-- Puts the sync triggers on a table for each of its moves armed with sync_writes that does not have them,
-- and returns how many it added. Trigger a fires on a write that sets the column (an UPDATE naming it, or an
-- INSERT giving it a value) and copies the column into the document. Trigger b fires on every write after
-- it, and has the column follow the document wherever the two disagree: after trigger a they agree, so b
-- changes only a write that left the column out. A document value b cannot convert leaves the column as it
-- was, rather than failing a write an older release is making. A move whose column type the document cannot
-- hold gets no triggers.
-- <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPromotionTests.cs:AWriteFromEitherRelease_KeepsTheColumnAndTheDocumentInAgreementAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:AWriteFromEitherRelease_KeepsTheColumnAndTheDocumentInAgreementAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_sync_physical_moves(p_table TEXT)
RETURNS INTEGER AS $$
DECLARE
  v_table REGCLASS := to_regclass(p_table);
  v_name TEXT;
  v_move RECORD;
  v_sync TEXT;
  v_type TEXT;
  v_forms RECORD;
  v_added INTEGER := 0;
BEGIN
  IF v_table IS NULL THEN
    RETURN 0;
  END IF;
  SELECT format('%I.%I', n.nspname, c.relname) INTO v_name
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = v_table;

  FOR v_move IN
    SELECT f.column_name, f.json_key, f.extraction, f.document_form
    FROM __SCHEMA__.wh_physical_column_fills f
    WHERE f.table_name = v_name AND f.sync_writes AND f.settled_at IS NULL
      AND f.direction IN ('to_column', 'to_document')
  LOOP
    SELECT format_type(a.atttypid, a.atttypmod) INTO v_type
    FROM pg_attribute a WHERE a.attrelid = v_table AND a.attname = v_move.column_name AND NOT a.attisdropped;
    IF v_type IS NULL THEN
      CONTINUE;
    END IF;

    IF v_move.document_form IS NULL THEN
      SELECT * INTO v_forms FROM __SCHEMA__.wh_physical_column_forms(v_table, v_move.column_name, v_move.json_key);
      v_move.document_form := v_forms.document_form;
      UPDATE __SCHEMA__.wh_physical_column_fills f SET document_form = v_forms.document_form
      WHERE f.table_name = v_name AND f.column_name = v_move.column_name;
    END IF;
    IF v_move.document_form IS NULL OR v_move.extraction IS NULL THEN
      CONTINUE;
    END IF;

    v_sync := __SCHEMA__._wh_physical_move_sync_name(v_name, v_move.column_name);
    IF EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = v_table AND t.tgname = v_sync || '_b') THEN
      CONTINUE;
    END IF;

    EXECUTE format($tpl$
      CREATE OR REPLACE FUNCTION __SCHEMA__.%1$I() RETURNS trigger AS $wh_sync$
      BEGIN
        IF TG_OP = 'INSERT' AND NEW.%2$I IS NULL THEN
          RETURN NEW;
        END IF;
        NEW.data := jsonb_set(NEW.data, ARRAY[%3$L],
          COALESCE((SELECT %4$s FROM (SELECT NEW.%2$I AS %2$I) wh_c), 'null'::jsonb), true);
        RETURN NEW;
      END
      $wh_sync$ LANGUAGE plpgsql
      $tpl$, v_sync || '_a', v_move.column_name, v_move.json_key, v_move.document_form);

    EXECUTE format($tpl$
      CREATE OR REPLACE FUNCTION __SCHEMA__.%1$I() RETURNS trigger AS $wh_sync$
      DECLARE
        v_synced %3$s;
      BEGIN
        BEGIN
          v_synced := (SELECT %4$s FROM (SELECT NEW.data AS data) wh_d);
        EXCEPTION WHEN data_exception THEN
          RETURN NEW;
        END;
        IF NEW.%2$I IS DISTINCT FROM v_synced THEN
          NEW.%2$I := v_synced;
        END IF;
        RETURN NEW;
      END
      $wh_sync$ LANGUAGE plpgsql
      $tpl$, v_sync || '_b', v_move.column_name, v_type, v_move.extraction);

    EXECUTE format('DROP TRIGGER IF EXISTS %I ON %s', v_sync || '_a', v_table);
    EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE OF %I ON %s FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.%I()',
      v_sync || '_a', v_move.column_name, v_table, v_sync || '_a');
    EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE ON %s FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.%I()',
      v_sync || '_b', v_table, v_sync || '_b');
    v_added := v_added + 1;
  END LOOP;
  RETURN v_added;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_sync_physical_moves(TEXT) IS
  'Adds the triggers that keep a moving field''s column and document in agreement on every write, for each '
  'unsettled move of p_table armed with sync_writes that lacks them. Returns how many moves it added them for.';

SELECT __SCHEMA__.drop_all_overloads('wh_arm_physical_column');

-- Arms a promoted field before the schema pass adds its column, so only the pass that adds it arms it
-- (the 179 rule). A column already there is recorded as the framework's, under its field. A table that is not there yet arms nothing. p_extraction null is a column the document
-- cannot fill: on a table with rows, a rebuild notice. p_sync arms the sync triggers, for a Split model,
-- whose new release reads the column while the old one reads the document. A column already there arms
-- nothing, unless its field was demoted and is now promoted again: the document has been the field's copy
-- since, so the column is refreshed from it, every row, and the move is armed as a promotion.
-- <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPromotionTests.cs:ASplitPromotion_FillsTheColumnFromTheDocumentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:PromotingADemotedFieldAgain_RefreshesTheColumnFromTheDocumentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldMoveNoticeTests.cs:AColumnTheDocumentCannotFill_IsReportedForARebuildAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_arm_physical_column(
    p_table TEXT, p_column TEXT, p_json_key TEXT, p_extraction TEXT, p_sync BOOLEAN)
RETURNS TEXT AS $$
DECLARE
  v_table REGCLASS := to_regclass(p_table);
  v_name TEXT;
  v_direction TEXT;
  v_has_rows BOOLEAN;
BEGIN
  IF v_table IS NULL THEN
    RETURN 'absent';
  END IF;
  SELECT format('%I.%I', n.nspname, c.relname) INTO v_name
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = v_table;
  SELECT f.direction INTO v_direction FROM __SCHEMA__.wh_physical_column_fills f
  WHERE f.table_name = v_name AND f.column_name = p_column;

  IF EXISTS (SELECT 1 FROM pg_attribute a
             WHERE a.attrelid = v_table AND a.attname = p_column AND NOT a.attisdropped) THEN
    IF v_direction IS DISTINCT FROM 'to_document' THEN
      -- A column the framework created for this field: recorded, so a later demotion knows the column is the
      -- framework's and which field it holds. A move in progress is left as it is.
      INSERT INTO __SCHEMA__.wh_physical_column_fills AS f
        (table_name, column_name, json_key, extraction, direction, document_form, sync_writes, armed_at, settled_at)
      VALUES (v_name, p_column, p_json_key, p_extraction, 'recorded', NULL, false, now(), now())
      ON CONFLICT (table_name, column_name) DO UPDATE SET json_key = EXCLUDED.json_key, extraction = EXCLUDED.extraction
        WHERE f.direction = 'recorded'
          AND (f.json_key IS DISTINCT FROM EXCLUDED.json_key OR f.extraction IS DISTINCT FROM EXCLUDED.extraction);
      RETURN 'present';
    END IF;
    PERFORM __SCHEMA__._wh_drop_physical_move_sync(v_name, p_column);
    IF p_extraction IS NULL THEN
      UPDATE __SCHEMA__.wh_physical_column_fills f
      SET direction = 'rebuild', json_key = p_json_key, extraction = NULL, document_form = NULL,
          sync_writes = false, armed_at = now(), settled_at = NULL
      WHERE f.table_name = v_name AND f.column_name = p_column;
      RETURN 'rebuild';
    END IF;
    EXECUTE format('UPDATE %s SET %I = %s WHERE %I IS DISTINCT FROM (%s)',
      v_table, p_column, p_extraction, p_column, p_extraction);
    UPDATE __SCHEMA__.wh_physical_column_fills f
    SET direction = 'to_column', json_key = p_json_key, extraction = p_extraction, document_form = NULL,
        sync_writes = p_sync, armed_at = now(), settled_at = NULL
    WHERE f.table_name = v_name AND f.column_name = p_column;
    RETURN 'refreshed';
  END IF;

  IF p_extraction IS NULL THEN
    EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s)', v_table) INTO v_has_rows;
    IF NOT v_has_rows THEN
      RETURN 'empty';
    END IF;
  END IF;

  INSERT INTO __SCHEMA__.wh_physical_column_fills AS f
    (table_name, column_name, json_key, extraction, direction, document_form, sync_writes, armed_at, settled_at)
  VALUES (v_name, p_column, p_json_key, p_extraction,
    CASE WHEN p_extraction IS NULL THEN 'rebuild' ELSE 'to_column' END, NULL,
    p_extraction IS NOT NULL AND p_sync, now(), NULL)
  ON CONFLICT (table_name, column_name) DO UPDATE SET
    json_key = EXCLUDED.json_key, extraction = EXCLUDED.extraction, direction = EXCLUDED.direction,
    document_form = NULL, sync_writes = EXCLUDED.sync_writes, armed_at = now(), settled_at = NULL;
  RETURN CASE WHEN p_extraction IS NULL THEN 'rebuild' ELSE 'armed' END;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_arm_physical_column(TEXT, TEXT, TEXT, TEXT, BOOLEAN) IS
  'Arms a promoted column before the schema pass adds it: absent (no table), present (already there; recorded), armed, '
  'empty (nothing to fill and no rows), rebuild (no document copy to fill from, on a table with rows), or '
  'refreshed (a demoted field promoted again: the column was refreshed from the document).';

SELECT __SCHEMA__.drop_all_overloads('wh_demote_physical_columns');

-- Moves the values of each recorded column whose field is no longer physical into the document. p_candidates
-- maps a column name to the document key of a field the model now keeps only in the document, under the name
-- its column would have had by default; p_owned lists the columns the model's promoted fields own now. A
-- column is demoted only when the ledger records the framework created it for a promoted field: one recorded
-- with its field is matched by that field, whatever its column is named; one seeded from the perspective
-- registry, with no field, is matched by the default name. A column an operator added, however it is named, is
-- never touched. So is a column already moved, which is how a later start never copies a stale column over the
-- document, and a column that is not there, or one the framework never creates for a field (NOT NULL,
-- generated). Otherwise the move is armed with sync_writes, its triggers are added BEFORE the copy (a write
-- that lands after the copy is synced, one before it is copied), and every row whose column holds a value the
-- document does not is copied. A column the document cannot hold is a rebuild notice. Returns one row per
-- recorded column of a candidate field.
-- <docs>fundamentals/perspectives/physical-fields#demoting-a-field</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:ADemotedField_IsCopiedIntoTheDocumentForEveryRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:ASecondPass_CopiesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:AColumnTheFrameworkDidNotRecord_IsNeverCopiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldMoveNoticeTests.cs:ADemotedColumnTheDocumentCannotHold_IsReportedForARebuildAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_demote_physical_columns(
    p_table TEXT, p_candidates JSONB, p_owned TEXT[] DEFAULT ARRAY[]::TEXT[])
RETURNS TABLE (demoted_column TEXT, outcome TEXT, copied BIGINT) AS $$
DECLARE
  v_table REGCLASS := to_regclass(p_table);
  v_name TEXT;
  v_candidate RECORD;
  v_forms RECORD;
BEGIN
  IF v_table IS NULL OR p_candidates IS NULL OR jsonb_typeof(p_candidates) <> 'object' THEN
    RETURN;
  END IF;
  SELECT format('%I.%I', n.nspname, c.relname) INTO v_name
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = v_table;

  FOR v_candidate IN
    SELECT f.column_name, COALESCE(f.json_key, d.value) AS json_key, f.direction
    FROM __SCHEMA__.wh_physical_column_fills f
    LEFT JOIN jsonb_each_text(p_candidates) d ON d.key = f.column_name
    JOIN pg_attribute a ON a.attrelid = v_table AND a.attname = f.column_name
    WHERE f.table_name = v_name
      AND f.direction IN ('recorded', 'to_column', 'to_document')
      AND NOT (f.column_name = ANY(COALESCE(p_owned, ARRAY[]::TEXT[])))
      AND ((f.json_key IS NOT NULL AND f.json_key IN (SELECT e.value FROM jsonb_each_text(p_candidates) e))
        OR (f.json_key IS NULL AND d.key IS NOT NULL))
      AND a.attnum > 0 AND NOT a.attisdropped AND NOT a.attnotnull AND a.attgenerated = ''
    ORDER BY f.column_name
  LOOP
    demoted_column := v_candidate.column_name;
    copied := 0;
    IF v_candidate.direction = 'to_document' THEN
      outcome := 'moved earlier';
      RETURN NEXT;
      CONTINUE;
    END IF;
    IF v_candidate.direction = 'to_column' THEN
      PERFORM __SCHEMA__._wh_drop_physical_move_sync(v_name, v_candidate.column_name);
    END IF;

    SELECT * INTO v_forms
    FROM __SCHEMA__.wh_physical_column_forms(v_table, v_candidate.column_name, v_candidate.json_key);
    IF v_forms.document_form IS NULL OR v_forms.extraction IS NULL THEN
      UPDATE __SCHEMA__.wh_physical_column_fills f
      SET json_key = v_candidate.json_key, extraction = NULL, direction = 'rebuild', document_form = NULL,
          sync_writes = false, armed_at = now(), settled_at = NULL
      WHERE f.table_name = v_name AND f.column_name = v_candidate.column_name;
      outcome := 'rebuild';
      RETURN NEXT;
      CONTINUE;
    END IF;

    UPDATE __SCHEMA__.wh_physical_column_fills f
    SET json_key = v_candidate.json_key, extraction = v_forms.extraction, direction = 'to_document',
        document_form = v_forms.document_form, sync_writes = true, armed_at = now(), settled_at = NULL
    WHERE f.table_name = v_name AND f.column_name = v_candidate.column_name;
    PERFORM __SCHEMA__.wh_sync_physical_moves(v_name);

    EXECUTE format(
      'UPDATE %1$s SET data = jsonb_set(data, ARRAY[%2$L], %3$s, true) '
        || 'WHERE %4$I IS NOT NULL AND data -> %2$L IS DISTINCT FROM %3$s',
      v_table, v_candidate.json_key, v_forms.document_form, v_candidate.column_name);
    GET DIAGNOSTICS copied = ROW_COUNT;
    outcome := 'moved';
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_demote_physical_columns(TEXT, JSONB, TEXT[]) IS
  'Copies into the document, once, the values of each column the ledger records the framework created for a '
  'promoted field that the model now keeps only in the document (p_candidates: default column name to document '
  'key; p_owned: columns still promoted), and arms the sync triggers for the rolling deploy. Never touches an '
  'unrecorded column, and never drops a column.';

SELECT __SCHEMA__.drop_all_overloads('wh_record_registered_physical_columns');

-- Records, as the framework's, every promoted column the perspective registry lists for a table: the columns
-- earlier releases promoted, before the schema pass recorded them itself. They are recorded with no field;
-- the next schema pass that still promotes one names its field, and a demotion matches one by its default name.
-- A registry entry whose table is gone, a column the table no longer has, and a column every perspective table
-- has are skipped. Returns how many columns it recorded.
-- <docs>fundamentals/perspectives/physical-fields#demoting-a-field</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:AColumnTheRegistryLists_IsRecordedAndDemotedByItsNameAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_record_registered_physical_columns()
RETURNS INTEGER AS $$
DECLARE
  v_recorded INTEGER := 0;
BEGIN
  IF to_regclass('__SCHEMA__.wh_perspective_registry') IS NULL THEN
    RETURN 0;
  END IF;
  INSERT INTO __SCHEMA__.wh_physical_column_fills AS f
    (table_name, column_name, json_key, extraction, direction, document_form, sync_writes, armed_at, settled_at)
  SELECT DISTINCT format('%I.%I', n.nspname, c.relname), col ->> 'name', NULL::text, NULL::text, 'recorded',
    NULL::text, false, now(), now()
  FROM __SCHEMA__.wh_perspective_registry r
  CROSS JOIN LATERAL jsonb_array_elements(
    CASE WHEN jsonb_typeof(r.schema_json -> 'columns') = 'array' THEN r.schema_json -> 'columns' ELSE '[]'::jsonb END) col
  JOIN pg_class c ON c.oid = to_regclass('__SCHEMA__.' || quote_ident(r.table_name))
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = col ->> 'name' AND a.attnum > 0 AND NOT a.attisdropped
  WHERE col ->> 'name' NOT IN ('id', 'data', 'model_data', 'metadata', 'scope', 'created_at', 'updated_at',
                               'sys_created_at', 'sys_updated_at', 'expires_at', 'version')
  ON CONFLICT (table_name, column_name) DO NOTHING;
  GET DIAGNOSTICS v_recorded = ROW_COUNT;
  RETURN v_recorded;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_record_registered_physical_columns() IS
  'Records as the framework''s every promoted column the perspective registry lists, for the columns earlier '
  'releases promoted before the schema pass recorded them. Returns how many it recorded.';

-- The columns earlier releases promoted. Idempotent: a recorded column is left as it is.
SELECT __SCHEMA__.wh_record_registered_physical_columns();

SELECT __SCHEMA__.drop_all_overloads('wh_fill_physical_columns');

-- One bounded batch per armed promotion: fills up to p_batch rows that have the value in the document and
-- none in the column, never overwriting a column a writer has filled. A column whose batch came up short
-- has nothing left right now; once it has been armed longer than p_settle it is disarmed (kept as recorded),
-- and its sync triggers, if it had them, are dropped. A column whose table or column is gone is disarmed. A batch that
-- fails is reported and leaves the column armed. Demotions and rebuild notices are not fills: see
-- wh_settle_physical_moves and wh_take_physical_rebuild_notices.
-- <docs>fundamentals/perspectives/physical-fields#rows-written-during-a-rolling-deploy</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs:ARowWrittenWithOnlyTheDocumentValue_IsFilledAndFoundByAColumnFilterAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs:AColumnStaysArmedUntilTheSettleWindowHasPassedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs:ABatchFillsNoMoreThanItsSizeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPromotionTests.cs:TheSyncTriggers_AreDroppedWhenTheMoveSettlesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_fill_physical_columns(p_batch INTEGER, p_settle INTERVAL)
RETURNS TABLE (fill_table TEXT, fill_column TEXT, filled BIGINT, disarmed BOOLEAN) AS $$
DECLARE
  v_fill RECORD;
  v_table REGCLASS;
  v_count BIGINT;
BEGIN
  FOR v_fill IN
    SELECT f.table_name, f.column_name, f.json_key, f.extraction, f.armed_at, f.sync_writes
    FROM __SCHEMA__.wh_physical_column_fills f
    WHERE f.direction = 'to_column' AND f.settled_at IS NULL AND f.extraction IS NOT NULL
    ORDER BY f.armed_at, f.table_name, f.column_name
  LOOP
    fill_table := v_fill.table_name;
    fill_column := v_fill.column_name;
    v_table := to_regclass(v_fill.table_name);

    IF v_table IS NULL OR NOT EXISTS (
      SELECT 1 FROM pg_attribute a
      WHERE a.attrelid = v_table AND a.attname = v_fill.column_name AND NOT a.attisdropped
    ) THEN
      PERFORM __SCHEMA__._wh_drop_physical_move_sync(v_fill.table_name, v_fill.column_name);
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
      IF v_fill.sync_writes THEN
        PERFORM __SCHEMA__._wh_drop_physical_move_sync(v_fill.table_name, v_fill.column_name);
      END IF;
      -- Kept, as the record that the framework created this column for the field.
      UPDATE __SCHEMA__.wh_physical_column_fills f
      SET direction = 'recorded', sync_writes = false, document_form = NULL, settled_at = now()
      WHERE f.table_name = v_fill.table_name AND f.column_name = v_fill.column_name
        AND f.armed_at = v_fill.armed_at;
    END IF;
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_fill_physical_columns(INTEGER, INTERVAL) IS
  'For each promotion armed in wh_physical_column_fills, fills at most p_batch rows whose value is in the '
  'document and not the column. Disarms a column whose batch came up short once armed longer than p_settle '
  '(dropping its sync triggers), and one whose table or column is gone. Returns one row per armed column.';

SELECT __SCHEMA__.drop_all_overloads('wh_settle_physical_moves');

-- Settles each demotion armed longer than p_settle, or whose table or column is gone: its sync triggers are
-- dropped and the row is kept with settled_at, so the column is never copied into the document again. The
-- column itself is left for an operator to drop. Returns one row per demotion still unsettled or settled now.
-- <docs>fundamentals/perspectives/physical-fields#demoting-a-field</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs:ASettledDemotion_DropsItsTriggersAndIsNeverCopiedAgainAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_settle_physical_moves(p_settle INTERVAL)
RETURNS TABLE (move_table TEXT, move_column TEXT, settled BOOLEAN) AS $$
DECLARE
  v_move RECORD;
  v_table REGCLASS;
BEGIN
  FOR v_move IN
    SELECT f.table_name, f.column_name, f.armed_at
    FROM __SCHEMA__.wh_physical_column_fills f
    WHERE f.direction = 'to_document' AND f.settled_at IS NULL
    ORDER BY f.armed_at, f.table_name, f.column_name
  LOOP
    move_table := v_move.table_name;
    move_column := v_move.column_name;
    v_table := to_regclass(v_move.table_name);
    settled := v_move.armed_at < now() - p_settle
      OR v_table IS NULL
      OR NOT EXISTS (SELECT 1 FROM pg_attribute a
                     WHERE a.attrelid = v_table AND a.attname = v_move.column_name AND NOT a.attisdropped);
    IF settled THEN
      PERFORM __SCHEMA__._wh_drop_physical_move_sync(v_move.table_name, v_move.column_name);
      UPDATE __SCHEMA__.wh_physical_column_fills f SET settled_at = now()
      WHERE f.table_name = v_move.table_name AND f.column_name = v_move.column_name;
    END IF;
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_settle_physical_moves(INTERVAL) IS
  'Drops the sync triggers of each demotion armed longer than p_settle (or whose column is gone) and marks it '
  'settled, keeping the row so the column is never copied again. Never drops the column.';

SELECT __SCHEMA__.drop_all_overloads('wh_take_physical_rebuild_notices');

-- Takes the rebuild notices: columns the document cannot fill, or demoted columns the document cannot hold.
-- Each is returned once and removed, so one maintenance run logs it.
-- <docs>fundamentals/perspectives/physical-fields#when-the-document-has-no-copy</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldMoveNoticeTests.cs:AColumnTheDocumentCannotFill_IsReportedForARebuildAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_take_physical_rebuild_notices()
RETURNS TABLE (notice_table TEXT, notice_column TEXT, notice_key TEXT) AS $$
  DELETE FROM __SCHEMA__.wh_physical_column_fills f
  WHERE f.direction = 'rebuild'
  RETURNING f.table_name, f.column_name, f.json_key;
$$ LANGUAGE sql;

COMMENT ON FUNCTION __SCHEMA__.wh_take_physical_rebuild_notices() IS
  'Removes and returns the rebuild notices: columns the document cannot fill, and demoted columns the '
  'document cannot hold, whose perspective needs a rebuild.';
