-- Migration: 184_RoleAssignmentResilience.sql
-- Date: 2026-10-01
-- Description: Duty role assignment, phases 3 and 4 (issue #966), with the decisions recorded on #968.
--              This file is the last word on every role-assignment function; 173 is where they
--              started, and the redefinition closure re-runs this file whenever 173 re-runs.
--
--              * The migrator is held by assignment. It is elected BEFORE migrations run, so the
--                whole file is part of the bootstrap closure: the vote, renewal, release and fence
--                must exist before the first migration does.
--              * Each duty declares its own lease (the vote is passed it), and the vote also
--                treats a holder as live while the backend it marked for its duty is running a
--                statement (pg_stat_activity state 'active'), as a backstop for one long
--                statement that outlasts the lease.
--              * Cooperative drain: a caller on a newer library version than a live holder asks it
--                to finish its current step and release. The holder learns it on its next renewal.
--              * A vacant role prefers the newest library version: a caller is deferred while a
--                live candidate on a newer version is voting for the same role. Among equals, the
--                first valid caller wins, as before.
--              * A stuck holder while bridged: once the row says the assignment has lapsed, a
--                would-be winner may terminate that holder's legacy-lock session, and only that
--                session (matched by pid and backend start), which is what lets anyone vote again.
--              * A fleet-wide lapse (every holder lapsed together: the database was out)
--                skips the flapping cool-down, so duties resume as soon as the database is back.
--              * Multi-role vote: wh_vote_roles votes for several roles in one statement.
--
--              The phase 1 entry points keep their signatures (wh_elect_role, wh_renew_role) as thin
--              wrappers, so an instance on the release before this one keeps working during a
--              rolling deploy. It never drains, and its version counts as the oldest.
-- Dependencies: 000 (drop_all_overloads)
--               010 (wh_service_instances)
--               106 (wh_instance_evictions)
--               108 (record_capability, release_capability)
--               173 (wh_role_assignments, wh_role_pending_work, _advisory_lock_held_elsewhere)

-- @whizbang:bootstrap-begin
-- Bootstrap: the migrator duty is won by this vote, and the migrator is chosen before migrations run.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_role_assignments (
  role                     TEXT        NOT NULL PRIMARY KEY,
  holder_instance_id       UUID,
  epoch                    BIGINT      NOT NULL DEFAULT 0,
  assigned_at              TIMESTAMPTZ,
  renewed_at               TIMESTAMPTZ,
  lease_expires_at         TIMESTAMPTZ,
  lease                    INTERVAL,
  election_count           BIGINT      NOT NULL DEFAULT 0,
  last_holder_instance_id  UUID,
  last_vacated_at          TIMESTAMPTZ,
  last_vacated_reason      TEXT
);

ALTER TABLE __SCHEMA__.wh_role_assignments
  -- The holder's library version as a sortable key (see LibraryVersionKey): what the newest-version
  -- preference and the cooperative drain compare. NULL for a holder on the release before this one.
  ADD COLUMN IF NOT EXISTS holder_version_key        INTEGER[],
  -- Set by a newer caller; the holder learns it on its next renewal, finishes its step and releases.
  ADD COLUMN IF NOT EXISTS drain_requested_at        TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS drain_requested_by        UUID,
  -- The backend the holder marked for its duty's current statement. While that exact backend (pid and
  -- backend start) is running a statement, the assignment does not lapse, whatever the lease says.
  ADD COLUMN IF NOT EXISTS duty_backend_pid          INTEGER,
  ADD COLUMN IF NOT EXISTS duty_backend_started_at   TIMESTAMPTZ,
  -- The holder's legacy-lock session while bridged. Kept after an involuntary void, so a would-be
  -- winner can terminate exactly that session; cleared by a release and replaced by the next grant.
  ADD COLUMN IF NOT EXISTS bridge_backend_pid        INTEGER,
  ADD COLUMN IF NOT EXISTS bridge_backend_started_at TIMESTAMPTZ,
  -- Whether the last involuntary vacancy was fleet-wide (see _role_lapse_is_fleet_wide). A fleet-wide
  -- lapse carries no cool-down: the database was at fault, not the holder.
  ADD COLUMN IF NOT EXISTS last_vacated_fleet_wide   BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_role_candidates (
  role           TEXT        NOT NULL,
  instance_id    UUID        NOT NULL,
  version_key    INTEGER[]   NOT NULL DEFAULT '{}',
  last_voted_at  TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (role, instance_id)
);

COMMENT ON TABLE __SCHEMA__.wh_role_candidates IS
'Who is voting for each role, and on which library version. A vacant role is not granted to a caller while
a live candidate on a newer version is voting for it, so a rolling deploy hands roles to the new release.
A candidate is live while it voted within the lease and is registered and not tombstoned.';

-- ============================================================================
-- _role_vote_lock_key — the transaction advisory-lock key for one role's vote,
-- scoped to this schema by the oid of its wh_role_assignments (see 173, #962).
-- Redefined here, unchanged, because the bootstrap needs it before 173 runs.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:VoteLockKey_CarriesThisSchemasTableOid_SoItIsSchemaScopedByConstructionAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_vote_lock_key(p_role TEXT)
RETURNS BIGINT
LANGUAGE sql
STABLE
AS $$
  SELECT ((('__SCHEMA__.wh_role_assignments'::regclass)::oid::bigint) << 32)
       | (hashtext(p_role)::bigint & 4294967295)
$$;

-- ============================================================================
-- _advisory_lock_held_elsewhere — whether a backend other than this one holds the
-- single-bigint advisory lock p_key in this database (see 173). Redefined here,
-- unchanged, because the bootstrap needs it before 173 runs.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_WhileAnotherBackendHoldsTheLegacySessionLock_AnswersLegacyHolderAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._advisory_lock_held_elsewhere(p_key BIGINT)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT EXISTS (
    SELECT 1
      FROM pg_locks l
     WHERE l.locktype = 'advisory'
       AND l.granted
       AND l.objsubid = 1
       AND l.database = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database())
       AND ((l.classid::bigint << 32) | (l.objid::bigint & 4294967295)) = p_key
       AND l.pid <> pg_backend_pid())
$$;

-- ============================================================================
-- _role_backend_active — whether that exact backend (pid AND backend start, so a
-- reused pid never counts) is running a statement in this database right now.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_TreatsAnExpiredHolderAsLive_WhileItsMarkedDutyBackendRunsAStatementAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_backend_active(p_pid INTEGER, p_started_at TIMESTAMPTZ)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT p_pid IS NOT NULL AND EXISTS (
    SELECT 1
      FROM pg_stat_activity a
     WHERE a.pid = p_pid
       AND a.backend_start = p_started_at
       AND a.state = 'active'
       AND a.datname = current_database())
$$;

-- ============================================================================
-- _role_lapse_reason — why the assignment in p_row is no longer valid, or NULL
-- while it is. Tombstone first (the fleet's verdict), then registration, then the
-- lease, which the marked duty backend's activity holds open.
--
-- <docs>proposals/duty-role-assignment</docs>
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
         AND NOT __SCHEMA__._role_backend_active(p_row.duty_backend_pid, p_row.duty_backend_started_at) THEN 'lapsed'
    ELSE NULL
  END
$$;

-- ============================================================================
-- _role_lapse_is_fleet_wide — whether the lapse of the assignment in p_row was
-- the database's fault rather than its holder's, so it carries no cool-down.
--
-- The holder failed to renew between its last renewal and its lease expiry (the
-- window). The lapse is fleet-wide when other assignments were held at the
-- window's start (still held since then, or vacated after it, which includes one
-- voided and re-granted as the fleet recovers) and none of them shows the database was reachable during it:
-- none was held through it, and none was granted or vacated inside it. Only an
-- assignment whose lease is no longer than the window can testify to being held
-- through it, since a longer lease survives an outage without a renewal. With no
-- other assignment to bear witness, a lapse is the holder's own and the cool-down
-- applies. The database's clock alone decides, like every other bound here.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_AfterEveryHolderLapsedTogether_SkipsTheCooldownAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_AfterOneHolderLapsedWhileAnotherKeptRenewing_KeepsTheCooldownAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_lapse_is_fleet_wide(p_row __SCHEMA__.wh_role_assignments)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_role_assignments o
             WHERE o.role <> p_row.role
               AND o.lease <= p_row.lease
               AND ((o.holder_instance_id IS NOT NULL AND o.assigned_at <= p_row.renewed_at)
                    OR o.last_vacated_at > p_row.renewed_at))
     AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_role_assignments o
             WHERE o.role <> p_row.role
               AND ((o.holder_instance_id IS NOT NULL
                     AND o.lease <= p_row.lease
                     AND o.assigned_at <= p_row.renewed_at
                     AND __SCHEMA__._role_lapse_reason(o) IS NULL)
                    OR o.assigned_at > p_row.renewed_at AND o.assigned_at <= p_row.lease_expires_at
                    OR o.last_vacated_at > p_row.renewed_at AND o.last_vacated_at <= p_row.lease_expires_at))
$$;

-- ============================================================================
-- _role_candidate_live — whether p_instance is a live candidate for p_role: it
-- voted within p_lease, is registered, and is not tombstoned.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_ForAVacantRole_DefersToALiveCandidateOnANewerVersionAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_candidate_live(p_last_voted_at TIMESTAMPTZ, p_instance_id UUID, p_lease INTERVAL)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT p_last_voted_at + p_lease > now()
     AND EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances s WHERE s.instance_id = p_instance_id)
     AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = p_instance_id)
$$;

-- ============================================================================
-- wh_vote_role — the vote.
--
-- outcome:
--   granted        the caller is the new holder, at a new epoch
--   held           the caller already held a live assignment; its lease is renewed, same epoch
--   contended      another instance holds a live assignment
--   draining       another instance holds it on an older version, and has been asked to drain
--   deferred       the role is free, but a live candidate on a newer version is voting for it
--   cooling_down   the caller's own assignment lapsed within p_cooldown; it may not win it back yet
--   legacy_holder  another backend holds p_legacy_lock_key (an instance on the session-lock elector)
--   refused        the caller is tombstoned or not registered
--
-- p_lease is the lease this role is held for (each duty declares its own). p_version_key is the
-- caller's library version as a sortable key; empty or NULL sorts below every version. p_bridge_pid is
-- the caller's legacy-lock session while bridged, recorded on a grant so a later winner can end exactly
-- that session if this holder lapses while holding it.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_VacantRole_GrantsEpochOne_WithALeaseInDatabaseTime_AndRecordsTheCapabilityAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_ByANewerCaller_AsksTheOlderHolderToDrain_OnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_ForAVacantRole_DefersToALiveCandidateOnANewerVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_GrantsEachRoleItsOwnDeclaredLeaseAsync</tests>
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

    -- Void it. Committed even when this caller is refused below: the vacancy is a fact. The bridge
    -- session is kept, so a winner can still end it if it is stuck holding the legacy lock.
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

  -- Mixed-version fleets: an instance still on the session-lock elector holds this duty's lock.
  IF p_legacy_lock_key IS NOT NULL AND __SCHEMA__._advisory_lock_held_elsewhere(p_legacy_lock_key) THEN
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
         bridge_backend_pid = p_bridge_pid,
         bridge_backend_started_at = (SELECT a.backend_start FROM pg_stat_activity a WHERE a.pid = p_bridge_pid)
   WHERE r.role = p_role
  RETURNING r.epoch INTO v_epoch;
  PERFORM __SCHEMA__.record_capability(p_instance_id, p_role);

  RETURN QUERY SELECT 'granted'::TEXT, p_instance_id, v_epoch, p_lease,
    v_row.last_holder_instance_id, v_row.last_vacated_reason;
END;
$$;

-- ============================================================================
-- wh_elect_role — the phase 1 vote, kept for an instance on the release before
-- this one: no version (so it counts as the oldest) and no bridge session. The
-- new outcomes read as contended to it, which is what they mean to a caller that
-- can only wait.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_AfterTheHoldersLeaseLapses_VoidsIt_AndGrantsTheNextEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:PhaseOneEntryPoints_StillWork_AndReadNewOutcomesAsContendedAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_elect_role(
  p_role TEXT,
  p_instance_id UUID,
  p_lease INTERVAL,
  p_cooldown INTERVAL,
  p_legacy_lock_key BIGINT DEFAULT NULL
) RETURNS TABLE(
  outcome TEXT,
  holder_instance_id UUID,
  epoch BIGINT,
  lease_remaining INTERVAL,
  previous_holder_instance_id UUID,
  void_reason TEXT
)
LANGUAGE sql
AS $$
  SELECT CASE WHEN v.outcome IN ('draining', 'deferred') THEN 'contended' ELSE v.outcome END,
         v.holder_instance_id, v.epoch, v.lease_remaining, v.previous_holder_instance_id, v.void_reason
    FROM __SCHEMA__.wh_vote_role(p_role, p_instance_id, p_lease, p_cooldown, p_legacy_lock_key, NULL, NULL) v
$$;

-- ============================================================================
-- wh_renew_role_lease — extend the caller's lease from now(), by the lease it was
-- granted. Answers 'renewed', 'drain' (renewed, and a newer instance asked for the
-- role: finish the current step and release) or 'lost'.
--
-- p_legacy_lock_key is passed by an unbridged holder: an instance on the
-- session-lock elector that took the duty's lock after the vote (an unsafe mixed
-- fleet) is seen here, and the assignment steps aside for it within one renewal.
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
  IF p_legacy_lock_key IS NOT NULL AND __SCHEMA__._advisory_lock_held_elsewhere(p_legacy_lock_key) THEN
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
-- wh_renew_role — the phase 1 renewal, kept for an instance on the release before
-- this one. It cannot drain, so a drain request reads as renewed to it.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Renew_ExtendsOnlyTheCurrentLiveAssignmentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:PhaseOneEntryPoints_StillWork_AndReadNewOutcomesAsContendedAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_renew_role(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS BOOLEAN
LANGUAGE sql
AS $$
  SELECT __SCHEMA__.wh_renew_role_lease(p_role, p_instance_id, p_epoch, NULL) <> 'lost'
$$;

-- ============================================================================
-- wh_release_role — vacate the role on a graceful stop, only when (instance,
-- epoch) is the current assignment, so a stale grant cannot vacate its successor.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Release_VacatesAtOnce_WithNoCooldownForTheReleaserAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Release_NotifiesWaiters_SoTheyReVoteAtOnceAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_release_role(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_role_assignments r
     SET holder_instance_id = NULL,
         lease_expires_at = NULL,
         drain_requested_at = NULL,
         drain_requested_by = NULL,
         duty_backend_pid = NULL,
         duty_backend_started_at = NULL,
         bridge_backend_pid = NULL,
         bridge_backend_started_at = NULL,
         last_holder_instance_id = p_instance_id,
         last_vacated_at = now(),
         last_vacated_reason = 'released',
         last_vacated_fleet_wide = FALSE
   WHERE r.role = p_role
     AND r.holder_instance_id = p_instance_id
     AND r.epoch = p_epoch;
  IF NOT FOUND THEN
    RETURN FALSE;
  END IF;
  PERFORM __SCHEMA__.release_capability(p_instance_id, p_role);
  -- Delivered at commit; the payload is the role alone (see 173).
  PERFORM pg_notify('wh_role_released', p_role);
  RETURN TRUE;
END;
$$;

-- ============================================================================
-- wh_assert_role_epoch — the fence. Raises WHF01 unless (instance, epoch) is the
-- current, non-tombstoned assignment whose lease is unexpired or whose marked duty
-- backend is running a statement (the vote treats both as live, so the fence does
-- too). FOR SHARE holds the row until the caller commits. clock_timestamp(): a
-- fenced transaction may have started long before this call.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:AssertEpoch_WithAStaleEpoch_RaisesInSql_AndTheWriteBesideItRollsBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:AssertEpoch_RefusesTheRightEpochFromTheWrongHolder_ALapsedLease_AndAnUnknownRoleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_TreatsAnExpiredHolderAsLive_WhileItsMarkedDutyBackendRunsAStatementAsync</tests>
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
           OR __SCHEMA__._role_backend_active(r.duty_backend_pid, r.duty_backend_started_at))
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
-- wh_mark_role_duty_backend — the holder marks the backend this is called on as
-- its duty's backend, so a long statement run on it keeps the assignment live past
-- the lease (the backstop). p_mark false clears the mark, which the holder does as
-- soon as the statement ends so a pooled connection reused for other work never
-- counts. FALSE when (instance, epoch) is not the current live assignment.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_TreatsAnExpiredHolderAsLive_WhileItsMarkedDutyBackendRunsAStatementAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:MarkDutyBackend_IsRefusedToAStaleHolder_AndClearingItEndsTheBackstopAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_mark_role_duty_backend(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_mark BOOLEAN
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_role_assignments r
     SET duty_backend_pid = CASE WHEN p_mark THEN pg_backend_pid() END,
         duty_backend_started_at = CASE WHEN p_mark
           THEN (SELECT a.backend_start FROM pg_stat_activity a WHERE a.pid = pg_backend_pid()) END
   WHERE r.role = p_role
     AND r.holder_instance_id = p_instance_id
     AND r.epoch = p_epoch
     AND __SCHEMA__._role_lapse_reason(r) IS NULL;
  RETURN FOUND;
END;
$$;

-- ============================================================================
-- wh_end_lapsed_bridge — a bridged would-be winner could not take the legacy
-- lock. When the row says the holder that holds it has lapsed (its assignment is
-- void, or was voided involuntarily), end that holder's legacy-lock session, and
-- only that session: matched by pid AND backend start, and only while it holds
-- p_legacy_lock_key. Waits for the session to exit (so its lock is gone on return)
-- and answers the pid it ended, or NULL when there was nothing to end. Serialized
-- by the vote lock, so a fleet asking at once ends it once.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:EndLapsedBridge_EndsOnlyTheLapsedHoldersLockSession_AndOnlyOnceAsync</tests>
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

-- ============================================================================
-- wh_vote_roles — several votes in one statement, one per role, in role order so
-- two instances voting for overlapping sets take the vote locks in the same
-- order. p_leases pairs with p_roles; so does p_legacy_lock_keys (NULL entries
-- allowed). Not for a bridged caller, which must take each legacy lock first.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:VoteRoles_VotesForEveryRoleInOneStatement_InRoleOrderAsync</tests>
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
  IF cardinality(p_roles) <> cardinality(p_leases) OR cardinality(p_roles) <> cardinality(p_legacy_lock_keys) THEN
    RAISE EXCEPTION 'wh_vote_roles: roles, leases and legacy keys must pair up' USING ERRCODE = '22023';
  END IF;
  FOR v_i IN SELECT u.ord FROM unnest(p_roles) WITH ORDINALITY AS u(name, ord) ORDER BY u.name LOOP
    RETURN QUERY SELECT p_roles[v_i], v.outcome, v.holder_instance_id, v.epoch, v.lease_remaining,
                        v.previous_holder_instance_id, v.void_reason
      FROM __SCHEMA__.wh_vote_role(p_roles[v_i], p_instance_id, p_leases[v_i], p_cooldown,
                                   p_legacy_lock_keys[v_i], p_version_key, NULL) v;
  END LOOP;
END;
$$;

-- ============================================================================
-- wh_request_role_drain — what a bridged caller does when it could not take the
-- role's legacy lock: it records its candidacy, and if a live holder is on an older
-- version it asks that holder to drain, exactly as the vote's fast path would. It
-- never assigns anything. Answers 'draining' when a drain is pending, else
-- 'contended'.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceE2ETests.cs:ABridgedNewerInstance_StillAsksTheOlderHolderToDrainAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_request_role_drain(
  p_role TEXT,
  p_instance_id UUID,
  p_lease INTERVAL,
  p_version_key INTEGER[]
) RETURNS TEXT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_row __SCHEMA__.wh_role_assignments%ROWTYPE;
  v_key INTEGER[] := COALESCE(p_version_key, '{}');
BEGIN
  INSERT INTO __SCHEMA__.wh_role_candidates AS c (role, instance_id, version_key, last_voted_at)
  VALUES (p_role, p_instance_id, v_key, now())
  ON CONFLICT (role, instance_id) DO UPDATE
     SET version_key = EXCLUDED.version_key, last_voted_at = EXCLUDED.last_voted_at
   WHERE c.last_voted_at < now() - p_lease / 2 OR c.version_key IS DISTINCT FROM EXCLUDED.version_key;

  SELECT * INTO v_row FROM __SCHEMA__.wh_role_assignments r WHERE r.role = p_role;
  IF NOT FOUND OR v_row.holder_instance_id IS NULL OR v_row.holder_instance_id = p_instance_id
     OR __SCHEMA__._role_lapse_reason(v_row) IS NOT NULL THEN
    RETURN 'contended';
  END IF;
  IF v_key > COALESCE(v_row.holder_version_key, '{}') AND v_row.drain_requested_at IS NULL THEN
    UPDATE __SCHEMA__.wh_role_assignments r
       SET drain_requested_at = now(), drain_requested_by = p_instance_id
     WHERE r.role = p_role AND r.holder_instance_id = v_row.holder_instance_id
       AND r.epoch = v_row.epoch AND r.drain_requested_at IS NULL;
    RETURN 'draining';
  END IF;
  RETURN CASE WHEN v_row.drain_requested_at IS NULL THEN 'contended' ELSE 'draining' END;
END;
$$;

-- The return type gained the drain, version and backstop columns, so the earlier definition goes first.
SELECT __SCHEMA__.drop_all_overloads('wh_role_assignment_status');

-- ============================================================================
-- wh_role_assignment_status — one row per role, for health, metrics and the
-- operator: state is held, lapsed (a holder whose assignment is no longer valid,
-- before the next vote records it) or vacant. void_reason says why a lapsed one
-- is. The columns phase 1 and 2 read keep their names and order.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Status_ReportsHeldLapsedAndVacant_WithEpochAndElectionCountAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceSqlTests.cs:Vote_ByANewerCaller_AsksTheOlderHolderToDrain_OnceAsync</tests>
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
  duty_backend_active BOOLEAN
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
         __SCHEMA__._role_backend_active(a.duty_backend_pid, a.duty_backend_started_at)
    FROM (
      SELECT r.*,
             CASE WHEN r.holder_instance_id IS NULL THEN NULL
                  ELSE __SCHEMA__._role_lapse_reason(r) END AS lapse
        FROM __SCHEMA__.wh_role_assignments r
    ) a
   ORDER BY a.role
$$;

