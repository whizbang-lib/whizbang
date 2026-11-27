-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 630_HeavyStartupWorkIsCappedAndSharedWorkRunsOncePerBuild.sql
-- Date: 2026-11-26
-- Description: Heavy startup work is capped across the fleet, and shared work runs once per schema per build (#2596).
--
--              The Converge stage runs on every instance at every start. A mass restart (a rollout, an autoscaler under
--              load) started that work on every instance at once. Two records, each read and written only through
--              the functions below, so every release that knows them runs one rule:
--
--                * wh_startup_work: one row per unit of per-schema-per-build work, naming the build fingerprint that
--                  last claimed it, whether it is running or done, and who claimed it. The first instance of a build
--                  claims it; the rest skip it while a live claimer runs it and after it finished. A run that did not
--                  finish releases the row, so the next start runs it again. A claimer that is no longer live (the
--                  one liveness rule, 316) is taken over: no deadline to tune. A different build claims it again, so
--                  the row always names the build that changed the shared state last, and a rollback re-runs it.
--                * wh_startup_permits: one row per instance holding a heavy-start permit. At most p_cap live instances
--                  hold one at once; a row whose instance is no longer live is removed as the next request is made,
--                  so a crashed instance never holds the cap.
--
--              Rolling deploys: an older release never calls these and runs its startup work uncapped, as before; it
--              is not counted against the cap. A newer instance on a schema without these functions (asked through
--              to_regprocedure) runs its work as before.
--
--              Cost: startup only, never a hot path. A claim is one row locked by key; a permit request locks the
--              permits table for its own statement (SHARE ROW EXCLUSIVE, so requests take turns and readers do not
--              wait), removes rows of instances the rule calls dead and counts the rest: a few rows, one per instance
--              in Converge.
--
-- Reversal: reversible (the down file drops the functions and both tables; they hold startup state only)
-- Dependencies: 316 (wh_live_instance_ids, wh_instance_is_live)
-- Objects: wh_startup_work, wh_startup_permits, wh_claim_startup_work, wh_complete_startup_work,
--          wh_try_acquire_startup_permit, wh_release_startup_permit
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_startup_work (
  work_name TEXT PRIMARY KEY,
  fingerprint TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('running', 'done')),
  instance_id UUID NOT NULL,
  claimed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_at TIMESTAMPTZ NULL
);

COMMENT ON TABLE __SCHEMA__.wh_startup_work IS
  '630 (#2596): per-schema-per-build startup work: the build fingerprint that last claimed each unit, whether it is '
  'running or done, and the instance that claimed it. Written only by wh_claim_startup_work and wh_complete_startup_work.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_startup_permits (
  instance_id UUID PRIMARY KEY,
  acquired_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE __SCHEMA__.wh_startup_permits IS
  '630 (#2596): the instances holding a heavy-start permit. Written only by wh_try_acquire_startup_permit and '
  'wh_release_startup_permit; a row whose instance the liveness rule calls dead does not count and is removed.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_claim_startup_work');
SELECT __SCHEMA__.drop_all_overloads('wh_complete_startup_work');
SELECT __SCHEMA__.drop_all_overloads('wh_try_acquire_startup_permit');
SELECT __SCHEMA__.drop_all_overloads('wh_release_startup_permit');

-- ============================================================================
-- wh_claim_startup_work: claims one unit of per-schema-per-build work for a
-- build's fingerprint. 'claimed' (run it), 'done' (it last ran to completion
-- for this fingerprint) or 'running' (a live peer of this build runs it).
--
-- <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:TheFirstStartOfABuild_ClaimsSharedWork_ThePeersSkipIt_AndOnceDoneEveryLaterStartSkipsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:AnotherBuild_ClaimsTheWorkAgain_SoARollbackRerunsWhatTheNewerBuildChangedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:AClaimHeldByAnInstanceNoLongerLive_IsTakenOver_AndItsLateCompletionChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:AStartWhosePassAppliedSomething_ClaimsDoneWork_AndARetryOfItsOwnClaimSucceedsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:TenStartsOfOneBuildAtOnce_ClaimTheWorkExactlyOnceAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_claim_startup_work(
  p_work TEXT,
  p_fingerprint TEXT,
  p_instance_id UUID,
  p_force BOOLEAN DEFAULT FALSE
) RETURNS TEXT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_row __SCHEMA__.wh_startup_work%ROWTYPE;
BEGIN
  INSERT INTO __SCHEMA__.wh_startup_work (work_name, fingerprint, state, instance_id)
  VALUES (p_work, p_fingerprint, 'running', p_instance_id)
  ON CONFLICT (work_name) DO NOTHING;
  IF FOUND THEN
    RETURN 'claimed';
  END IF;

  -- A concurrent first claim waited on the key above; this lock then reads the row it committed.
  SELECT * INTO v_row FROM __SCHEMA__.wh_startup_work w WHERE w.work_name = p_work FOR UPDATE;
  IF v_row.fingerprint = p_fingerprint AND NOT p_force THEN
    IF v_row.state = 'done' THEN
      RETURN 'done';
    END IF;
    IF v_row.instance_id <> p_instance_id AND __SCHEMA__.wh_instance_is_live(v_row.instance_id) THEN
      RETURN 'running';
    END IF;
  END IF;

  -- Another build's row, a forced claim, this instance's own earlier claim, or a claimer no longer live.
  UPDATE __SCHEMA__.wh_startup_work w
     SET fingerprint = p_fingerprint, state = 'running', instance_id = p_instance_id, claimed_at = now(),
         completed_at = NULL
   WHERE w.work_name = p_work;
  RETURN 'claimed';
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_claim_startup_work(TEXT, TEXT, UUID, BOOLEAN) IS
  '630 (#2596): claims per-schema-per-build startup work for a build fingerprint: claimed, done or running.';

-- ============================================================================
-- wh_complete_startup_work: ends this instance's claim. Done when the run
-- succeeded; released (the row removed) otherwise, so the next start claims
-- it. A claim this instance no longer holds is left as it is.
--
-- <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:ARunThatDidNotFinish_ReleasesItsClaim_AndTheNextStartClaimsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:AClaimHeldByAnInstanceNoLongerLive_IsTakenOver_AndItsLateCompletionChangesNothingAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_complete_startup_work(
  p_work TEXT,
  p_fingerprint TEXT,
  p_instance_id UUID,
  p_succeeded BOOLEAN
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  IF p_succeeded THEN
    UPDATE __SCHEMA__.wh_startup_work w
       SET state = 'done', completed_at = now()
     WHERE w.work_name = p_work AND w.fingerprint = p_fingerprint AND w.instance_id = p_instance_id
       AND w.state = 'running';
  ELSE
    DELETE FROM __SCHEMA__.wh_startup_work w
     WHERE w.work_name = p_work AND w.fingerprint = p_fingerprint AND w.instance_id = p_instance_id
       AND w.state = 'running';
  END IF;
  RETURN FOUND;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_complete_startup_work(TEXT, TEXT, UUID, BOOLEAN) IS
  '630 (#2596): ends a startup work claim: done when it succeeded, released otherwise; only the claimer''s own claim.';

-- ============================================================================
-- wh_try_acquire_startup_permit: one of p_cap heavy-start permits (a cap
-- below 1 reads as 1). Granted when this instance holds one already or fewer
-- than the cap are held by live instances; held counts the permits after.
--
-- <docs>operations/startup/startup-pipeline#the-heavy-start-cap</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:NoMoreThanTheCap_HoldAPermit_AndAPermitGivenBackIsFreeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:APermitHeldByAnInstanceNoLongerLive_IsFreeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:TenStartsAskingAtOnce_AreGrantedNoMoreThanTheCapAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:ACapBelowOne_IsReadAsOneAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_try_acquire_startup_permit(
  p_instance_id UUID,
  p_cap INTEGER
) RETURNS TABLE(granted BOOLEAN, held INTEGER)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_held INTEGER;
BEGIN
  -- Requests take turns, so two cannot both see a free permit; the lock ends with this statement's transaction.
  LOCK TABLE __SCHEMA__.wh_startup_permits IN SHARE ROW EXCLUSIVE MODE;

  DELETE FROM __SCHEMA__.wh_startup_permits p
   WHERE p.instance_id <> p_instance_id
     AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_live_instance_ids() l WHERE l.instance_id = p.instance_id);

  SELECT count(*)::INTEGER INTO v_held FROM __SCHEMA__.wh_startup_permits;
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_startup_permits p WHERE p.instance_id = p_instance_id) THEN
    RETURN QUERY SELECT TRUE, v_held;
    RETURN;
  END IF;
  IF v_held < GREATEST(p_cap, 1) THEN
    INSERT INTO __SCHEMA__.wh_startup_permits (instance_id) VALUES (p_instance_id);
    RETURN QUERY SELECT TRUE, v_held + 1;
    RETURN;
  END IF;
  RETURN QUERY SELECT FALSE, v_held;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_try_acquire_startup_permit(UUID, INTEGER) IS
  '630 (#2596): one of p_cap heavy-start permits for this instance; permits of instances no longer live are freed.';

-- ============================================================================
-- wh_release_startup_permit: gives this instance's permit back.
--
-- <docs>operations/startup/startup-pipeline#the-heavy-start-cap</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs:NoMoreThanTheCap_HoldAPermit_AndAPermitGivenBackIsFreeAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_release_startup_permit(p_instance_id UUID)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_startup_permits p WHERE p.instance_id = p_instance_id;
  RETURN FOUND;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_release_startup_permit(UUID) IS
  '630 (#2596): gives an instance''s heavy-start permit back.';
