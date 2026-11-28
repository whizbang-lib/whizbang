-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 761_ARowWriteThatMissedAnAppliedFanOutRewindsTheRow.sql (#2622): the function goes.

SELECT __SCHEMA__.drop_all_overloads('wh_rewind_row_fan_outs_missed');
