-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 597_TheEphemeralReapScansStartAtTheOldestBody.sql (#2574): the floor function goes, and the reap's
-- candidate scans walk the whole ephemeral index again. The two settings it wrote are left; nothing reads them.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_ephemeral_reap_scan_floor();
