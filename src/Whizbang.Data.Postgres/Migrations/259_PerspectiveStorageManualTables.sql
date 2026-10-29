-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 259_PerspectiveStorageManualTables.sql
-- Date: 2026-10-29
-- Description: The setting that names the perspective tables whose storage parameters an operator manages by hand
--              (#1747, WS-4 decision 3).
--
--              At every start the schema pass brings each perspective table's fillfactor and autovacuum settings in
--              line with its model's [PerspectiveTableStorage]: a declared value is set, and one of those seven
--              parameters the model does not declare is reset, including a value set by hand, because the catalog
--              does not record who set it. A table named here is left alone instead: neither set nor reset.
--
--              The value is a comma-separated list of bare table names, the form pending_table_rewrites already
--              uses for a list of tables; empty means every table follows its model. Inserted empty with ON CONFLICT
--              DO NOTHING, so a re-run keeps what an operator wrote.
--
-- Dependencies: 032 (wh_settings)
-- Objects: none
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description) VALUES
  ('perspective_storage_manual_tables', '', 'string',
   'Comma-separated perspective tables whose fillfactor and autovacuum settings are managed by hand: the schema pass neither sets nor resets them. Read at each start.')
ON CONFLICT (setting_key) DO NOTHING;
