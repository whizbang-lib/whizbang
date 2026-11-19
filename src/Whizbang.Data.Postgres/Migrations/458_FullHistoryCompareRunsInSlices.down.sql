-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 458_FullHistoryCompareRunsInSlices.sql (#2361): the slice and start functions and the cursor go.

SELECT __SCHEMA__.drop_all_overloads('wh_heal_stream_digests_slice');
SELECT __SCHEMA__.drop_all_overloads('wh_full_history_compare_start');
DELETE FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_full_history_compare_cursor';
