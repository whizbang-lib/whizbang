-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 556_RebuildWritesABatchOfCursorsInOneStatement.sql (#2513): the batched cursor write is dropped.
-- wh_complete_rebuilt_cursor (273) still writes one cursor at a time.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_complete_rebuilt_cursors(UUID[], TEXT[], UUID[], SMALLINT[]);
