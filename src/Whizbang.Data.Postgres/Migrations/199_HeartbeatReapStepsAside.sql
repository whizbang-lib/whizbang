-- Migration: 199_HeartbeatReapStepsAside.sql
-- Date: 2026-10-08
-- Description: The stale-peer reap inside record_heartbeat steps aside instead of waiting (#1217).
--
--              record_heartbeat reaps a stale peer inline (cleanup_stale_instances): it deletes the peer's
--              registration and releases its leases with plain updates of wh_outbox, wh_inbox_state,
--              wh_perspective_events, wh_active_streams and wh_receptor_processing. A claim that has just taken
--              one of the dead peer's expired rows holds that row until it commits, so the reap waited for the
--              claim, and the heartbeat with it: the same shape 196 removed from the registration row, by
--              another path. Reproduced in ClaimWorkRegistrationSqlTests, holding a claim's transaction open.
--
--              The reap is opportunistic: MaintenanceWorker runs cleanup_stale_instances on its own cadence,
--              and the next heartbeat that sees the peer tries again. So the heartbeat now runs it in a
--              subtransaction with a 100 ms lock timeout and, when a lock is not available (or the reap is
--              chosen as a deadlock victim), rolls the reap back and returns. The heartbeat's own write is
--              unaffected and committed with it.
--
-- Dependencies: 147 (record_heartbeat, copied verbatim with the guarded reap), 162 (cleanup_stale_instances)
-- Objects: record_heartbeat

-- The signature is 147's; every definition site clears the overloads first (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatThatFindsAStalePeerDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkRegistrationSqlTests.cs:ClaimWork_HeldOpen_AHeartbeatForTheSameInstanceDoesNotWaitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecordHeartbeatBackfillTests.cs:PeerPastTheDerivedThreshold_IsStillReapedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.record_heartbeat(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_metadata JSONB DEFAULT '{}'::JSONB,
  p_lifecycle_phase TEXT DEFAULT NULL,
  p_library_version TEXT DEFAULT NULL,
  p_stale_threshold_seconds INTEGER DEFAULT NULL
) RETURNS BOOLEAN AS $$
DECLARE
  -- 147: the caller's cadence-derived threshold; the pre-147 constant when nothing is passed.
  v_stale_cutoff TIMESTAMPTZ := NOW() - (COALESCE(p_stale_threshold_seconds, 30) * INTERVAL '1 second');
  -- v0.687: any heartbeat older than this is treated as definitively dead and
  -- bypasses the v0.681 alive-lock guard. Covers OOMKilled pods on half-open TCP
  -- where the session lock can linger until OS keepalive (~2 h default on Linux).
  -- 147: never below twice the stale threshold, so the two cutoffs cannot cross.
  v_definitive_dead_cutoff TIMESTAMPTZ := NOW() - GREATEST(INTERVAL '5 minutes', 2 * (COALESCE(p_stale_threshold_seconds, 30) * INTERVAL '1 second'));
BEGIN
  -- An evicted instance must never rejoin silently. Reaping already released its leases
  -- to other instances; a heartbeat that quietly re-inserted the row would let the
  -- returning process believe it was still an ordinary fleet member. Refuse instead, and
  -- tell the caller (this is the fence reaping was missing, migration 106).
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions WHERE instance_id = p_instance_id) THEN
    RETURN FALSE;
  END IF;

  INSERT INTO __SCHEMA__.wh_service_instances
    (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata, lifecycle_phase, library_version)
  VALUES
    (p_instance_id, p_service_name, p_host_name, p_process_id, NOW(), NOW(), p_metadata, p_lifecycle_phase, p_library_version)
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = NOW(),
    metadata = EXCLUDED.metadata,
    -- 147: the beat's value is the live truth when present; a NULL keeps what record_instance_state wrote.
    lifecycle_phase = COALESCE(EXCLUDED.lifecycle_phase, __SCHEMA__.wh_service_instances.lifecycle_phase),
    library_version = COALESCE(EXCLUDED.library_version, __SCHEMA__.wh_service_instances.library_version);

  -- Opportunistic stale-peer cleanup (Phase H step 6 slice 1). When a peer has gone
  -- silent past the stale cutoff, delete its row and release its leases so live
  -- instances can claim on the next claim_work tick. Cheap pre-check on the indexed
  -- last_heartbeat_at means the heavyweight DELETE+lease-null block only fires when
  -- there's actually a stale peer; most heartbeats no-op past the EXISTS check.
  -- Backstop: MaintenanceWorker also runs cleanup_stale_instances every IntervalMinutes.
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances
    WHERE last_heartbeat_at < v_stale_cutoff
      AND instance_id != p_instance_id
    LIMIT 1
  ) THEN
    -- 199: a heartbeat never waits for the reap. Its lease releases wait on rows a claim may hold, so
    -- the reap runs in a subtransaction under a short lock timeout and is rolled back when a lock is
    -- not available; the backstop and the next heartbeat try again. The timeout is set inside the
    -- subtransaction, so the rollback restores the caller's value with it.
    BEGIN
      PERFORM set_config('lock_timeout', '100ms', true);
      PERFORM __SCHEMA__.cleanup_stale_instances(v_stale_cutoff, v_definitive_dead_cutoff);
    EXCEPTION WHEN lock_not_available OR deadlock_detected THEN
      NULL;
    END;
  END IF;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.record_heartbeat(UUID, TEXT, TEXT, INTEGER, JSONB, TEXT, TEXT, INTEGER) IS
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by the C# HeartbeatWorker on its own timer, independent of polling cadence, and by ClaimWorker to register when a claim reports its row missing or stale (196). Opportunistically cleans up stale peers when detected (cheap pre-check guard); since 199 that cleanup steps aside under a 100 ms lock timeout instead of waiting for rows a claim holds. Migration 106: returns FALSE and does nothing when the calling instance_id has been tombstoned in wh_instance_evictions (this instance was reaped and must not rejoin); returns TRUE otherwise. Migration 147: p_lifecycle_phase and p_library_version backfill the registry row (non-null wins, null keeps the recorded value), and p_stale_threshold_seconds lets the caller derive the peer-reap threshold from its own cadence instead of the 30 s constant.';
