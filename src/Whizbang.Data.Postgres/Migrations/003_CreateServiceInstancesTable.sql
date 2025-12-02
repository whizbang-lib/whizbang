-- Migration: Create service instances tracking table
-- Date: 2025-12-02
-- Description: Tracks active service instances with heartbeat timestamps
--              for distributed work coordination and failure detection

-- Create service instances table
CREATE TABLE IF NOT EXISTS wb_service_instances (
  instance_id UUID NOT NULL PRIMARY KEY,
  service_name VARCHAR(200) NOT NULL,
  host_name VARCHAR(200) NOT NULL,
  process_id INTEGER NOT NULL,
  started_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  metadata JSONB NULL
);

-- Create index for service name queries
CREATE INDEX IF NOT EXISTS idx_service_instances_service_name
ON wb_service_instances (service_name, last_heartbeat_at);

-- Create index for heartbeat-based queries (finding expired instances)
CREATE INDEX IF NOT EXISTS idx_service_instances_heartbeat
ON wb_service_instances (last_heartbeat_at);

-- Add comments for documentation
COMMENT ON TABLE wb_service_instances IS 'Tracks active service instances for distributed work coordination';
COMMENT ON COLUMN wb_service_instances.instance_id IS 'Unique identifier for this service instance (generated at startup)';
COMMENT ON COLUMN wb_service_instances.service_name IS 'Name of the service (e.g., InventoryWorker)';
COMMENT ON COLUMN wb_service_instances.host_name IS 'Hostname where the service is running';
COMMENT ON COLUMN wb_service_instances.process_id IS 'Operating system process ID';
COMMENT ON COLUMN wb_service_instances.started_at IS 'When this instance started';
COMMENT ON COLUMN wb_service_instances.last_heartbeat_at IS 'Last time this instance reported a heartbeat';
COMMENT ON COLUMN wb_service_instances.metadata IS 'Additional instance metadata (JSON)';
