-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 321_EveryDutyIsARoleAssignment.sql
-- Date: 2026-11-03
-- Description: One way to hold a duty (#2044, part 2 of the coordination standard, #2027). Every duty is a role
--              assignment with a lease and an epoch; no duty is held by a session advisory lock, and no role
--              function reads one.
--
--              * The vote and the renewal no longer read advisory locks. An instance on an older release that holds a
--                duty by its own session lock records the holding (record_capability), and the vote answers
--                legacy_holder, and a holder steps aside within one renewal, while that instance is live by the one
--                liveness rule (wh_instance_is_live). A recorded holding of an instance the rule calls dead does not
--                count, and is reaped with its row.
--              * The legacy-lock bridge is gone: no bridge session is recorded on a grant, and the bridge expiry ends
--                nothing. Both keep their signatures, so an instance on an older release that still passes a legacy
--                key or a bridge session, or calls the expiry, is answered from the table alone.
--              * The advisory-lock reader (_advisory_lock_held_elsewhere) is dropped.
--              * The multi-role vote takes no legacy keys (NULL is accepted, and any keys passed are ignored).
-- Dependencies: 108 (wh_instance_capabilities, record_capability, release_capability)
--               184 (wh_role_assignments, wh_role_candidates and the role functions redefined here)
--               316 (wh_instance_is_live)

-- @whizbang:bootstrap-begin
-- Bootstrap: the migrator duty is won by this vote, and the migrator is chosen before migrations run.

-- ============================================================================
-- _role_held_outside_assignment: whether an instance other than p_caller holds
-- p_role outside the role table: it recorded the holding (an older release's
-- session-lock elector records its win through record_capability), it is not the
-- role's assignment holder, and the one liveness rule calls it live.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_WhileALiveInstanceHoldsTheDutyOutsideTheTable_AnswersLegacyHolderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_DoesNotCountAHolderOutsideTheTable_ThatTheLivenessRuleCallsDeadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:RenewLease_StepsAside_WhenALegacyHolderTookTheLockAfterTheVoteAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_held_outside_assignment(p_role TEXT, p_caller UUID)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT EXISTS (
    SELECT 1
      FROM __SCHEMA__.wh_instance_capabilities c
     WHERE c.capability = p_role
       AND c.instance_id IS DISTINCT FROM p_caller
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_role_assignments r
                        WHERE r.role = p_role AND r.holder_instance_id = c.instance_id)
       AND __SCHEMA__.wh_instance_is_live(c.instance_id))
$$;

COMMENT ON FUNCTION __SCHEMA__._role_held_outside_assignment(TEXT, UUID) IS
  '321 (#2044): whether a live instance other than the caller holds the role outside the role table (an older release''s '
  'recorded session-lock holding). Judged by the one liveness rule; no lock is read.';

-- ============================================================================
-- wh_vote_role: the vote (see 184). Outcomes are unchanged; legacy_holder now
-- means a live instance holds the duty outside the table (see
-- _role_held_outside_assignment). p_legacy_lock_key and p_bridge_pid are kept for
-- an instance on an older release and are not read: no lock decides a duty, and
-- no bridge session is recorded.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_VacantRole_GrantsEpochOne_WithALeaseInDatabaseTime_AndRecordsTheCapabilityAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_WhileAnotherBackendHoldsTheLegacySessionLock_AnswersLegacyHolderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_IgnoresAnAdvisoryLockOnTheLegacyKey_AndRecordsNoBridgeSessionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_ByANewerCaller_AsksTheOlderHolderToDrain_OnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_ForAVacantRole_DefersToALiveCandidateOnANewerVersionAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_vote_role(
  p_role TEXT,
  p_instance_id UUID,
  p_lease INTERVAL,
  p_cooldown INTERVAL,
  p_legacy_lock_key BIGINT,
  p_version_key INTEGER[],
  p_bridge_pid INTEGER
) RETURNS TABLE(
  outcome TEXT,
  holder_instance_id UUID,
  epoch BIGINT,
  lease_remaining INTERVAL,
  previous_holder_instance_id UUID,
  void_reason TEXT
)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
#variable_conflict use_column
DECLARE
  v_row __SCHEMA__.wh_role_assignments%ROWTYPE;
  v_void TEXT;
  v_epoch BIGINT;
  v_fleet_wide BOOLEAN;
  v_key INTEGER[] := COALESCE(p_version_key, '{}');
BEGIN
  IF p_lease IS NULL OR p_lease <= INTERVAL '0' THEN
    RAISE EXCEPTION 'wh_vote_role: the lease must be positive, got %', p_lease USING ERRCODE = '22023';
  END IF;

  -- The caller first: an evicted or unknown instance is told so, whoever holds the role.
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = p_instance_id)
     OR NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances s WHERE s.instance_id = p_instance_id) THEN
    RETURN QUERY SELECT 'refused'::TEXT, NULL::UUID, 0::BIGINT, NULL::INTERVAL, NULL::UUID, NULL::TEXT;
    RETURN;
  END IF;

  -- Candidacy, written only when stale or when the version changed, so a poller mostly reads.
  INSERT INTO __SCHEMA__.wh_role_candidates AS c (role, instance_id, version_key, last_voted_at)
  VALUES (p_role, p_instance_id, v_key, now())
  ON CONFLICT (role, instance_id) DO UPDATE
     SET version_key = EXCLUDED.version_key, last_voted_at = EXCLUDED.last_voted_at
   WHERE c.last_voted_at < now() - p_lease / 2 OR c.version_key IS DISTINCT FROM EXCLUDED.version_key;

  -- Fast path: a live holder other than the caller answers without the vote lock.
  SELECT * INTO v_row FROM __SCHEMA__.wh_role_assignments r WHERE r.role = p_role;
  IF FOUND
     AND v_row.holder_instance_id IS NOT NULL
     AND v_row.holder_instance_id <> p_instance_id
     AND __SCHEMA__._role_lapse_reason(v_row) IS NULL THEN
    -- Cooperative drain: a newer caller asks an older holder to finish its step and release.
    IF v_key > COALESCE(v_row.holder_version_key, '{}') AND v_row.drain_requested_at IS NULL THEN
      UPDATE __SCHEMA__.wh_role_assignments r
         SET drain_requested_at = now(), drain_requested_by = p_instance_id
       WHERE r.role = p_role AND r.holder_instance_id = v_row.holder_instance_id
         AND r.epoch = v_row.epoch AND r.drain_requested_at IS NULL;
      v_row.drain_requested_at := now();
    END IF;
    RETURN QUERY SELECT CASE WHEN v_row.drain_requested_at IS NULL THEN 'contended' ELSE 'draining' END::TEXT,
      v_row.holder_instance_id, v_row.epoch, v_row.lease_expires_at - now(), NULL::UUID, NULL::TEXT;
    RETURN;
  END IF;

  -- The vote. Held until this statement's transaction commits, which is milliseconds away.
  PERFORM pg_advisory_xact_lock(__SCHEMA__._role_vote_lock_key(p_role));

  INSERT INTO __SCHEMA__.wh_role_assignments (role) VALUES (p_role) ON CONFLICT (role) DO NOTHING;
  -- FOR UPDATE as well as the vote lock: wh_assert_role_epoch takes FOR SHARE, so a vote cannot
  -- reassign the role between a fenced writer's check and its commit.
  SELECT * INTO v_row FROM __SCHEMA__.wh_role_assignments r WHERE r.role = p_role FOR UPDATE;

  IF v_row.holder_instance_id IS NOT NULL THEN
    v_void := __SCHEMA__._role_lapse_reason(v_row);
    IF v_void IS NULL THEN
      IF v_row.holder_instance_id = p_instance_id THEN
        UPDATE __SCHEMA__.wh_role_assignments r
           SET renewed_at = now(), lease_expires_at = now() + p_lease, lease = p_lease, holder_version_key = v_key
         WHERE r.role = p_role;
        RETURN QUERY SELECT 'held'::TEXT, p_instance_id, v_row.epoch, p_lease, NULL::UUID, NULL::TEXT;
        RETURN;
      END IF;
      -- Another vote won between the fast path and the lock.
      RETURN QUERY SELECT 'contended'::TEXT, v_row.holder_instance_id, v_row.epoch,
        v_row.lease_expires_at - now(), NULL::UUID, NULL::TEXT;
      RETURN;
    END IF;

    -- Void it. Committed even when this caller is refused below: the vacancy is a fact.
    UPDATE __SCHEMA__.wh_role_assignments r
       SET holder_instance_id = NULL,
           lease_expires_at = NULL,
           drain_requested_at = NULL,
           drain_requested_by = NULL,
           duty_backend_pid = NULL,
           duty_backend_started_at = NULL,
           last_holder_instance_id = v_row.holder_instance_id,
           last_vacated_at = now(),
           last_vacated_reason = v_void,
           last_vacated_fleet_wide = v_void = 'lapsed' AND __SCHEMA__._role_lapse_is_fleet_wide(v_row)
     WHERE r.role = p_role
    RETURNING r.last_vacated_fleet_wide INTO v_fleet_wide;
    v_row.last_vacated_fleet_wide := v_fleet_wide;
    PERFORM __SCHEMA__.release_capability(v_row.holder_instance_id, p_role);
    v_row.last_holder_instance_id := v_row.holder_instance_id;
    v_row.last_vacated_at := now();
    v_row.last_vacated_reason := v_void;
  END IF;

  -- Hysteresis: an instance whose assignment lapsed may not win it back for p_cooldown, so a slow
  -- instance cannot bounce the role. A release is not a health signal and carries no cool-down, and
  -- neither does a fleet-wide lapse: the database was at fault, so duties resume as soon as it is back.
  IF v_row.last_holder_instance_id = p_instance_id
     AND v_row.last_vacated_reason = 'lapsed'
     AND NOT v_row.last_vacated_fleet_wide
     AND v_row.last_vacated_at + COALESCE(p_cooldown, INTERVAL '0') > now() THEN
    RETURN QUERY SELECT 'cooling_down'::TEXT, NULL::UUID, v_row.epoch, NULL::INTERVAL,
      v_row.last_holder_instance_id, v_row.last_vacated_reason;
    RETURN;
  END IF;

  -- Mixed-version fleets: a live instance on an older release holds this duty outside the table, by its own
  -- session lock, and recorded the holding (#2044). No lock is read: the record, and the one liveness rule, decide.
  IF __SCHEMA__._role_held_outside_assignment(p_role, p_instance_id) THEN
    RETURN QUERY SELECT 'legacy_holder'::TEXT, NULL::UUID, v_row.epoch, NULL::INTERVAL, NULL::UUID, NULL::TEXT;
    RETURN;
  END IF;

  -- Candidates that left are forgotten here, under the vote lock, so the table stays the fleet's size.
  DELETE FROM __SCHEMA__.wh_role_candidates c
   WHERE c.role = p_role
     AND (NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances s WHERE s.instance_id = c.instance_id)
          OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = c.instance_id));

  -- The newest library version is preferred: a live candidate on a newer version that could win
  -- (it is not cooling down) gets the role instead. Among equals, the first valid caller wins.
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_role_candidates c
     WHERE c.role = p_role
       AND c.instance_id <> p_instance_id
       AND c.version_key > v_key
       AND __SCHEMA__._role_candidate_live(c.last_voted_at, c.instance_id, p_lease)
       AND NOT (c.instance_id = v_row.last_holder_instance_id
                AND v_row.last_vacated_reason = 'lapsed'
                AND NOT v_row.last_vacated_fleet_wide
                AND v_row.last_vacated_at + COALESCE(p_cooldown, INTERVAL '0') > now())) THEN
    RETURN QUERY SELECT 'deferred'::TEXT, NULL::UUID, v_row.epoch, NULL::INTERVAL, NULL::UUID, NULL::TEXT;
    RETURN;
  END IF;

  UPDATE __SCHEMA__.wh_role_assignments r
     SET holder_instance_id = p_instance_id,
         epoch = r.epoch + 1,
         assigned_at = now(),
         renewed_at = now(),
         lease_expires_at = now() + p_lease,
         lease = p_lease,
         election_count = r.election_count + 1,
         holder_version_key = v_key,
         drain_requested_at = NULL,
         drain_requested_by = NULL,
         duty_backend_pid = NULL,
         duty_backend_started_at = NULL,
         bridge_backend_pid = NULL,
         bridge_backend_started_at = NULL
   WHERE r.role = p_role
  RETURNING r.epoch INTO v_epoch;
  PERFORM __SCHEMA__.record_capability(p_instance_id, p_role);

  RETURN QUERY SELECT 'granted'::TEXT, p_instance_id, v_epoch, p_lease,
    v_row.last_holder_instance_id, v_row.last_vacated_reason;
END;
$$;

-- ============================================================================
-- wh_renew_role_lease: extend the caller's lease (see 184). The holder steps aside
-- ('lost', vacated as legacy_holder) once a live instance holds the duty outside
-- the table. p_legacy_lock_key is kept for an older caller and is not read.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:RenewLease_AnswersDrain_AfterANewerCallerAskedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:RenewLease_StepsAside_WhenALegacyHolderTookTheLockAfterTheVoteAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_renew_role_lease(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_legacy_lock_key BIGINT DEFAULT NULL
) RETURNS TEXT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_drain BOOLEAN;
BEGIN
  IF __SCHEMA__._role_held_outside_assignment(p_role, p_instance_id) THEN
    UPDATE __SCHEMA__.wh_role_assignments r
       SET holder_instance_id = NULL,
           lease_expires_at = NULL,
           drain_requested_at = NULL,
           drain_requested_by = NULL,
           duty_backend_pid = NULL,
           duty_backend_started_at = NULL,
           last_holder_instance_id = p_instance_id,
           last_vacated_at = now(),
           last_vacated_reason = 'legacy_holder',
           last_vacated_fleet_wide = FALSE
     WHERE r.role = p_role AND r.holder_instance_id = p_instance_id AND r.epoch = p_epoch;
    IF FOUND THEN
      PERFORM __SCHEMA__.release_capability(p_instance_id, p_role);
    END IF;
    RETURN 'lost';
  END IF;

  UPDATE __SCHEMA__.wh_role_assignments r
     SET renewed_at = now(), lease_expires_at = now() + r.lease
   WHERE r.role = p_role
     AND r.holder_instance_id = p_instance_id
     AND r.epoch = p_epoch
     AND __SCHEMA__._role_lapse_reason(r) IS NULL
  RETURNING r.drain_requested_at IS NOT NULL INTO v_drain;
  IF NOT FOUND THEN
    RETURN 'lost';
  END IF;
  RETURN CASE WHEN v_drain THEN 'drain' ELSE 'renewed' END;
END;
$$;

-- ============================================================================
-- wh_end_lapsed_bridge: kept for a bridged instance on an older release, which
-- calls it when it cannot take its legacy lock. There is no bridge: no duty is
-- held by a session, so no session is ended, and the answer is always NULL.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:EndLapsedBridge_EndsOnlyTheLapsedHoldersLockSession_AndOnlyOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleFenceWaitSqlTests.cs:EndLapsedBridge_ALapsedBridgedHolder_IsStillFoundAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_end_lapsed_bridge(
  p_role TEXT,
  p_legacy_lock_key BIGINT
) RETURNS INTEGER
LANGUAGE sql
STABLE
AS $$
  SELECT NULL::INTEGER
$$;

-- No role function reads an advisory lock any more.
DROP FUNCTION IF EXISTS __SCHEMA__._advisory_lock_held_elsewhere(BIGINT);

-- @whizbang:bootstrap-end

-- ============================================================================
-- wh_vote_roles: several votes in one statement (see 184). p_legacy_lock_keys is
-- kept for an older caller and is not read; NULL is accepted.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:VoteRoles_VotesForEveryRoleInOneStatement_InRoleOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:VoteRoles_TakesNoLegacyKeysAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_vote_roles(
  p_roles TEXT[],
  p_leases INTERVAL[],
  p_instance_id UUID,
  p_cooldown INTERVAL,
  p_legacy_lock_keys BIGINT[],
  p_version_key INTEGER[]
) RETURNS TABLE(
  role TEXT,
  outcome TEXT,
  holder_instance_id UUID,
  epoch BIGINT,
  lease_remaining INTERVAL,
  previous_holder_instance_id UUID,
  void_reason TEXT
)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_i INTEGER;
BEGIN
  IF cardinality(p_roles) <> cardinality(p_leases) THEN
    RAISE EXCEPTION 'wh_vote_roles: roles and leases must pair up' USING ERRCODE = '22023';
  END IF;
  FOR v_i IN SELECT u.ord FROM unnest(p_roles) WITH ORDINALITY AS u(name, ord) ORDER BY u.name LOOP
    RETURN QUERY SELECT p_roles[v_i], v.outcome, v.holder_instance_id, v.epoch, v.lease_remaining,
                        v.previous_holder_instance_id, v.void_reason
      FROM __SCHEMA__.wh_vote_role(p_roles[v_i], p_instance_id, p_leases[v_i], p_cooldown,
                                   NULL, p_version_key, NULL) v;
  END LOOP;
END;
$$;
