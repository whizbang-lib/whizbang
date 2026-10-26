-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 241_PerspectiveTablesFollowTheirModelBeforeCreate.sql
-- Date: 2026-10-24
-- Description: A perspective table whose generated name changed is renamed in place before the perspective tables
--              are created, so its rows are kept (#1719).
--
--              reconcile_perspective_registry (030, last word 211) follows a model to a new table name with
--              ALTER TABLE ... RENAME, but both drivers call it after the perspective pass, whose
--              CREATE TABLE IF NOT EXISTS has by then built the new name as an empty table. The rename then fails
--              ("relation already exists"), the reconcile swallows the failure, records the new name, and the rows
--              stay behind under the old one where nothing reads them.
--
--              wh_follow_perspective_table_renames runs before the perspective pass, from the same payload. For
--              each model it takes the existing tables its rows may be under, other than its table now: the one
--              its registry row names (by CLR type and service), then each entry's FormerTableName, the name a
--              released Dapper build gave the table of one of the model's perspectives (that build named tables
--              from the perspective class and never wrote the registry, so on upgrade there is no row to follow).
--              A former name the payload maps any model to is that model's table and is never taken. Each one:
--
--                new table does not exist         the table is renamed, and so is every index on it whose name
--                                                 carries the old table name (its primary key included) or the part
--                                                 of it past wh_per_, when the new index name is free and within 63
--                                                 bytes: 'renamed'
--                new table exists                 nothing is dropped, merged or moved: 'both_exist', which the
--                                                 drivers log as a warning naming the model and both tables (a
--                                                 model folded by two perspectives left two tables on Dapper; the
--                                                 first is renamed and the second is reported this way)
--
--              No source table, no row. The registry row itself is left for the reconcile, which still runs after
--              the pass and records the new name. On a fresh database nothing exists and nothing is returned.
--
--              Number 241: 239 and 240 are taken by an open branch.
--
-- Dependencies: 211 (last word of reconcile_perspective_registry, which keys wh_perspective_registry the same way)
-- Objects: wh_follow_perspective_table_renames
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_follow_perspective_table_renames');

-- <docs>fundamentals/perspectives/table-naming#renaming-perspectives</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPerspectiveTableRenameTests.cs:Initializer_WhenTheModelsTableNameChanges_RenamesTheExistingTableInPlaceAndKeepsItsRowsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPerspectiveTableRenameTests.cs:Initializer_WhenBothTablesExist_LeavesBothAndWarnsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPerspectiveTableRenameTests.cs:Initializer_WhenAReleasedBuildLeftTheTableUnderItsPerspectiveName_FollowsItWithNoRegistryRowAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPerspectiveTableRenameTests.cs:Initializer_WhenTwoPerspectivesOfOneModelLeftTwoTables_RenamesTheRegisteredOneAndWarnsAboutTheOtherAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPerspectiveTableRenameTests.cs:Initializer_WhenAFormerNameIsAnotherModelsTable_LeavesItToThatModelAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PerspectiveTableRenameTests.cs:Initialize_WhenTheModelsTableNameChanged_RenamesTheTableInPlaceAndKeepsItsRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_follow_perspective_table_renames(
  p_perspectives JSONB,
  p_service_name VARCHAR DEFAULT NULL
)
RETURNS TABLE (
  o_action VARCHAR,
  o_clr_type_name VARCHAR,
  o_old_table_name VARCHAR,
  o_new_table_name VARCHAR
) AS $$
DECLARE
  v_entry RECORD;
  v_registered VARCHAR;
  v_targets TEXT[];
  v_sources TEXT[];
  v_old_table TEXT;
  v_old_oid OID;
  v_index RECORD;
  v_new_index TEXT;
  v_old_short TEXT;
  v_new_short TEXT;
BEGIN
  -- Every table the payload maps a model to. A former name that is one of them is that model's table now.
  v_targets := ARRAY(SELECT DISTINCT e->>'TableName' FROM jsonb_array_elements(p_perspectives) AS e);

  FOR v_entry IN
    SELECT
      a.e->>'ClrTypeName' AS clr_type_name,
      a.e->>'TableName' AS table_name,
      COALESCE(a.e->>'ServiceName', p_service_name) AS service_name,
      array_agg(a.e->>'FormerTableName' ORDER BY a.ord)
        FILTER (WHERE NULLIF(a.e->>'FormerTableName', '') IS NOT NULL) AS former_tables
    FROM jsonb_array_elements(p_perspectives) WITH ORDINALITY AS a(e, ord)
    GROUP BY 1, 2, 3
  LOOP
    v_registered := NULL;
    SELECT pr.table_name INTO v_registered
    FROM __SCHEMA__.wh_perspective_registry pr
    WHERE pr.clr_type_name = v_entry.clr_type_name
      AND pr.service_name = v_entry.service_name;

    -- The existing tables the model's rows may be under: the one its registry row names first, then each name a
    -- released build gave the table of one of its perspectives, in payload order.
    v_sources := ARRAY(
      SELECT s.name
      FROM unnest(ARRAY[v_registered]::TEXT[] || COALESCE(v_entry.former_tables, ARRAY[]::TEXT[]))
             WITH ORDINALITY AS s(name, ord)
      WHERE s.name IS NOT NULL
        AND s.name <> v_entry.table_name
        AND (s.name = v_registered OR NOT s.name = ANY(v_targets))
        AND to_regclass('__SCHEMA__.' || quote_ident(s.name)) IS NOT NULL
      GROUP BY s.name
      ORDER BY min(s.ord));

    FOREACH v_old_table IN ARRAY v_sources LOOP
      IF to_regclass('__SCHEMA__.' || quote_ident(v_entry.table_name)) IS NOT NULL THEN
        o_action := 'both_exist';
      ELSE
        v_old_oid := to_regclass('__SCHEMA__.' || quote_ident(v_old_table));
        EXECUTE format('ALTER TABLE __SCHEMA__.%I RENAME TO %I', v_old_table, v_entry.table_name);

        -- The table's own indexes, found by its oid, which the rename keeps. An index is named after the whole
        -- table (ix_wh_per_order_..., wh_per_order_pkey) or after the part past the prefix (idx_order_...).
        v_old_short := CASE WHEN v_old_table LIKE 'wh\_per\_%' THEN substr(v_old_table, 8) END;
        v_new_short := CASE WHEN v_entry.table_name LIKE 'wh\_per\_%' THEN substr(v_entry.table_name, 8) END;
        FOR v_index IN
          SELECT ic.relname AS name
          FROM pg_index i
          JOIN pg_class ic ON ic.oid = i.indexrelid
          WHERE i.indrelid = v_old_oid
        LOOP
          v_new_index := CASE
            WHEN position(v_old_table IN v_index.name) > 0 THEN
              overlay(v_index.name PLACING v_entry.table_name
                      FROM position(v_old_table IN v_index.name) FOR length(v_old_table))
            WHEN v_old_short IS NOT NULL AND v_new_short IS NOT NULL AND position(v_old_short IN v_index.name) > 0 THEN
              overlay(v_index.name PLACING v_new_short
                      FROM position(v_old_short IN v_index.name) FOR length(v_old_short))
          END;
          IF v_new_index IS NOT NULL
             AND octet_length(v_new_index) <= 63
             AND to_regclass('__SCHEMA__.' || quote_ident(v_new_index)) IS NULL THEN
            EXECUTE format('ALTER INDEX __SCHEMA__.%I RENAME TO %I', v_index.name, v_new_index);
          END IF;
        END LOOP;

        o_action := 'renamed';
      END IF;

      o_clr_type_name := v_entry.clr_type_name;
      o_old_table_name := v_old_table;
      o_new_table_name := v_entry.table_name;
      RETURN NEXT;
    END LOOP;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_follow_perspective_table_renames(JSONB, VARCHAR) IS
  'Renames a perspective table whose model now maps to a new name, before the perspective tables are created (#1719).';
