-- Migration: 011_CleanupStaleInstances.sql
-- Date: 2025-12-25
-- Description: Creates cleanup_stale_instances function for removing inactive instances.
--              Returns deleted instance IDs for logging. Releases work from deleted instances.
-- Dependencies: 001-010 (requires wh_service_instances, wh_outbox, wh_inbox, wh_perspective_events)

SELECT __SCHEMA__.drop_all_overloads('cleanup_stale_instances');

CREATE OR REPLACE FUNCTION __SCHEMA__.cleanup_stale_instances(
  p_stale_cutoff TIMESTAMPTZ
) RETURNS TABLE(deleted_instance_id UUID) AS $$
DECLARE
  v_deleted_ids UUID[];
BEGIN

  -- Find and delete stale instances (older than cutoff)
  WITH deleted AS (
    DELETE FROM wh_service_instances
    WHERE last_heartbeat_at < p_stale_cutoff
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
  END IF;

  -- Return deleted IDs for orchestrator logging
  RETURN QUERY
  SELECT UNNEST(COALESCE(v_deleted_ids, ARRAY[]::UUID[]));
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.cleanup_stale_instances IS
'Removes stale service instances (with last_heartbeat_at < p_stale_cutoff) and releases their work items. Returns deleted instance IDs for logging. Called by process_work_batch orchestrator.';
