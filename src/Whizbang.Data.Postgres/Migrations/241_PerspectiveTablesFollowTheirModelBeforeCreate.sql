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
--              each model the registry already maps (by its CLR type and service) to a different table:
--
--                old table exists, new does not   the table is renamed, and so is every index on it whose name
--                                                 carries the old table name (its primary key included) or the part
--                                                 of it past wh_per_, when the new index name is free and within 63
--                                                 bytes: 'renamed'
--                both exist                       nothing is dropped, merged or moved: 'both_exist', which the
--                                                 drivers log as a warning naming the model and both tables
--                otherwise                        nothing to do, and no row is returned
--
--              The registry row itself is left for the reconcile, which still runs after the pass and records the
--              new name. On a fresh database the registry is empty and the function returns nothing.
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
  v_old_table VARCHAR;
  v_old_oid OID;
  v_index RECORD;
  v_new_index TEXT;
  v_old_short TEXT;
  v_new_short TEXT;
BEGIN
  FOR v_entry IN
    SELECT DISTINCT
      e->>'ClrTypeName' AS clr_type_name,
      e->>'TableName' AS table_name,
      COALESCE(e->>'ServiceName', p_service_name) AS service_name
    FROM jsonb_array_elements(p_perspectives) AS e
  LOOP
    SELECT pr.table_name INTO v_old_table
    FROM __SCHEMA__.wh_perspective_registry pr
    WHERE pr.clr_type_name = v_entry.clr_type_name
      AND pr.service_name = v_entry.service_name;

    CONTINUE WHEN v_old_table IS NULL OR v_old_table = v_entry.table_name;

    v_old_oid := to_regclass('__SCHEMA__.' || quote_ident(v_old_table));
    CONTINUE WHEN v_old_oid IS NULL;

    IF to_regclass('__SCHEMA__.' || quote_ident(v_entry.table_name)) IS NOT NULL THEN
      o_action := 'both_exist';
    ELSE
      EXECUTE format('ALTER TABLE __SCHEMA__.%I RENAME TO %I', v_old_table, v_entry.table_name);

      -- The table's own indexes, found by its oid, which the rename keeps. An index is named after the whole table
      -- (ix_wh_per_order_..., wh_per_order_pkey) or after the part past the prefix (idx_order_...).
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
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_follow_perspective_table_renames(JSONB, VARCHAR) IS
  'Renames a perspective table whose model now maps to a new name, before the perspective tables are created (#1719).';
