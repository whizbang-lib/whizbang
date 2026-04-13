-- Migration: 036_DeregisterInstance.sql
-- Date: 2026-04-12
-- Description: Creates deregister_instance function for graceful shutdown.
--              Releases all leases, clears active stream assignments, logs shutdown to wh_log,
--              and removes the instance from wh_service_instances.
--              Called by WhizbangShutdownService.StopAsync on SIGTERM / graceful shutdown.
-- Dependencies: 001-035 (requires all core tables)

SELECT __SCHEMA__.drop_all_overloads('deregister_instance');

CREATE OR REPLACE FUNCTION __SCHEMA__.deregister_instance(
  p_instance_id UUID,
  p_service_name TEXT DEFAULT NULL,
  p_host_name TEXT DEFAULT NULL
) RETURNS VOID AS $$
BEGIN
  -- Release all outbox leases held by this instance
  UPDATE wh_outbox SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all inbox leases held by this instance
  UPDATE wh_inbox SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all perspective event leases held by this instance
  UPDATE wh_perspective_events SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND processed_at IS NULL;

  -- Release all receptor processing leases held by this instance
  UPDATE wh_receptor_processing SET instance_id = NULL, lease_expiry = NULL
  WHERE instance_id = p_instance_id AND completed_at IS NULL;

  -- Release all active stream assignments held by this instance
  UPDATE wh_active_streams SET assigned_instance_id = NULL, lease_expiry = NULL
  WHERE assigned_instance_id = p_instance_id;

  -- Log shutdown to wh_log for audit trail (guaranteed persistence)
  INSERT INTO wh_log (log_level, source, message_id, error_message, metadata)
  VALUES (
    1,  -- Information
    'shutdown',
    p_instance_id,
    'Instance deregistered during graceful shutdown',
    jsonb_build_object(
      'service_name', COALESCE(p_service_name, 'unknown'),
      'host_name', COALESCE(p_host_name, 'unknown')
    )
  );

  -- Remove the instance registration
  DELETE FROM wh_service_instances WHERE instance_id = p_instance_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.deregister_instance IS
'Graceful shutdown deregistration. Releases all leases (outbox, inbox, perspective events, receptors, active streams), logs shutdown to wh_log, and removes the instance from wh_service_instances. Called by WhizbangShutdownService on SIGTERM.';
