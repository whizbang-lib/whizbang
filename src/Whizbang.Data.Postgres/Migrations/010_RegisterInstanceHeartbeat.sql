-- Migration: 010_RegisterInstanceHeartbeat.sql
-- Date: 2025-12-25
-- Description: Creates register_instance_heartbeat function for updating instance heartbeats.
--              Used by process_work_batch orchestrator to maintain instance liveness tracking.
-- Dependencies: 001-009 (requires wh_service_instances table)

CREATE OR REPLACE FUNCTION __SCHEMA__.register_instance_heartbeat(
  p_instance_id UUID,
  p_service_name VARCHAR(200),
  p_host_name VARCHAR(200),
  p_process_id INTEGER,
  p_metadata JSONB,
  p_now TIMESTAMPTZ,
  p_lease_expiry TIMESTAMPTZ
) RETURNS VOID AS $$
BEGIN
  -- Insert or update instance heartbeat
  -- ON CONFLICT ensures idempotency for repeated calls
  INSERT INTO wh_service_instances (
    instance_id,
    service_name,
    host_name,
    process_id,
    started_at,
    last_heartbeat_at,
    metadata
  ) VALUES (
    p_instance_id,
    p_service_name,
    p_host_name,
    p_process_id,
    p_now,
    p_now,
    p_metadata
  )
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = p_now,
    metadata = COALESCE(EXCLUDED.metadata, wh_service_instances.metadata);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.register_instance_heartbeat IS
'Updates service instance heartbeat timestamp. Inserts new instance if not exists, updates last_heartbeat_at if exists. Called by process_work_batch orchestrator.';
