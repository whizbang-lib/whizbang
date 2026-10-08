-- Migration: 198_LapsedBridgeLooksBeforeItLocks.sql
-- Date: 2026-10-08
-- Description: wh_end_lapsed_bridge reads the role row before it takes any lock, and returns at once when there
--              is no lapsed bridge to end (#1217).
--
--              Every bridged instance that cannot take a role's legacy lock asks this function, on every vote
--              cycle, whether the lock's holder is a lapsed bridged holder whose session should be ended. The
--              answer is almost always no. The function took the role's vote lock and the role row FOR UPDATE
--              before it looked, so it queued behind the holder's renewal vote (which holds both) and behind the
--              holder's fenced duty (wh_assert_role_epoch holds the row FOR SHARE until the duty's transaction
--              commits). Measured under concurrent load in LivenessUnderLoadScenarioTests: every wait the call
--              made was behind the commit-order stamper's fenced transaction or behind the holder's vote, and the
--              vote's own waits were behind the stamper and behind this call.
--
--              The row is now read without locks first. No row, no bridge session, or a holder whose assignment
--              is live: nothing to end, NULL, no lock taken. Otherwise the vote lock and the row lock are taken and
--              the same tests are made again under them before anything is ended, as before, so a fleet asking
--              at once still ends a lapsed bridge once.
--
--              Inside a bootstrap region, as 184's definition is: the bootstrap closure is the marked regions of
--              every migration in order, so a later redefinition of a bootstrap function has to be marked too, or
--              a bootstrap applied again would put 184's body back while the ledger skips this file.
--
-- Dependencies: 184 (wh_end_lapsed_bridge, copied verbatim with the unlocked look first; _role_vote_lock_key,
--               _role_lapse_reason, wh_role_assignments)
-- Objects: wh_end_lapsed_bridge

-- @whizbang:bootstrap-begin

-- ============================================================================
-- wh_end_lapsed_bridge — a bridged would-be winner could not take the legacy
-- lock. When the row says the holder that holds it has lapsed (its assignment is
-- void, or was voided involuntarily), end that holder's legacy-lock session, and
-- only that session: matched by pid AND backend start, and only while it holds
-- p_legacy_lock_key. Waits for the session to exit (so its lock is gone on return)
-- and answers the pid it ended, or NULL when there was nothing to end. Serialized
-- by the vote lock, so a fleet asking at once ends it once. Looks before it locks
-- (198), so the usual answer, nothing to end, waits for nothing.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:EndLapsedBridge_EndsOnlyTheLapsedHoldersLockSession_AndOnlyOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleFenceWaitSqlTests.cs:EndLapsedBridge_NothingToEnd_DoesNotWaitForAFencedTransactionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleFenceWaitSqlTests.cs:EndLapsedBridge_NothingToEnd_DoesNotWaitForTheHoldersVoteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleFenceWaitSqlTests.cs:EndLapsedBridge_ALapsedBridgedHolder_IsStillFoundAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_end_lapsed_bridge(
  p_role TEXT,
  p_legacy_lock_key BIGINT
) RETURNS INTEGER
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_row __SCHEMA__.wh_role_assignments%ROWTYPE;
BEGIN
  -- 198: look first, without locks. Nothing to end is the usual answer, and it must not wait for the
  -- holder's renewal or its fenced duty. A bridge that is lapsing right now is ended on the next ask.
  SELECT * INTO v_row FROM __SCHEMA__.wh_role_assignments r WHERE r.role = p_role;
  IF NOT FOUND OR v_row.bridge_backend_pid IS NULL
     OR (v_row.holder_instance_id IS NOT NULL AND __SCHEMA__._role_lapse_reason(v_row) IS NULL) THEN
    RETURN NULL;
  END IF;

  -- There may be something to end: decide again under the vote lock and the row lock (184, unchanged).
  PERFORM pg_advisory_xact_lock(__SCHEMA__._role_vote_lock_key(p_role));
  SELECT * INTO v_row FROM __SCHEMA__.wh_role_assignments r WHERE r.role = p_role FOR UPDATE;
  IF NOT FOUND OR v_row.bridge_backend_pid IS NULL
     OR (v_row.holder_instance_id IS NOT NULL AND __SCHEMA__._role_lapse_reason(v_row) IS NULL) THEN
    RETURN NULL;
  END IF;
  IF NOT EXISTS (
    SELECT 1
      FROM pg_stat_activity a
      JOIN pg_locks l ON l.pid = a.pid
     WHERE a.pid = v_row.bridge_backend_pid
       AND a.backend_start = v_row.bridge_backend_started_at
       AND l.locktype = 'advisory'
       AND l.granted
       AND l.objsubid = 1
       AND l.database = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database())
       AND ((l.classid::bigint << 32) | (l.objid::bigint & 4294967295)) = p_legacy_lock_key) THEN
    RETURN NULL;
  END IF;
  PERFORM pg_terminate_backend(v_row.bridge_backend_pid, 5000);
  UPDATE __SCHEMA__.wh_role_assignments r
     SET bridge_backend_pid = NULL, bridge_backend_started_at = NULL
   WHERE r.role = p_role;
  RETURN v_row.bridge_backend_pid;
END;
$$;

-- @whizbang:bootstrap-end
