-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 494_CutsAwaitingTheSeal.sql (#2365): eligible_not_yet_deleted reports nothing.

DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_cuts_awaiting_seal;
