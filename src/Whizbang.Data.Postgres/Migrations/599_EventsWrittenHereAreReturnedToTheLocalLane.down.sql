-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 599_EventsWrittenHereAreReturnedToTheLocalLane.sql (#2573): the walk's window, its restore and its
-- cursor table go. Rows already returned to the local lane stay there, which is correct.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_restore_written_here_origins(BIGINT, BIGINT, TEXT);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_written_here_restoration_window(TEXT, INTEGER);
DROP TABLE IF EXISTS __SCHEMA__.wh_written_here_restoration;
