-- Migration: 191_ConsumerTypeNameHelpers.sql
-- Date: 2026-10-04
-- Description: Help for type names inside a consumer's own data (#1035). Migration 063 normalized the
--              columns Whizbang owns; a consumer's type names live inside its own documents, under field
--              names Whizbang has never seen, and nothing found or fixed them.
--
--              wh_find_qualified_type_names() reports every table and column in this service's schema that
--              holds a versioned assembly-qualified name, with how many spellings it holds per logical type:
--              more spellings than types is the defect, which a raw count would not show.
--
--              wh_normalize_snapshot_type_names_batch() strips the version decoration from the names inside
--              wh_perspective_snapshots. Snapshots are a Whizbang table holding consumer-shaped documents,
--              and a consumer that normalizes its own perspectives but not its snapshots gets the decorated
--              form back on the next restore. Only a complete decoration is stripped (Version, Culture and
--              PublicKeyToken in that order), so prose that mentions a version is left alone. The pass is
--              gated by the wh_settings row 'perspective_snapshot_type_name_version' (1 = swept) and runs in
--              bounded slices the runner repeats.
-- Dependencies: 006 (wh_normalize_clr_type_name), 105 (wh_settings in the service schema)
-- Objects: wh_find_qualified_type_names, wh_normalize_snapshot_type_names_batch, _wh_strip_type_name_decoration

-- The decoration a versioned assembly-qualified name carries, and nothing looser.
-- <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SnapshotSweep_NormalizesQualifiedNamesInsideSnapshotsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_strip_type_name_decoration(p_text TEXT)
RETURNS TEXT AS $$
  SELECT regexp_replace(
    p_text,
    ', *Version=[0-9]+(\.[0-9]+){1,3}, *Culture=[A-Za-z-]+, *PublicKeyToken=([0-9a-fA-F]{16}|null)',
    '', 'g');
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_strip_type_name_decoration IS
'Strips every complete Version/Culture/PublicKeyToken decoration from text that holds assembly-qualified names, such as a JSON document. Internal to the snapshot sweep.';

-- <docs>operations/infrastructure/migrations#finding-qualified-type-names</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:FindQualifiedTypeNames_ReportsSpellingsPerLogicalTypeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_find_qualified_type_names()
RETURNS TABLE (
  table_name TEXT,
  column_name TEXT,
  rows BIGINT,
  distinct_spellings BIGINT,
  logical_types BIGINT,
  sample TEXT
) AS $$
DECLARE
  v_schema TEXT;
  v_column RECORD;
  v_name   CONSTANT TEXT :=
    '[A-Za-z_][A-Za-z0-9_.+`]*, *[A-Za-z_][A-Za-z0-9_.]*, *Version=[0-9]+(\.[0-9]+){1,3}, *Culture=[A-Za-z-]+, *PublicKeyToken=([0-9a-fA-F]{16}|null)';
BEGIN
  SELECT n.nspname INTO v_schema
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.oid = to_regclass('__SCHEMA__.wh_settings');

  FOR v_column IN
    SELECT c.table_name AS t, c.column_name AS col
    FROM information_schema.columns c
    JOIN information_schema.tables tb ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
    WHERE c.table_schema = v_schema
      AND tb.table_type = 'BASE TABLE'
      AND c.data_type IN ('jsonb', 'json', 'text', 'character varying')
    ORDER BY c.table_name, c.column_name
  LOOP
    RETURN QUERY EXECUTE format(
      'SELECT %L::text, %L::text, count(DISTINCT r.row_id), count(DISTINCT m.spelling), '
      || 'count(DISTINCT __SCHEMA__.wh_normalize_clr_type_name(m.spelling)), min(m.spelling) '
      || 'FROM (SELECT ctid AS row_id, %I::text AS body FROM %I.%I WHERE %I::text ~ %L) r '
      || 'CROSS JOIN LATERAL (SELECT (regexp_matches(r.body, %L, ''g''))[1] AS spelling) m '
      || 'HAVING count(*) > 0',
      v_column.t, v_column.col, v_column.col, v_schema, v_column.t, v_column.col, v_name,
      '(' || v_name || ')');
  END LOOP;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_find_qualified_type_names IS
'Reports each table and column in this service''s schema holding versioned assembly-qualified type names: the rows, the distinct spellings, the logical types they name, and a sample. More spellings than types is the defect. Reads every text and JSON column, so run it deliberately, not on a schedule.';

-- <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SnapshotSweep_NormalizesQualifiedNamesInsideSnapshotsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SnapshotSweep_AfterItRan_ReportsNothingToDoAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_normalize_snapshot_type_names_batch(p_limit INT)
RETURNS BIGINT AS $$
DECLARE
  v_from BIGINT;
  v_to   BIGINT;
  v_rows BIGINT := 0;
BEGIN
  IF COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
               WHERE setting_key = 'perspective_snapshot_type_name_version'), 0) >= 1 THEN
    RETURN 0;
  END IF;

  -- One scan enumerates the work, as 063 does: slices taken from a predicate re-read the rows already
  -- handled, so the pass would be quadratic in the table.
  IF to_regclass('__SCHEMA__.wh_snapshot_type_name_todo') IS NULL THEN
    EXECUTE 'CREATE TABLE __SCHEMA__.wh_snapshot_type_name_todo ('
      || 'seq BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, '
      || 'stream_id UUID NOT NULL, perspective_name TEXT NOT NULL, snapshot_event_id UUID NOT NULL)';
    EXECUTE 'INSERT INTO __SCHEMA__.wh_snapshot_type_name_todo (stream_id, perspective_name, snapshot_event_id) '
      || 'SELECT stream_id, perspective_name, snapshot_event_id FROM __SCHEMA__.wh_perspective_snapshots '
      || 'WHERE snapshot_data::text ~ ''Version=[0-9]'' ORDER BY ctid';
    INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
    VALUES ('perspective_snapshot_type_name_cursor', '0', 'integer',
            'Progress of the perspective snapshot type-name sweep (migration 191); removed when it completes.',
            NOW(), 'migration:191_ConsumerTypeNameHelpers')
    ON CONFLICT (setting_key) DO UPDATE SET setting_value = '0', updated_at = NOW();
  END IF;

  SELECT setting_value::BIGINT INTO v_from FROM __SCHEMA__.wh_settings
  WHERE setting_key = 'perspective_snapshot_type_name_cursor';
  v_from := COALESCE(v_from, 0);

  EXECUTE 'SELECT max(seq) FROM (SELECT seq FROM __SCHEMA__.wh_snapshot_type_name_todo '
    || 'WHERE seq > $1 ORDER BY seq LIMIT $2) s'
    INTO v_to USING v_from, p_limit;

  IF v_to IS NULL THEN
    EXECUTE 'DROP TABLE __SCHEMA__.wh_snapshot_type_name_todo';
    DELETE FROM __SCHEMA__.wh_settings WHERE setting_key = 'perspective_snapshot_type_name_cursor';
    INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
    VALUES ('perspective_snapshot_type_name_version', '1', 'integer',
            'Whether the type names inside wh_perspective_snapshots were stripped of their version decoration (1 = swept). Gates migration 191''s sweep.',
            NOW(), 'migration:191_ConsumerTypeNameHelpers')
    ON CONFLICT (setting_key) DO UPDATE SET setting_value = '1', updated_at = NOW();
    RETURN 0;
  END IF;

  EXECUTE 'UPDATE __SCHEMA__.wh_perspective_snapshots s '
    || 'SET snapshot_data = __SCHEMA__._wh_strip_type_name_decoration(s.snapshot_data::text)::jsonb '
    || 'FROM __SCHEMA__.wh_snapshot_type_name_todo t '
    || 'WHERE t.seq > $1 AND t.seq <= $2 '
    || 'AND s.stream_id = t.stream_id AND s.perspective_name = t.perspective_name '
    || 'AND s.snapshot_event_id = t.snapshot_event_id '
    || 'AND s.snapshot_data::text <> __SCHEMA__._wh_strip_type_name_decoration(s.snapshot_data::text)'
    USING v_from, v_to;
  GET DIAGNOSTICS v_rows = ROW_COUNT;

  UPDATE __SCHEMA__.wh_settings SET setting_value = v_to::TEXT, updated_at = NOW()
  WHERE setting_key = 'perspective_snapshot_type_name_cursor';

  -- A slice that changed nothing still advanced the cursor, so it reports progress; only the exhausted
  -- worklist reports zero.
  RETURN GREATEST(v_rows, 1);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_normalize_snapshot_type_names_batch IS
'Strips the version decoration from up to p_limit perspective snapshots holding assembly-qualified names, returning a positive number while work remains and 0 once the sweep is done (or was done before). Gated by the wh_settings row perspective_snapshot_type_name_version.';

-- @whizbang:batch-begin
SELECT __SCHEMA__.wh_normalize_snapshot_type_names_batch(@whizbang_batch_size);
-- @whizbang:batch-end
