-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 355_RoleHoldersAreJudgedByTheLivenessRule.sql
-- Date: 2026-11-07
-- Description: A role holder's liveness is judged by the one liveness rule (#2140).
--
--              #2027 routed every reader of instance liveness through wh_instance_liveness (a recent heartbeat, or a
--              held alive-lock on the instance's notification connection). The role lease was the one rule left of its
--              own: a holder lapsed 15 seconds after its last renewal, and it renewed only when its work loop verified
--              before a unit of exclusive work. A freshly elected holder busy with startup, or inside one long unit of
--              work, lapsed although the rule called it live, the role moved, and the old holder learned it only on its
--              next verify.
--
--              Now a holder keeps its role past its lease while the one rule calls it live (wh_instance_is_live, the
--              shared code every other reader asks) and it made progress inside the window it declared for its epoch:
--              its last renewal, which only its own work loop sends, is newer than its progress window. What remains
--              role-specific is the epoch fence and that progress check, so a holder that is connected but stuck still
--              loses the role within its window. A holder the rule calls dead lapses at its lease, as before.
--
--              * wh_role_assignments gains progress_window and progress_epoch. The window counts only for the epoch
--                that declared it, so a window left on the row by an earlier holder never keeps a later one.
--              * wh_declare_role_progress_window: a holder declares its window once it holds an epoch. A holder that
--                declares none (an instance on an older release) is judged by its lease alone, exactly as before, so a
--                mixed-version fleet never runs on two rules for one epoch.
--              * _role_holder_kept_live: the one place the role asks the rule. _role_lapse_reason (the vote, the
--                renewal, the status, the duty-backend mark) and the fence (wh_assert_role_epoch) both call it, so the
--                fence treats a holder as live exactly when the vote does.
--              * wh_role_is_held: the read a holder's grant watch makes once per renew interval, so a holder learns of
--                a loss within one interval without waiting for its own next verify.
--
--              All of it is in the bootstrap region: the migrator is elected before migrations run, by the same vote.
--
--              Number 355: the next free number after the ones taken on develop and in open pull requests.
--
-- Dependencies: 184 (wh_role_assignments, _role_lapse_reason, _role_backend_active, wh_assert_role_epoch), 316
--               (wh_instance_is_live, the one liveness rule)
-- Objects: wh_role_assignments, _role_holder_kept_live, _role_lapse_reason, wh_assert_role_epoch,
--          wh_declare_role_progress_window, wh_role_is_held
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the migrator duty is won by the vote, which judges its holder through these functions.

ALTER TABLE __SCHEMA__.wh_role_assignments
  -- How long the holder may go without renewing before it loses the role although the one liveness rule calls it
  -- live: the role's progress check. NULL: none declared, and the lease alone decides.
  ADD COLUMN IF NOT EXISTS progress_window INTERVAL,
  -- The epoch that declared progress_window; the window counts for that epoch only.
  ADD COLUMN IF NOT EXISTS progress_epoch BIGINT;

-- ============================================================================
-- _role_holder_kept_live: whether the holder in p_row keeps its role past its
-- lease at p_at: it declared a progress window for this epoch, it renewed inside
-- that window, and the one liveness rule calls it live. The progress check comes
-- first, so the rule is asked only for a holder inside its window.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AHolderPastItsLease_ThatTheRuleCallsLive_AndThatMadeProgressInsideItsWindow_KeepsTheRoleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AHolderTheRuleCallsDead_LapsesAtItsLease_InsideItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AConnectedButStuckHolder_LapsesAtItsProgressWindow_ThoughTheRuleCallsItLiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:ProgressWindow_CountsOnlyForTheEpochThatDeclaredItAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_holder_kept_live(p_row __SCHEMA__.wh_role_assignments, p_at TIMESTAMPTZ)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT p_row.progress_window IS NOT NULL
     AND p_row.progress_epoch = p_row.epoch
     AND p_row.renewed_at + p_row.progress_window > p_at
     AND __SCHEMA__.wh_instance_is_live(p_row.holder_instance_id)
$$;

COMMENT ON FUNCTION __SCHEMA__._role_holder_kept_live(__SCHEMA__.wh_role_assignments, TIMESTAMPTZ) IS
  '355 (#2140): whether a role holder keeps its role past its lease: it renewed inside the progress window it declared '
  'for this epoch, and the one liveness rule (wh_instance_is_live) calls it live.';

-- ============================================================================
-- _role_lapse_reason: why the assignment in p_row is no longer valid, or NULL
-- while it is (see 184). Tombstone first (the fleet's verdict), then
-- registration, then the lease, which the marked duty backend's activity holds
-- open, and past which the holder is judged by the one liveness rule and its
-- progress window (_role_holder_kept_live).
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AHolderPastItsLease_ThatTheRuleCallsLive_AndThatMadeProgressInsideItsWindow_KeepsTheRoleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_ADirectHolderHoldingItsAliveLock_DoesNotLapse_WhileItsHeartbeatIsPastOneBeatAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AHolderThatDeclaredNoWindow_LapsesAtItsLease_AsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Status_ReportsAHolderKeptByTheRule_AsHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_TreatsAnExpiredHolderAsLive_WhileItsMarkedDutyBackendRunsAStatementAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_WhenTheHolderIsTombstoned_VoidsItAsEvictedAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_lapse_reason(p_row __SCHEMA__.wh_role_assignments)
RETURNS TEXT
LANGUAGE sql
STABLE
AS $$
  SELECT CASE
    WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = p_row.holder_instance_id) THEN 'evicted'
    WHEN NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances s WHERE s.instance_id = p_row.holder_instance_id) THEN 'unregistered'
    WHEN (p_row.lease_expires_at IS NULL OR p_row.lease_expires_at <= now())
         AND NOT __SCHEMA__._role_backend_active(p_row.duty_backend_pid, p_row.duty_backend_started_at)
         AND NOT __SCHEMA__._role_holder_kept_live(p_row, now()) THEN 'lapsed'
    ELSE NULL
  END
$$;

-- ============================================================================
-- wh_assert_role_epoch: the fence (see 184). Raises WHF01 unless (instance,
-- epoch) is the current, non-tombstoned assignment whose lease is unexpired,
-- whose marked duty backend is running a statement, or whose holder the one
-- liveness rule keeps inside its progress window (the vote treats all three as
-- live, so the fence does too). FOR SHARE holds the row until the caller
-- commits. clock_timestamp(): a fenced transaction may have started long before
-- this call.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AHolderPastItsLease_ThatTheRuleCallsLive_AndThatMadeProgressInsideItsWindow_KeepsTheRoleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AHolderTheRuleCallsDead_LapsesAtItsLease_InsideItsWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:AssertEpoch_WithAStaleEpoch_RaisesInSql_AndTheWriteBesideItRollsBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:AssertEpoch_RefusesTheRightEpochFromTheWrongHolder_ALapsedLease_AndAnUnknownRoleAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_assert_role_epoch(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS VOID
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  PERFORM 1
     FROM __SCHEMA__.wh_role_assignments r
    WHERE r.role = p_role
      AND r.holder_instance_id = p_instance_id
      AND r.epoch = p_epoch
      AND (r.lease_expires_at > clock_timestamp()
           OR __SCHEMA__._role_backend_active(r.duty_backend_pid, r.duty_backend_started_at)
           OR __SCHEMA__._role_holder_kept_live(r, clock_timestamp()))
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = p_instance_id)
      FOR SHARE OF r;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'role % at epoch % is not held by instance %', p_role, p_epoch, p_instance_id
      USING ERRCODE = 'WHF01',
            HINT = 'The assignment was lost (lapsed, released, evicted or taken over). Stop the exclusive work.';
  END IF;
END;
$$;

-- ============================================================================
-- wh_declare_role_progress_window: the holder of (p_role, p_epoch) declares how
-- long it may go without renewing before it loses the role although the one
-- liveness rule calls it live. A window of zero or NULL declares none (the lease
-- alone decides). FALSE when (instance, epoch) is not the current live
-- assignment.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:DeclareProgressWindow_IsRefusedToAHolderThatDoesNotHoldTheRole_AndAZeroWindowTurnsItOffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessE2ETests.cs:ARestartWithASlowStartup_KeepsOneHolder_AndTheHoldersLateVerifyRenewsItAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_declare_role_progress_window(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_window INTERVAL
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_role_assignments r
     SET progress_window = CASE WHEN p_window > INTERVAL '0' THEN p_window END,
         progress_epoch = p_epoch
   WHERE r.role = p_role
     AND r.holder_instance_id = p_instance_id
     AND r.epoch = p_epoch
     AND __SCHEMA__._role_lapse_reason(r) IS NULL;
  RETURN FOUND;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_declare_role_progress_window(TEXT, UUID, BIGINT, INTERVAL) IS
  '355 (#2140): the holder declares its progress window for its epoch: how long it may go without renewing before it '
  'loses the role although the one liveness rule calls it live. Zero or NULL declares none.';

-- ============================================================================
-- wh_role_is_held: whether (p_instance_id, p_epoch) is the current, valid
-- assignment of p_role, judged exactly as the vote judges it. A holder's grant
-- watch asks this once per renew interval; it writes nothing, so it never
-- counts as progress.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:RoleHeld_AnswersForTheCurrentHolderAndEpochOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:RoleHeld_ReadsTheAssignmentByItsPrimaryKeyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessE2ETests.cs:TheOldHolder_ObservesItsLossWithinOneRenewInterval_SoExclusiveWorkNeverOverlapsAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_role_is_held(p_role TEXT, p_instance_id UUID, p_epoch BIGINT)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
SET timezone = 'UTC'
AS $$
  SELECT EXISTS (
    SELECT 1
      FROM __SCHEMA__.wh_role_assignments r
     WHERE r.role = p_role
       AND r.holder_instance_id = p_instance_id
       AND r.epoch = p_epoch
       AND __SCHEMA__._role_lapse_reason(r) IS NULL)
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_role_is_held(TEXT, UUID, BIGINT) IS
  '355 (#2140): whether the instance holds the role at that epoch, judged as the vote judges it. Read-only: the grant '
  'watch asks it once per renew interval, and it never counts as the holder''s progress.';

-- @whizbang:bootstrap-end
