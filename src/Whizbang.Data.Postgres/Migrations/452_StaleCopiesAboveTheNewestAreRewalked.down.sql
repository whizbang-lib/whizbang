-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 452_StaleCopiesAboveTheNewestAreRewalked.sql (#2366): the function goes. A walk it restarted completes
-- again and changes nothing more.

SELECT __SCHEMA__.drop_all_overloads('wh_restart_superseding_history_walks');
