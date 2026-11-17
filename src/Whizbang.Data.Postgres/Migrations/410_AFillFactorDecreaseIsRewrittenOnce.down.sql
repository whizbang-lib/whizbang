-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 405_AFillFactorDecreaseIsRewrittenOnce.sql (#2300): wh_clear_table_rewrite goes back to its 254
-- definition, which does not read the reason table, then the two functions and the reason table are dropped (lossy: a
-- table still queued for a lowered fillfactor then waits for bloat, as it did before).

-- <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteRequestSqlTests.cs:RequestIsIdempotent_AndClearRemovesItAsync</tests>
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

  DELETE FROM __SCHEMA__.wh_table_rewrite_measurements WHERE table_name = p_table;
END;
$$;

SELECT __SCHEMA__.drop_all_overloads('wh_request_fillfactor_rewrite');
SELECT __SCHEMA__.drop_all_overloads('wh_tables_owed_fillfactor_rewrite');
DROP TABLE IF EXISTS __SCHEMA__.wh_fillfactor_rewrite_requests;
