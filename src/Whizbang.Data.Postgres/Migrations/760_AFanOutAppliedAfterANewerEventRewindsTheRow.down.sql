-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 760_AFanOutAppliedAfterANewerEventRewindsTheRow.sql (#2622): the function goes.

SELECT __SCHEMA__.drop_all_overloads('wh_rewind_fan_out_rows_applied_late');
