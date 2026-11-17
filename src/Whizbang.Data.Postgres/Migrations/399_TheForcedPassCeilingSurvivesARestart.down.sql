-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 399_TheForcedPassCeilingSurvivesARestart.sql (#2288): the deferral table is dropped (lossy: the
-- ceiling is again measured in memory from each process start).

DROP TABLE IF EXISTS __SCHEMA__.wh_housekeeping_deferrals;
