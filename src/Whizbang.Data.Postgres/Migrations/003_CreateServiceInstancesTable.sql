-- Migration: Create service instances tracking table
-- Date: 2025-12-02
-- Description: Tracks active service instances with heartbeat timestamps
--              for distributed work coordination and failure detection

-- Create service instances table
CREATE TABLE IF NOT EXISTS wh_service_instances (
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
ON wh_service_instances (service_name, last_heartbeat_at);

-- Create index for heartbeat-based queries (finding expired instances)
CREATE INDEX IF NOT EXISTS idx_service_instances_heartbeat
ON wh_service_instances (last_heartbeat_at);

-- Add comments for documentation
COMMENT ON TABLE wh_service_instances IS 'Tracks active service instances for distributed work coordination';
COMMENT ON COLUMN wh_service_instances.instance_id IS 'Unique identifier for this service instance (generated at startup)';
COMMENT ON COLUMN wh_service_instances.service_name IS 'Name of the service (e.g., InventoryWorker)';
COMMENT ON COLUMN wh_service_instances.host_name IS 'Hostname where the service is running';
COMMENT ON COLUMN wh_service_instances.process_id IS 'Operating system process ID';
COMMENT ON COLUMN wh_service_instances.started_at IS 'When this instance started';
COMMENT ON COLUMN wh_service_instances.last_heartbeat_at IS 'Last time this instance reported a heartbeat';
COMMENT ON COLUMN wh_service_instances.metadata IS 'Additional instance metadata (JSON)';

-- Create partition assignments table
CREATE TABLE IF NOT EXISTS wh_partition_assignments (
  partition_number INTEGER NOT NULL PRIMARY KEY,
  instance_id UUID NOT NULL,
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (instance_id) REFERENCES wh_service_instances(instance_id) ON DELETE CASCADE
);

-- Create index for instance queries
CREATE INDEX IF NOT EXISTS idx_partition_assignments_instance
ON wh_partition_assignments (instance_id, last_heartbeat);

-- Add comments for documentation
COMMENT ON TABLE wh_partition_assignments IS 'Tracks partition ownership for distributed work coordination';
COMMENT ON COLUMN wh_partition_assignments.partition_number IS 'Partition number (0-9999 by default)';
COMMENT ON COLUMN wh_partition_assignments.instance_id IS 'Instance that owns this partition';
COMMENT ON COLUMN wh_partition_assignments.assigned_at IS 'When this partition was assigned to the instance';
COMMENT ON COLUMN wh_partition_assignments.last_heartbeat IS 'Last heartbeat timestamp for this partition assignment';
