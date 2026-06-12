-- Migration: 011_CleanupStaleInstances.sql
-- Date: 2025-12-25 (last revised 2026-06-12 for v0.687 definitive-dead cutoff)
-- Description: Creates cleanup_stale_instances function for removing inactive instances.
--              Returns deleted instance IDs for logging. Releases work from deleted instances.
--              v0.687: adds optional p_definitive_dead_cutoff parameter that bypasses the
--              v0.681 alive-lock guard when the heartbeat is so old we're confident the
--              instance is dead regardless of pg_locks state. Targets the OOMKilled-on-
--              misbehaving-TCP-path case where the session lock can linger until OS
--              keepalive fires (defaults to 7200 s on Linux).
-- Dependencies: 001-010 (requires wh_service_instances, wh_outbox, wh_inbox, wh_perspective_events)
--               055 (claim_instance_alive_lock — alive-lock source)

SELECT __SCHEMA__.drop_all_overloads('cleanup_stale_instances');

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
    DELETE FROM wh_service_instances
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
    -- Release outbox messages
    UPDATE wh_outbox
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release inbox messages
    UPDATE wh_inbox
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release perspective events
    UPDATE wh_perspective_events
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Release active stream assignments from deleted instances
    UPDATE wh_active_streams
    SET assigned_instance_id = NULL,
        lease_expiry = NULL
    WHERE assigned_instance_id = ANY(v_deleted_ids);

    -- Release receptor processing leases from deleted instances
    UPDATE wh_receptor_processing
    SET instance_id = NULL,
        lease_expiry = NULL
    WHERE instance_id = ANY(v_deleted_ids);

    -- Log stale instance removal to wh_log for audit trail
    INSERT INTO wh_log (log_level, source, message_id, error_message, metadata)
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
    FROM wh_service_instances si
    WHERE si.last_heartbeat_at >= p_stale_cutoff;  -- live instances only
  END IF;

  -- Return deleted IDs for orchestrator logging
  RETURN QUERY
  SELECT UNNEST(COALESCE(v_deleted_ids, ARRAY[]::UUID[]));
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.cleanup_stale_instances IS
'Removes stale service instances (with last_heartbeat_at < p_stale_cutoff) and releases their work items. Returns deleted instance IDs for logging. Called by process_work_batch orchestrator. v0.687 — optional p_definitive_dead_cutoff bypasses the v0.681 alive-lock guard when the heartbeat is so old we trust it over pg_locks (covers OOMKilled pods on half-open TCP sessions where the advisory lock can linger for hours).';
