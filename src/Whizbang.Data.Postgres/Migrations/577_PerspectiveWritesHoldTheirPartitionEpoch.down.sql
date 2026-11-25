-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 577_PerspectiveWritesHoldTheirPartitionEpoch.sql (#2556): both functions are dropped. A driver of this
-- version calls them in map mode, so the reversal belongs with a driver that does not.

SELECT __SCHEMA__.drop_all_overloads('wh_held_stream');
SELECT __SCHEMA__.drop_all_overloads('wh_hold_partition_epoch');
