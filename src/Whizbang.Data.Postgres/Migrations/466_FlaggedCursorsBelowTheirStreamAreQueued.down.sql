-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 466_FlaggedCursorsBelowTheirStreamAreQueued.sql (#2379): the repair function goes. A row it queued is
-- ordinary work and is processed as any other, so nothing else is undone.

SELECT __SCHEMA__.drop_all_overloads('wh_queue_unreachable_rewinds');
