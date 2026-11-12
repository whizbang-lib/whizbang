-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 399_IntegrityCompletionIsReportedDirectly.sql
-- Date: 2026-11-11
-- Description: What the derived integrity completion gauges read (#2261): each origin's latest announced commit
--              sequence, and a per-minute tally of the ledger buckets the service opened and closed.
--
--              verified_percent divides an origin's seal (wh_integrity_seals.sealed_through) by the highest commit
--              sequence the origin has committed that this service knows of. Each checkpoint announces it (its
--              watermark, ToCommitSequence) and nothing kept it:
--              1. wh_integrity_known_origins.last_commit_sequence (new, nullable): the highest watermark a checkpoint
--                 from the origin announced, written with the row the checkpoint already rewrites (339). NULL until a
--                 checkpoint announces one. Not indexed, so the update stays heap-only.
--
--              repair_progress_percent and backlog_eta_seconds are computed over a rolling window, per service, from
--              the same counts as the ledger_buckets_opened and ledger_buckets_closed counters. Those counters are per
--              process and start over on a restart, so every instance adds what its counters counted since its last
--              write to one shared tally, and the instance that reports the backlog reads the window from it:
--              2. wh_integrity_ledger_tally (new): one row per minute (the writer's clock, truncated), the buckets opened
--                 and closed in it, summed over every instance that wrote in that minute.
--              3. wh_integrity_ledger_tally_add (new): adds one instance's counts to its minute and removes the minutes
--                 before the given keep point (the caller keeps twice the window), in one call. One upsert per instance
--                 per maintenance tick, and only when its counters moved.
--              4. wh_integrity_ledger_tally_since (new): the buckets opened and closed in the minutes from the given
--                 instant, zero when none.
--
--              The tally holds about two windows of minutes (120 rows by default), so neither function needs an index
--              beyond the primary key.
--
--              Number 399: 395 is the highest on develop, and the integrity bundle's changes take 396 to 401 in order.
--
-- Reversal: lossy (the reverse drops the tally, whose minutes are lost, and the kept watermarks; both are rebuilt by
--           the next ticks and checkpoints after the migration is applied again)
-- Dependencies: 339 (wh_integrity_known_origins)
-- Objects: wh_integrity_known_origins.last_commit_sequence, wh_integrity_ledger_tally, wh_integrity_ledger_tally_add,
--          wh_integrity_ledger_tally_since

ALTER TABLE __SCHEMA__.wh_integrity_known_origins
  ADD COLUMN IF NOT EXISTS last_commit_sequence BIGINT;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_known_origins.last_commit_sequence IS
'399 (#2261): the highest commit sequence (inclusive) a checkpoint from this origin announced, the denominator of verified_percent. NULL until a checkpoint announces one.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_ledger_tally (
  minute_start TIMESTAMPTZ NOT NULL PRIMARY KEY,
  opened       BIGINT      NOT NULL DEFAULT 0,
  closed       BIGINT      NOT NULL DEFAULT 0
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_ledger_tally IS
'399 (#2261): the integrity ledger buckets the service opened and closed, per minute, summed over every instance; the source of repair_progress_percent and backlog_eta_seconds. Holds about two windows of minutes.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_tally_add');

-- One instance's counts added to its minute; the minutes before p_keep_since removed in the same call.
-- <docs>resilience/stream-integrity#derived-completion-meters</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCompletionSqlTests.cs:TheTally_AddsPerMinute_AndIsSummedOverTheWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCompletionSqlTests.cs:TheTally_PrunesMinutesOlderThanItKeeps_InTheSameCallAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_tally_add(
  p_at         TIMESTAMPTZ,
  p_opened     BIGINT,
  p_closed     BIGINT,
  p_keep_since TIMESTAMPTZ
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_ledger_tally AS t (minute_start, opened, closed)
  VALUES (date_trunc('minute', p_at), p_opened, p_closed)
  ON CONFLICT (minute_start) DO UPDATE
    SET opened = t.opened + EXCLUDED.opened,
        closed = t.closed + EXCLUDED.closed;

  DELETE FROM __SCHEMA__.wh_integrity_ledger_tally WHERE minute_start < p_keep_since;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_ledger_tally_add(TIMESTAMPTZ, BIGINT, BIGINT, TIMESTAMPTZ) IS
'399 (#2261): adds one instance''s ledger buckets opened and closed to the minute of p_at, and removes the minutes before p_keep_since.';

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_tally_since');

-- The window's counts: every minute from p_since, zero when none.
-- <docs>resilience/stream-integrity#derived-completion-meters</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCompletionSqlTests.cs:TheTally_AddsPerMinute_AndIsSummedOverTheWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCompletionSqlTests.cs:AnEmptyTally_ReadsZeroAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_tally_since(p_since TIMESTAMPTZ)
RETURNS TABLE (opened BIGINT, closed BIGINT)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  SELECT COALESCE(SUM(t.opened), 0)::BIGINT, COALESCE(SUM(t.closed), 0)::BIGINT
  FROM __SCHEMA__.wh_integrity_ledger_tally t
  WHERE t.minute_start >= date_trunc('minute', p_since);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_ledger_tally_since(TIMESTAMPTZ) IS
'399 (#2261): the ledger buckets the service opened and closed in the minutes from p_since (its minute included).';
