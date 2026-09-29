-- Migration: 173_RoleAssignments.sql
-- Date: 2026-09-28
-- Description: Duty role assignment (issue #966). The advisory lock decides only the VOTE; the
--              role is a row from then on.
--
--              Until now a duty was a session advisory lock held on a dedicated connection for the
--              holder's whole tenure, and wh_instance_capabilities only reported it. That costs a
--              pinned direct connection per held duty (a transaction-pooling front end cannot keep
--              a session lock), gives no way to take a duty from a holder that is alive but stuck
--              (its session stays open, so its lock stays held), and loses every holding on a
--              database restart.
--
--              Here a vote is ONE statement: wh_elect_role takes a transaction-scoped advisory lock
--              on the role, re-reads the assignment, voids it when it has lapsed or its holder has
--              been evicted or reaped, and assigns the caller when the role is free. The lock is
--              released at commit. Because the vote is a single statement, a client that stalls
--              cannot hold the vote lock: the server runs the statement to completion without a
--              round trip to it.
--
--              The assignment row is the authority. It carries an EPOCH, incremented on every new
--              assignment and never reused, which exclusive work presents to wh_assert_role_epoch
--              in the same transaction as its writes: a stale epoch raises WHF01 and the writes
--              roll back, so a paused holder that wakes after losing the role cannot do damage.
--
--              Liveness: an assignment is valid while its lease is unexpired AND its holder is
--              registered in wh_service_instances AND is not tombstoned in wh_instance_evictions.
--              The lease is extended only by the holder (wh_renew_role), from its work loop, and
--              every bound is computed from the database's clock, never an instance's.
--
--              Deliberately NO foreign key to wh_service_instances: a cascade would delete the row
--              and with it the epoch, and the epoch must never go backwards. A reaped holder is
--              detected by the vote, which voids the assignment and records why. Rows are never
--              deleted; a release or a void sets the holder to NULL and keeps the epoch.
--
--              Not part of the bootstrap closure: nothing votes for a role before migrations run.
--              The migrator duty stays on the session-lock elector for that reason.
-- Dependencies: 010 (wh_service_instances)
--               106 (wh_instance_evictions)
--               108 (record_capability, release_capability)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_role_assignments (
  role                     TEXT        NOT NULL PRIMARY KEY,
  -- NULL means vacant.
  holder_instance_id       UUID,
  -- The fencing token. Moves only when the holder changes, never backwards.
  epoch                    BIGINT      NOT NULL DEFAULT 0,
  assigned_at              TIMESTAMPTZ,
  renewed_at               TIMESTAMPTZ,
  lease_expires_at         TIMESTAMPTZ,
  -- The lease length the holder was granted; a renewal extends by this from now().
  lease                    INTERVAL,
  election_count           BIGINT      NOT NULL DEFAULT 0,
  -- The most recent vacancy: who, when and why (released | lapsed | evicted | unregistered). The
  -- cool-down after an involuntary lapse reads these, and the next grant reports them.
  last_holder_instance_id  UUID,
  last_vacated_at          TIMESTAMPTZ,
  last_vacated_reason      TEXT
);

COMMENT ON TABLE __SCHEMA__.wh_role_assignments IS
'One row per role (duty). The row is the role: holder, epoch (fencing token), lease in database time,
election count and the last vacancy. Written only by wh_elect_role, wh_renew_role and wh_release_role;
exclusive work checks it with wh_assert_role_epoch. Never deleted, so the epoch never goes backwards.';

-- ============================================================================
-- _role_vote_lock_key — the transaction advisory-lock key for one role's vote.
-- The high half is the oid of THIS schema's wh_role_assignments, which scopes
-- the key to the schema by construction (no schema name handled as text, so the
-- runners' different __SCHEMA__ substitutions cannot disagree). A collision with
-- another advisory-lock family can only make a vote wait; it cannot change the
-- outcome, because the row, not the lock, is the authority.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:VoteLockKey_CarriesThisSchemasTableOid_SoItIsSchemaScopedByConstructionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_VacantRole_GrantsEpochOne_WithALeaseInDatabaseTime_AndRecordsTheCapabilityAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_AfterTheHoldersLeaseLapses_VoidsIt_AndGrantsTheNextEpochAsync</tests>
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
-- _role_void_reason — why an assignment held by p_holder is no longer valid, or
-- NULL while it is. Tombstone first (the fleet's verdict), then registration,
-- then the lease.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_WhenTheHolderIsTombstoned_VoidsItAsEvictedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_WhenTheHolderIsNoLongerRegistered_VoidsItAsUnregisteredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_AfterTheHoldersLeaseLapses_VoidsIt_AndGrantsTheNextEpochAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._role_void_reason(p_holder UUID, p_lease_expires_at TIMESTAMPTZ)
RETURNS TEXT
LANGUAGE sql
STABLE
AS $$
  SELECT CASE
    WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = p_holder) THEN 'evicted'
    WHEN NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances s WHERE s.instance_id = p_holder) THEN 'unregistered'
    WHEN p_lease_expires_at IS NULL OR p_lease_expires_at <= now() THEN 'lapsed'
    ELSE NULL
  END
$$;

-- ============================================================================
-- _advisory_lock_held_elsewhere — whether a backend other than this one holds the
-- single-bigint advisory lock p_key in this database. Same reassembly as
-- AdvisoryLockProbe: PostgreSQL splits the key across classid (high 32 bits) and
-- objid (low 32 bits). Used to see an instance still on the session-lock elector.
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
-- wh_elect_role — the vote.
--
-- outcome:
--   granted        the caller is the new holder, at a new epoch
--   held           the caller already held a live assignment; its lease is renewed, same epoch
--   contended      another instance holds a live assignment
--   cooling_down   the caller's own assignment lapsed within p_cooldown; it may not win it back yet
--   legacy_holder  another backend holds p_legacy_lock_key (an instance on the session-lock elector)
--   refused        the caller is tombstoned or not registered
--
-- previous_holder_instance_id / void_reason: on a grant, the last vacancy this grant replaced (who
-- held the role before, and why they stopped), so the winner can log the hand-off exactly once.
-- lease_remaining: a DURATION computed by the database, never an instant for the caller's clock.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_VacantRole_GrantsEpochOne_WithALeaseInDatabaseTime_AndRecordsTheCapabilityAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_AfterTheHoldersLeaseLapses_VoidsIt_AndGrantsTheNextEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Elect_ByALapsedHolder_WithinTheCooldown_IsRefused_ButAnotherInstanceIsGrantedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentElectorE2ETests.cs:TenInstancesRacing_ExactlyOneHolderAndOneEpochActAsync</tests>
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
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
#variable_conflict use_column
DECLARE
  v_row __SCHEMA__.wh_role_assignments%ROWTYPE;
  v_void TEXT;
  v_epoch BIGINT;
BEGIN
  IF p_lease IS NULL OR p_lease <= INTERVAL '0' THEN
    RAISE EXCEPTION 'wh_elect_role: the lease must be positive, got %', p_lease USING ERRCODE = '22023';
  END IF;

  -- The caller first: an evicted or unknown instance is told so, whoever holds the role.
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions e WHERE e.instance_id = p_instance_id)
     OR NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances s WHERE s.instance_id = p_instance_id) THEN
    RETURN QUERY SELECT 'refused'::TEXT, NULL::UUID, 0::BIGINT, NULL::INTERVAL, NULL::UUID, NULL::TEXT;
    RETURN;
  END IF;

  -- Fast path: a live holder other than the caller answers without the vote lock and without a
  -- write, so an instance polling for a held role costs one read.
  SELECT * INTO v_row FROM __SCHEMA__.wh_role_assignments r WHERE r.role = p_role;
  IF FOUND
     AND v_row.holder_instance_id IS NOT NULL
     AND v_row.holder_instance_id <> p_instance_id
     AND __SCHEMA__._role_void_reason(v_row.holder_instance_id, v_row.lease_expires_at) IS NULL THEN
    RETURN QUERY SELECT 'contended'::TEXT, v_row.holder_instance_id, v_row.epoch,
      v_row.lease_expires_at - now(), NULL::UUID, NULL::TEXT;
    RETURN;
  END IF;

  -- The vote. Held until this statement's transaction commits, which is milliseconds away.
  PERFORM pg_advisory_xact_lock(__SCHEMA__._role_vote_lock_key(p_role));

  INSERT INTO __SCHEMA__.wh_role_assignments (role) VALUES (p_role) ON CONFLICT (role) DO NOTHING;
  -- FOR UPDATE as well as the vote lock: wh_assert_role_epoch takes FOR SHARE, so a vote cannot
  -- reassign the role between a fenced writer's check and its commit.
  SELECT * INTO v_row FROM __SCHEMA__.wh_role_assignments r WHERE r.role = p_role FOR UPDATE;

  IF v_row.holder_instance_id IS NOT NULL THEN
    v_void := __SCHEMA__._role_void_reason(v_row.holder_instance_id, v_row.lease_expires_at);
    IF v_void IS NULL THEN
      IF v_row.holder_instance_id = p_instance_id THEN
        UPDATE __SCHEMA__.wh_role_assignments r
           SET renewed_at = now(), lease_expires_at = now() + p_lease, lease = p_lease
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
           last_holder_instance_id = v_row.holder_instance_id,
           last_vacated_at = now(),
           last_vacated_reason = v_void
     WHERE r.role = p_role;
    PERFORM __SCHEMA__.release_capability(v_row.holder_instance_id, p_role);
    v_row.last_holder_instance_id := v_row.holder_instance_id;
    v_row.last_vacated_at := now();
    v_row.last_vacated_reason := v_void;
  END IF;

  -- Hysteresis: an instance whose assignment lapsed may not win it back for p_cooldown, so a slow
  -- instance cannot bounce the role. A release is not a health signal and carries no cool-down.
  IF v_row.last_holder_instance_id = p_instance_id
     AND v_row.last_vacated_reason = 'lapsed'
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

  UPDATE __SCHEMA__.wh_role_assignments r
     SET holder_instance_id = p_instance_id,
         epoch = r.epoch + 1,
         assigned_at = now(),
         renewed_at = now(),
         lease_expires_at = now() + p_lease,
         lease = p_lease,
         election_count = r.election_count + 1
   WHERE r.role = p_role
  RETURNING r.epoch INTO v_epoch;
  PERFORM __SCHEMA__.record_capability(p_instance_id, p_role);

  RETURN QUERY SELECT 'granted'::TEXT, p_instance_id, v_epoch, p_lease,
    v_row.last_holder_instance_id, v_row.last_vacated_reason;
END;
$$;

-- ============================================================================
-- wh_renew_role — extend the caller's lease from now(), by the lease it was granted.
-- Refuses a wrong epoch, a lapsed lease (void: win a new vote instead), and a
-- tombstoned or unregistered holder. Called from the holder's work loop.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Renew_ExtendsOnlyTheCurrentLiveAssignmentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Renew_ByATombstonedHolder_IsRefusedAsync</tests>
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_renew_role(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_role_assignments r
     SET renewed_at = now(), lease_expires_at = now() + r.lease
   WHERE r.role = p_role
     AND r.holder_instance_id = p_instance_id
     AND r.epoch = p_epoch
     AND __SCHEMA__._role_void_reason(r.holder_instance_id, r.lease_expires_at) IS NULL;
  RETURN FOUND;
END;
$$;

-- ============================================================================
-- wh_release_role — vacate the role on a graceful stop, only when (instance,
-- epoch) is the current assignment, so a stale grant cannot vacate its successor.
--
-- <docs>proposals/duty-role-assignment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentSqlTests.cs:Release_VacatesAtOnce_WithNoCooldownForTheReleaserAsync</tests>
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
         last_holder_instance_id = p_instance_id,
         last_vacated_at = now(),
         last_vacated_reason = 'released'
   WHERE r.role = p_role
     AND r.holder_instance_id = p_instance_id
     AND r.epoch = p_epoch;
  IF NOT FOUND THEN
    RETURN FALSE;
  END IF;
  PERFORM __SCHEMA__.release_capability(p_instance_id, p_role);
  RETURN TRUE;
END;
$$;

-- ============================================================================
-- wh_assert_role_epoch — the fence. Exclusive work calls it in the same
-- transaction as its writes. Raises WHF01 unless (instance, epoch) is the
-- current, unexpired, non-tombstoned assignment. The holder is part of the check
-- because an asynchronous-replica failover can lose the last epoch increment,
-- and the same epoch number could then be issued twice.
--
-- FOR SHARE holds the row until the caller commits, so no vote can reassign the
-- role between this check and the writes it guards. clock_timestamp(), not now():
-- a fenced transaction may have started long before this call.
--
-- <docs>proposals/duty-role-assignment</docs>
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
      AND r.lease_expires_at > clock_timestamp()
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
-- wh_role_assignment_status — one row per role, for health, metrics and the
-- operator: state is held, lapsed (a holder whose assignment is no longer valid,
-- before the next vote records it) or vacant. void_reason says why a lapsed one is.
--
-- <docs>proposals/duty-role-assignment</docs>
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
  last_vacated_reason TEXT
)
LANGUAGE sql
STABLE
AS $$
  SELECT a.role,
         CASE
           WHEN a.holder_instance_id IS NULL THEN 'vacant'
           WHEN a.void_reason IS NULL THEN 'held'
           ELSE 'lapsed'
         END,
         a.holder_instance_id,
         a.epoch,
         a.assigned_at,
         a.renewed_at,
         a.lease_expires_at,
         CASE WHEN a.holder_instance_id IS NULL OR a.void_reason IS NOT NULL THEN NULL
              ELSE a.lease_expires_at - now() END,
         a.election_count,
         a.void_reason,
         a.last_holder_instance_id,
         a.last_vacated_at,
         a.last_vacated_reason
    FROM (
      SELECT r.*,
             CASE WHEN r.holder_instance_id IS NULL THEN NULL
                  ELSE __SCHEMA__._role_void_reason(r.holder_instance_id, r.lease_expires_at) END AS void_reason
        FROM __SCHEMA__.wh_role_assignments r
    ) a
   ORDER BY a.role
$$;
