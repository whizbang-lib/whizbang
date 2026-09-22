-- Migration: 147_HeartbeatRegistryBackfill.sql
-- Date: 2026-09-08
-- Description: Two liveness gaps closed on the heartbeat write.
--
--              (1) The registry row can exist with NULL lifecycle_phase and library_version for the
--              whole life of an instance. record_instance_state (109) is UPDATE-only and returns
--              FALSE when the row does not exist yet, which is expected during early startup; the
--              row is then created by the first heartbeat, or by claim_work's fallback INSERT, with
--              neither column set. If no later transition happens (the common case: the last
--              transition to Running preceded the row), the columns stay blank and every reader
--              that keys on them treats the instance as unknown. The heartbeat now carries the
--              instance's current phase and version and backfills them: a non-null value wins (it
--              is the live truth from the process), a null leaves the recorded value alone.
--
--              (2) The opportunistic peer reap inside record_heartbeat compared peers against a
--              30-second constant, equal to the writer's fast cadence: zero margin, so a beat late
--              by any latency at all could tombstone a live peer on a host without the alive-lock
--              (pooled connections). The threshold is now a parameter the caller derives from its
--              cadence (HeartbeatLivenessThreshold: two of the slowest cadence plus one fast
--              interval; 150 s with the defaults). Callers that pass nothing keep the old default.
--              The definitive-dead bypass keeps its 5-minute floor but never falls below twice the
--              stale threshold, so the two cutoffs cannot cross.
--
--              Signature change (three optional parameters appended), so the previous overload is
--              dropped first; exactly one overload of record_heartbeat exists afterwards.
-- Dependencies: 106 (record_heartbeat, cleanup_stale_instances, wh_instance_evictions)
--               109 (lifecycle_phase, library_version columns)

SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

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
    PERFORM __SCHEMA__.cleanup_stale_instances(v_stale_cutoff, v_definitive_dead_cutoff);
  END IF;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.record_heartbeat(UUID, TEXT, TEXT, INTEGER, JSONB, TEXT, TEXT, INTEGER) IS
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by the C# HeartbeatWorker on its own timer, independent of polling cadence. Opportunistically cleans up stale peers when detected (cheap pre-check guard). Migration 106: returns FALSE and does nothing when the calling instance_id has been tombstoned in wh_instance_evictions (this instance was reaped and must not rejoin); returns TRUE otherwise. Migration 147: p_lifecycle_phase and p_library_version backfill the registry row (non-null wins, null keeps the recorded value), and p_stale_threshold_seconds lets the caller derive the peer-reap threshold from its own cadence instead of the 30 s constant (which was equal to the fast cadence and could tombstone a live peer on a delayed beat). Sub-millisecond cost on the no-stale-peer, non-evicted path.';
