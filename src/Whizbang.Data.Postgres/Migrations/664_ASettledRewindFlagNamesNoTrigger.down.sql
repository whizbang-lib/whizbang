-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 664_ASettledRewindFlagNamesNoTrigger.sql: the trigger and its function go. The columns it cleared are
-- not restored; a settled flag's trigger and times were never read.
-- Objects: _wh_unflagged_cursor_names_no_trigger, trg_wh_unflagged_cursor_names_no_trigger

DROP TRIGGER IF EXISTS trg_wh_unflagged_cursor_names_no_trigger ON __SCHEMA__.wh_perspective_cursors;
DROP FUNCTION IF EXISTS __SCHEMA__._wh_unflagged_cursor_names_no_trigger();
