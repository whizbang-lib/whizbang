-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 615_ALapseNeedsASuccessor.sql
-- Date: 2026-11-25
-- Description: A role lapses only when another live instance can take it (#2593, phase 1 of #1328).
--
--              A holder past its lease and its progress window was voided as lapsed, and the vote then answered
--              cooling_down to that same instance, although no other instance could take the role (#1327: a single
--              instance lost the commit-stamper role to its own lease four times in two minutes). The verdict "the
--              holder lapsed" only matters when someone else could hold the role, so it is now given only then.
--
--              * _role_has_successor: whether a live candidate for the role other than the holder exists. Live is a
--                candidacy no older than the role's lease, not tombstoned, and the one liveness rule
--                (wh_instance_is_live), so a peer that stopped voting, or that the rule calls dead, does not count.
--              * _role_lapse_reason: the lapsed verdict also needs a successor, or a holder the rule calls dead. A live
--                sole holder keeps its role and its epoch past its lease and its window; evicted and unregistered are
--                unchanged. It is asked last, inside the lapsed branch, so a holder inside its lease reads nothing new.
--              * wh_assert_role_epoch: the fence accepts that sole holder's writes, as the vote does: there is no other
--                writer.
--              * wh_vote_role: cooling_down is answered only while a peer that could take the role exists, so a holder
--                voided while a peer existed, whose peer then left, wins the role straight back.
--              * wh_role_assignment_status gains stalled_without_successor: a holder kept only because no peer can take
--                its role, which the roles health component reports Degraded. A healthy sole holder is not stalled.
--
--              Mixed versions: every release calls these functions, so the rule applies to old and new instances at
--              once. It only extends a holding when no successor exists; it never shortens one. A peer's vote writes
--              its candidacy before it reads the assignment, so in a fleet of two or more a stuck holder is taken over
--              exactly as before.
--
-- Reversal: reversible (the down file restores the last word of each function before 615 and drops
--           _role_has_successor)
-- Dependencies: 184 (wh_role_candidates, wh_role_assignment_status), 316 (wh_instance_is_live), 326 (wh_vote_role),
--               355 (_role_holder_kept_live, _role_lapse_reason, wh_assert_role_epoch)
-- Objects: _role_has_successor, _role_lapse_reason, wh_assert_role_epoch, wh_vote_role, wh_role_assignment_status
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the migrator duty is won by the vote, which judges its holder through these functions.

-- ============================================================================
-- _role_has_successor: whether a live candidate for p_role other than p_holder
-- exists at p_at: it voted within p_lease (the role's lease), it is not
-- tombstoned, and the one liveness rule calls it live (which also needs its
-- registration row).
--
-- <docs>operations/startup/capabilities-and-duties#a-lapse-needs-a-successor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_ALiveSoleHolderPastItsLeaseAndWindow_KeepsTheRoleAtItsEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_APeerThatVotes_TakesOverAStuckHolder_AtTheNextEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Successor_APeerTheRuleCallsDead_ATombstonedPeer_AndAStaleCandidacy_DoNotCountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:CoolDown_AHolderWhosePeerLeft_WinsTheRoleStraightBackAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_has_successor(p_role TEXT, p_holder UUID, p_lease INTERVAL, p_at TIMESTAMPTZ)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT EXISTS (
    SELECT 1
      FROM __SCHEMA__.wh_role_candidates c
     WHERE c.role = p_role
       AND c.instance_id IS DISTINCT FROM p_holder
       AND c.last_voted_at + p_lease > p_at
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = c.instance_id)
       AND __SCHEMA__.wh_instance_is_live(c.instance_id))
$$;

COMMENT ON FUNCTION __SCHEMA__._role_has_successor(TEXT, UUID, INTERVAL, TIMESTAMPTZ) IS
  '615 (#2593): whether a live candidate other than the holder could take the role: it voted within the role''s lease, '
  'it is not tombstoned, and the one liveness rule calls it live. A lapse, and a cool-down, need one.';

-- ============================================================================
-- _role_lapse_reason: why the assignment in p_row is no longer valid, or NULL
-- while it is (see 184, 355). Tombstone first, then registration, then the
-- lease, which the marked duty backend's activity holds open, past which the
-- holder is judged by the one liveness rule and its progress window. A holder
-- past all of those lapses only when the rule calls it dead or a successor
-- exists: a live sole holder keeps its role (615).
--
-- <docs>operations/startup/capabilities-and-duties#a-lapse-needs-a-successor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_ALiveSoleHolderPastItsLeaseAndWindow_KeepsTheRoleAtItsEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_APeerThatVotes_TakesOverAStuckHolder_AtTheNextEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_ASoleHolderTheRuleCallsDead_LapsesAtItsLease_AndANewInstanceWinsWithNoCoolDownAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_ATombstonedSoleHolder_IsStillEvictedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleHolderLivenessSqlTests.cs:Vote_AConnectedButStuckHolder_LapsesAtItsProgressWindow_ThoughTheRuleCallsItLiveAsync</tests>
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
         AND NOT __SCHEMA__._role_holder_kept_live(p_row, now()) THEN
      -- A nested CASE, so the successor is asked only here, after every cheaper check said the holder is past them.
      CASE
        WHEN __SCHEMA__.wh_instance_is_live(p_row.holder_instance_id)
             AND NOT __SCHEMA__._role_has_successor(p_row.role, p_row.holder_instance_id, p_row.lease, now()) THEN NULL
        ELSE 'lapsed'
      END
    ELSE NULL
  END
$$;

-- ============================================================================
-- wh_assert_role_epoch: the fence (see 184, 355). Raises WHF01 unless (instance,
-- epoch) is the current, non-tombstoned assignment whose lease is unexpired,
-- whose marked duty backend is running a statement, whose holder the one
-- liveness rule keeps inside its progress window, or whose live holder has no
-- successor (615): the vote treats all four as held, so the fence does too.
-- The checks are a CASE so they run in that order and the successor is asked
-- last. FOR SHARE holds the row until the caller commits. clock_timestamp(): a
-- fenced transaction may have started long before this call.
--
-- <docs>operations/startup/capabilities-and-duties#a-lapse-needs-a-successor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_ALiveSoleHolderPastItsLeaseAndWindow_KeepsTheRoleAtItsEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Lapse_APeerThatVotes_TakesOverAStuckHolder_AtTheNextEpochAsync</tests>
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
      AND CASE
            WHEN r.lease_expires_at > clock_timestamp() THEN TRUE
            WHEN __SCHEMA__._role_backend_active(r.duty_backend_pid, r.duty_backend_started_at) THEN TRUE
            WHEN __SCHEMA__._role_holder_kept_live(r, clock_timestamp()) THEN TRUE
            ELSE __SCHEMA__.wh_instance_is_live(r.holder_instance_id)
                 AND NOT __SCHEMA__._role_has_successor(r.role, r.holder_instance_id, r.lease, clock_timestamp())
          END
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
-- wh_vote_role: the vote (see 184, 326). Unchanged but for the cool-down: an
-- instance whose assignment lapsed is answered cooling_down only while another
-- live candidate could take the role (615); with none, it wins it straight back.
--
-- <docs>operations/startup/capabilities-and-duties#a-lapse-needs-a-successor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:CoolDown_AHolderWhosePeerLeft_WinsTheRoleStraightBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:CoolDown_WithALivePeer_IsAnsweredAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_VacantRole_GrantsEpochOne_WithALeaseInDatabaseTime_AndRecordsTheCapabilityAsync</tests>
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
     AND v_row.last_vacated_at + COALESCE(p_cooldown, INTERVAL '0') > now()
     -- 615 (#2593): the cool-down keeps the role for a peer, so it holds only while one can take it.
     AND __SCHEMA__._role_has_successor(p_role, p_instance_id, p_lease, now()) THEN
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

-- @whizbang:bootstrap-end

-- The return type gains stalled_without_successor, so the earlier definition goes first.
SELECT __SCHEMA__.drop_all_overloads('wh_role_assignment_status');

-- ============================================================================
-- wh_role_assignment_status: one row per role, for health, metrics and the
-- operator (see 184). state is held, lapsed or vacant. stalled_without_successor
-- (615) is a held role whose holder is past its lease, its marked backend's
-- statement and its progress window, and keeps the role only because no other
-- live candidate can take it: the roles health component reports it Degraded.
-- The earlier columns keep their names and order.
--
-- <docs>operations/startup/capabilities-and-duties#a-lapse-needs-a-successor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Status_ReportsAStalledSoleHolder_AsHeld_AndStalledWithoutSuccessorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleLapseNeedsASuccessorSqlTests.cs:Status_AHealthySoleHolder_IsNotStalledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Status_ReportsHeldLapsedAndVacant_WithEpochAndElectionCountAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_role_assignment_status()
RETURNS TABLE(
  role TEXT,
  state TEXT,
  holder_instance_id UUID,
  epoch BIGINT,
  assigned_at TIMESTAMPTZ,
  renewed_at TIMESTAMPTZ,
  lease_expires_at TIMESTAMPTZ,
  lease_remaining INTERVAL,
  election_count BIGINT,
  void_reason TEXT,
  last_holder_instance_id UUID,
  last_vacated_at TIMESTAMPTZ,
  last_vacated_reason TEXT,
  pending_work BIGINT,
  drain_requested_at TIMESTAMPTZ,
  duty_backend_active BOOLEAN,
  stalled_without_successor BOOLEAN
)
LANGUAGE sql
STABLE
AS $$
  SELECT a.role,
         CASE
           WHEN a.holder_instance_id IS NULL THEN 'vacant'
           WHEN a.lapse IS NULL THEN 'held'
           ELSE 'lapsed'
         END,
         a.holder_instance_id,
         a.epoch,
         a.assigned_at,
         a.renewed_at,
         a.lease_expires_at,
         CASE WHEN a.holder_instance_id IS NULL OR a.lapse IS NOT NULL THEN NULL
              ELSE a.lease_expires_at - now() END,
         a.election_count,
         a.lapse,
         a.last_holder_instance_id,
         a.last_vacated_at,
         a.last_vacated_reason,
         (SELECT count(*) FROM __SCHEMA__.wh_role_pending_work w WHERE w.role = a.role),
         a.drain_requested_at,
         a.backend_active,
         -- Held, and past everything that keeps a holder but the missing successor.
         a.holder_instance_id IS NOT NULL
           AND a.lapse IS NULL
           AND (a.lease_expires_at IS NULL OR a.lease_expires_at <= now())
           AND NOT a.backend_active
           AND NOT __SCHEMA__._role_holder_kept_live(a.r, now())
    FROM (
      SELECT r.*,
             r AS r,
             CASE WHEN r.holder_instance_id IS NULL THEN NULL
                  ELSE __SCHEMA__._role_lapse_reason(r) END AS lapse,
             __SCHEMA__._role_backend_active(r.duty_backend_pid, r.duty_backend_started_at) AS backend_active
        FROM __SCHEMA__.wh_role_assignments r
    ) a
   ORDER BY a.role
$$;
