-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 451_OperationalRecordsRollOff.sql (#2354): the operational retention sweep and its index go. Rows it
-- removed do not come back; the rewrite log keeps its row cap and restore requests keep their lifecycle cascade.

SELECT __SCHEMA__.drop_all_overloads('wh_sweep_operational_retention');
DROP INDEX IF EXISTS __SCHEMA__.idx_archive_restore_requests_restored;
