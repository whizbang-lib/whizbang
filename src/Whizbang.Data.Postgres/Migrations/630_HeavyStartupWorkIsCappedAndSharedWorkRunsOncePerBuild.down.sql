-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 630_HeavyStartupWorkIsCappedAndSharedWorkRunsOncePerBuild.sql (#2596): the functions and both tables
-- are dropped. They hold startup state only: the next start of a release with them claims and asks again from nothing,
-- and a release without them runs its startup work uncapped, as before.

SELECT __SCHEMA__.drop_all_overloads('wh_release_startup_permit');
SELECT __SCHEMA__.drop_all_overloads('wh_try_acquire_startup_permit');
SELECT __SCHEMA__.drop_all_overloads('wh_complete_startup_work');
SELECT __SCHEMA__.drop_all_overloads('wh_claim_startup_work');

DROP TABLE IF EXISTS __SCHEMA__.wh_startup_permits;
DROP TABLE IF EXISTS __SCHEMA__.wh_startup_work;
