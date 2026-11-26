-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 616_EachInstanceAssignmentIsOneFact.sql (#2595): the derived assignment is dropped. Nothing is stored,
-- so nothing is lost.

SELECT __SCHEMA__.drop_all_overloads('wh_instance_assignments');
