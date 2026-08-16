-- Migration: 106_InstanceEvictionFencing.sql
-- Date: 2026-08-15
-- Description: cleanup_stale_instances deletes a stale instance's row and releases its work, but
--              record_heartbeat is an unguarded INSERT ... ON CONFLICT DO UPDATE. The reaped
--              instance's next heartbeat re-inserts it and it rejoins as though nothing happened —
--              a pod paused by a long GC pause, a brief partition, or a throttled node returns and
--              resumes against state that has moved on without it. This migration makes reaping an
--              actual fence: a tombstone that a returning instance's heartbeat consults and is
--              refused against, with the refusal reported through record_heartbeat's own return
--              value rather than a side channel.
--
--              record_heartbeat's return type changes VOID -> BOOLEAN: true = registered/renewed,
--              false = this instance has been evicted and must not consider itself part of the
--              fleet. cleanup_stale_instances' signature is unchanged; only its body gains the
--              tombstone insert.
--
--              Retention for wh_instance_evictions is handled by perform_maintenance (032),
--              which already documents itself as "Extensible — add new maintenance operations
--              here over time."
-- Dependencies: 010-011 (wh_service_instances, cleanup_stale_instances)
--               029 (record_heartbeat)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_instance_evictions (
  instance_id UUID PRIMARY KEY,
  evicted_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  reason TEXT
);

COMMENT ON TABLE __SCHEMA__.wh_instance_evictions IS
'Tombstones for instances reaped by cleanup_stale_instances. Consulted by record_heartbeat so a
paused process that resumes after being reaped is refused rather than silently rejoining. Purged
by perform_maintenance after instance_eviction_retention_hours (default 24).';

CREATE INDEX IF NOT EXISTS idx_instance_evictions_evicted_at
  ON __SCHEMA__.wh_instance_evictions (evicted_at);

-- ============================================================================
-- cleanup_stale_instances — same signature, body gains the tombstone insert.
-- Full body copied from migration 011 verbatim + the eviction record.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.cleanup_stale_instances(
  p_stale_cutoff TIMESTAMPTZ,
  p_definitive_dead_cutoff TIMESTAMPTZ DEFAULT NULL
) RETURNS TABLE(deleted_instance_id UUID) AS $$
DECLARE
  v_deleted_ids UUID[];
BEGIN

  -- Find and delete stale instances (older than cutoff). v0.681 — also skip rows
  -- whose session-level alive-lock is still held (migration 055): the adaptive
  -- heartbeat cadence may legitimately delay the heartbeat write past p_stale_cutoff
  -- when the direct conn is healthy. The lock is the primary liveness signal in
  -- that mode; the heartbeat-table check remains the fallback.
  --
  -- v0.687 — the alive-lock guard has a long-tail failure mode under OOMKill +
  -- half-open TCP. The kernel SIGKILLs the process before any graceful socket
  -- teardown, so the server-side session keeps holding the advisory lock until
  -- OS-level TCP keepalive notices (defaults to 7200 s = 2 h on Linux). Within
  -- that window cleanup_stale_instances refuses to remove the dead row, which
  -- in turn keeps that instance_id on every claimed lease in wh_inbox / wh_outbox
  -- / wh_perspective_events — claim_orphaned_* can't release the work because
  -- those rows still have a future lease_expiry and a non-null instance_id.
  --
  -- The optional p_definitive_dead_cutoff lets callers say: "if the heartbeat
  -- table has been silent for THIS long, the instance is definitely dead — bypass
  -- the alive-lock guard and clean it up." The lock guard still applies in the
  -- short window (heartbeat stale but newer than the definitive cutoff) so we
  -- preserve the adaptive-heartbeat correctness case. NULL preserves pre-v0.687
  -- behavior (single-arg callers get the legacy semantics).
  WITH deleted AS (
    DELETE FROM __SCHEMA__.wh_service_instances
    WHERE last_heartbeat_at < p_stale_cutoff
      AND (
        -- v0.687 definitive-dead bypass: heartbeat is older than the caller's
        -- "we don't trust the lock past this point" threshold. Skip the guard.
        (p_definitive_dead_cutoff IS NOT NULL
          AND last_heartbeat_at < p_definitive_dead_cutoff)
        OR
        -- v0.681 alive-lock guard: respect the lock as the primary liveness
        -- signal within the adaptive-heartbeat window.
        NOT EXISTS (
          -- pg_locks.classid/objid are oid (uint32). hashtext() returns signed int4 — when
          -- negative, the lower-32-bit lane evaluates >2^31-1 as bigint, which overflows
          -- ::int (22003). Compare against the bigint expression and cast to ::oid so the
          -- comparison stays in oid-space without sign-flip.
          SELECT 1 FROM pg_locks
          WHERE locktype = 'advisory'
            AND classid = ((hashtext('wh_instance_alive:' || wh_service_instances.instance_id::text)::bigint >> 32) & x'FFFFFFFF'::bigint)::oid
            AND objid = (hashtext('wh_instance_alive:' || wh_service_instances.instance_id::text)::bigint & x'FFFFFFFF'::bigint)::oid
            AND granted = true
        )
      )
    RETURNING instance_id
  )
  SELECT ARRAY_AGG(instance_id) INTO v_deleted_ids
  FROM deleted;

  -- Release all work from deleted instances
  IF v_deleted_ids IS NOT NULL THEN
    -- Tombstone every reaped instance so a paused process that resumes and calls
    -- record_heartbeat again is refused rather than silently rejoining — see migration 106.
    -- ON CONFLICT is defensive only: an instance_id cannot be re-deleted once gone, so a
    -- collision here would mean a caller reused an id, which this must not paper over by
    -- discarding the earlier eviction's timestamp.
    INSERT INTO __SCHEMA__.wh_instance_evictions (instance_id, evicted_at, reason)
    SELECT unnest(v_deleted_ids), NOW(), 'stale heartbeat (last_heartbeat_at < ' || p_stale_cutoff || ')'
    ON CONFLICT (instance_id) DO NOTHING;

    -- Release outbox messages
    UPDATE __SCHEMA__.wh_outbox
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release inbox messages
    UPDATE __SCHEMA__.wh_inbox
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release perspective events
    UPDATE __SCHEMA__.wh_perspective_events
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release active stream assignments from deleted instances
    UPDATE __SCHEMA__.wh_active_streams
    SET assigned_instance_id = NULL,
        lease_expiry = NULL
    WHERE assigned_instance_id = ANY(v_deleted_ids);

    -- Release receptor processing leases from deleted instances
    UPDATE __SCHEMA__.wh_receptor_processing
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Log stale instance removal to wh_log for audit trail
    INSERT INTO __SCHEMA__.wh_log (log_level, source, message_id, error_message, metadata)
    SELECT
      2,  -- Warning
      'stale_cleanup',
      unnest(v_deleted_ids),
      'Stale instance removed — all leases released',
      jsonb_build_object(
        'deleted_instance_count', array_length(v_deleted_ids, 1),
        'stale_cutoff', p_stale_cutoff
      );

    -- v0.502 slice B.3 — orphan-redistribution NOTIFY.
    -- After releasing leases owned by the dead instances, wake every LIVE instance so it
    -- runs a catch-up claim_orphaned_* over the newly-unowned rows. Without this, live
    -- instances only discover the released work on their next poll tick — which under the
    -- new v0.502 NotifyHealthyPollingIntervalMilliseconds=30000 default could be up to
    -- 30 seconds away. Emitting a NOTIFY here turns orphan recovery from polling-bound to
    -- NOTIFY-bound, the architectural goal of v0.502.
    --
    -- Per-instance channel naming matches existing PgWorkNotificationListener.ChannelName:
    --   wh_work_i_{instance_id}
    -- Payload "orphan" signals "go run claim_orphaned_*" to ClaimWorker._onSignal.
    PERFORM pg_notify('wh_work_i_' || si.instance_id::text, 'orphan')
    FROM __SCHEMA__.wh_service_instances si
    WHERE si.last_heartbeat_at >= p_stale_cutoff;  -- live instances only
  END IF;

  -- Return deleted IDs for orchestrator logging
  RETURN QUERY
  SELECT UNNEST(COALESCE(v_deleted_ids, ARRAY[]::UUID[]));
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.cleanup_stale_instances IS
'Removes stale service instances (with last_heartbeat_at < p_stale_cutoff) and releases their work items. Returns deleted instance IDs for logging. Called by process_work_batch orchestrator. v0.687 — optional p_definitive_dead_cutoff bypasses the v0.681 alive-lock guard when the heartbeat is so old we trust it over pg_locks (covers OOMKilled pods on half-open TCP sessions where the advisory lock can linger for hours). Migration 106 — every reaped instance is tombstoned in wh_instance_evictions so record_heartbeat can refuse it if it later resumes.';

-- ============================================================================
-- record_heartbeat — RETURNS VOID -> RETURNS BOOLEAN (signature change, drop first).
-- Full body copied from migration 029 verbatim + the eviction check.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

CREATE OR REPLACE FUNCTION __SCHEMA__.record_heartbeat(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_metadata JSONB DEFAULT '{}'::JSONB
) RETURNS BOOLEAN AS $$
DECLARE
  v_stale_cutoff TIMESTAMPTZ := NOW() - INTERVAL '30 seconds';
  -- v0.687: any heartbeat older than this is treated as definitively dead and
  -- bypasses the v0.681 alive-lock guard. Covers OOMKilled pods on half-open TCP
  -- where the session lock can linger until OS keepalive (~2 h default on Linux).
  v_definitive_dead_cutoff TIMESTAMPTZ := NOW() - INTERVAL '5 minutes';
BEGIN
  -- An evicted instance must never rejoin silently. Reaping already released its leases
  -- to other instances; a heartbeat that quietly re-inserted the row would let the
  -- returning process believe it was still an ordinary fleet member. Refuse instead, and
  -- tell the caller — this is the fence reaping was missing (migration 106).
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions WHERE instance_id = p_instance_id) THEN
    RETURN FALSE;
  END IF;

  INSERT INTO __SCHEMA__.wh_service_instances
    (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
  VALUES
    (p_instance_id, p_service_name, p_host_name, p_process_id, NOW(), NOW(), p_metadata)
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = NOW(),
    metadata = EXCLUDED.metadata;

  -- Opportunistic stale-peer cleanup (Phase H step 6 slice 1). When a peer has gone
  -- silent past the stale cutoff, delete its row and release its leases so live
  -- instances can claim on the next claim_work tick. Cheap pre-check on the indexed
  -- last_heartbeat_at means the heavyweight DELETE+lease-null block only fires when
  -- there's actually a stale peer — most heartbeats no-op past the EXISTS check.
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

COMMENT ON FUNCTION __SCHEMA__.record_heartbeat IS
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by C# HeartbeatWorker on its own timer (5 s default), independent of polling cadence. Opportunistically cleans up stale peers when detected (cheap pre-check guard). Migration 106 — returns FALSE and does nothing when the calling instance_id has been tombstoned in wh_instance_evictions (this instance was reaped and must not rejoin); returns TRUE otherwise. Sub-millisecond cost on the no-stale-peer, non-evicted path.';
