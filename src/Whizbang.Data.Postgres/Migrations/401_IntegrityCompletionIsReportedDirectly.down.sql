-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 401_IntegrityCompletionIsReportedDirectly.sql (#2261): the tally, its two functions and the kept
-- watermark column are dropped. Lossy: the tally's minutes and the watermarks are lost, and the next maintenance ticks
-- and checkpoints rebuild them once 401 is applied again. Nothing before 401 reads any of them.

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_tally_since');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_tally_add');
DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_ledger_tally;
-- RECLAIM: not required: wh_integrity_known_origins holds one row per origin (a handful), so the eight dropped bytes
--          per row are negligible and no rewrite is worth running.
ALTER TABLE __SCHEMA__.wh_integrity_known_origins DROP COLUMN IF EXISTS last_commit_sequence;
