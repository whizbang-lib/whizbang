-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 399_TheForcedPassCeilingSurvivesARestart.sql
-- Date: 2026-11-12
-- Description: The housekeeping forced-pass ceiling (Whizbang:Housekeeping:MaxDeferral) is measured from the first
--              deferral of each deferrable activity, and that moment was held only in process memory, so every restart
--              started the hour over and a service redeployed more often than the ceiling never reached its forced pass
--              (#2288). It is now held here, in the service's own schema, beside the work tables the coordinator reads
--              settledness from:
--
--                wh_housekeeping_deferrals  one row per deferral budget that is being spent: maintenance, dead-letter
--                                           recovery, and each integrity requester. Written by the first deferral of a
--                                           run of deferrals (first writer wins), read with the service backlog in the
--                                           same statement, and deleted when a pass of that activity completes, by any
--                                           instance of the service.
--
--              No function: the three statements are plain single-row reads and writes issued by the coordinators
--              (CoordinatorFleetSql), and the table holds at most a handful of rows.
--
--              Number 399: 397 is the highest on develop.
--
-- Reversal: lossy (the table is dropped with its rows; the ceiling is again measured in memory from each process start)
-- Dependencies: none
-- Objects: wh_housekeeping_deferrals

-- <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HousekeepingDeferralSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperFleetRecoveryTests.cs:HousekeepingDeferral_IsReadWithTheBacklog_KeepsTheFirstWriter_AndClearsAsync</tests>
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_housekeeping_deferrals (
  budget_key TEXT NOT NULL,
  first_deferred_at TIMESTAMPTZ NOT NULL,
  CONSTRAINT pk_housekeeping_deferrals PRIMARY KEY (budget_key)
);

COMMENT ON TABLE __SCHEMA__.wh_housekeeping_deferrals IS
  'When each housekeeping deferral budget (maintenance, dead-letter recovery, each integrity requester) was first '
  'deferred, so the forced-pass ceiling (MaxDeferral) is measured across process restarts (#2288). Written on the '
  'first deferral, deleted when a pass of that activity completes.';
