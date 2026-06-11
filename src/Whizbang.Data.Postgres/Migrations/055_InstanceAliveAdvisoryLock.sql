-- Migration: 055_InstanceAliveAdvisoryLock.sql
-- Date: 2026-06-10
-- Description: Adds session-level advisory-lock-based instance liveness signal.
--              Long-lived direct connections (LISTEN, pinned worker pool) acquire a
--              session-level advisory lock keyed by instance_id at conn open. When the
--              connection drops (pod death, network reset), PostgreSQL auto-releases the
--              lock so other instances can detect the death within seconds rather than
--              30 s of heartbeat-table staleness. The heartbeat table write remains the
--              fallback for environments without a direct conn.
-- Dependencies: 010 (wh_service_instances), 011 (cleanup_stale_instances), 025-027 (claim_orphaned_*)

SELECT __SCHEMA__.drop_all_overloads('claim_instance_alive_lock');
SELECT __SCHEMA__.drop_all_overloads('is_instance_alive');

-- ============================================================================
-- claim_instance_alive_lock: called by direct-conn holders at conn open.
-- Returns TRUE if the lock was acquired; FALSE if another conn already holds it
-- (e.g. duplicate-startup race). Either result is non-fatal for callers — the
-- heartbeat table fallback still works.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_instance_alive_lock(
  p_instance_id UUID
) RETURNS BOOLEAN AS $$
BEGIN
  RETURN pg_try_advisory_lock(hashtext('wh_instance_alive:' || p_instance_id::text));
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_instance_alive_lock IS
  'Acquires a session-level advisory lock proving this instance is alive. Called at conn open by '
  'PgSharedNotifyConnection and PinnedConnectionPool. Returns TRUE on success, FALSE if another '
  'conn already holds the lock (uncommon — duplicate-startup race; log + continue).';

-- ============================================================================
-- is_instance_alive: dual-signal predicate. Used by cleanup_stale_instances
-- and the claim_orphaned_* family to decide whether an instance is alive.
--
-- Signal 1 (primary): the alive-lock is held in pg_locks. Pod is alive even if
--                     the heartbeat table is stale (e.g. adaptive 60 s cadence).
-- Signal 2 (fallback): heartbeat row exists with last_heartbeat_at within
--                     p_heartbeat_threshold_seconds of NOW(). Used when the
--                     direct conn isn't enabled or hasn't been acquired yet.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.is_instance_alive(
  p_instance_id UUID,
  p_heartbeat_threshold_seconds INT DEFAULT 90
) RETURNS BOOLEAN AS $$
DECLARE
  v_lock_key BIGINT;
  v_lock_held BOOLEAN;
  v_heartbeat_fresh BOOLEAN;
BEGIN
  v_lock_key := hashtext('wh_instance_alive:' || p_instance_id::text);

  -- Signal 1: any session currently holds the alive-lock for this instance.
  SELECT EXISTS (
    SELECT 1 FROM pg_locks
    WHERE locktype = 'advisory'
      AND classid = (v_lock_key >> 32) & x'FFFFFFFF'::bigint
      AND objid = v_lock_key & x'FFFFFFFF'::bigint
      AND granted = true
  ) INTO v_lock_held;

  IF v_lock_held THEN
    RETURN TRUE;
  END IF;

  -- Signal 2: heartbeat table row is fresh.
  SELECT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances
    WHERE instance_id = p_instance_id
      AND last_heartbeat_at > NOW() - (p_heartbeat_threshold_seconds * INTERVAL '1 second')
  ) INTO v_heartbeat_fresh;

  RETURN v_heartbeat_fresh;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.is_instance_alive IS
  'Returns TRUE if the instance is alive via EITHER signal: the alive-lock held in pg_locks '
  '(primary, sub-second detection on conn death) OR a fresh heartbeat row (fallback, 30 s '
  'recovery for hosts without a direct conn). cleanup_stale_instances and claim_orphaned_* '
  'switched to this predicate so adaptive heartbeat cadence (5 s when no lock, 60 s when lock '
  'held) does not trip false cleanups.';
