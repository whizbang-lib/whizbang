-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 407_ARewriteRecordsWhatCarriedItOut.sql (#2301): the recording function and the history are dropped
-- (lossy: the record of past rewrites is lost; requests are cleared through wh_clear_table_rewrite as before).

SELECT __SCHEMA__.drop_all_overloads('wh_complete_table_rewrite');
DROP TABLE IF EXISTS __SCHEMA__.wh_table_rewrite_history;
