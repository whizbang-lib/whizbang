-- Migration 015: Create wh_active_streams table for ephemeral stream coordination
-- Date: 2025-12-18
-- Description: Tracks which instance owns each active stream (outbox/inbox/perspectives)
--              Ephemeral: Only contains streams with pending work
--              Enables cross-subsystem coordination and sticky stream assignment
--              Streams are added when first work arrives and removed when all work completes

CREATE TABLE wh_active_streams (
  stream_id UUID PRIMARY KEY,
  partition_number INTEGER NOT NULL,
  assigned_instance_id UUID,  -- NULL indicates orphaned stream
  lease_expiry TIMESTAMPTZ,   -- NULL indicates no lease
  created_at TIMESTAMPTZ DEFAULT NOW() NOT NULL,
  updated_at TIMESTAMPTZ DEFAULT NOW() NOT NULL,

  CONSTRAINT fk_active_streams_instance
    FOREIGN KEY (assigned_instance_id)
    REFERENCES wh_service_instances(instance_id)
    ON DELETE CASCADE
);

-- Index for instance queries (which streams does instance X own?)
CREATE INDEX idx_active_streams_instance ON wh_active_streams(assigned_instance_id) WHERE assigned_instance_id IS NOT NULL;

-- Index for partition queries (which streams in partition X are available?)
CREATE INDEX idx_active_streams_partition ON wh_active_streams(partition_number) WHERE assigned_instance_id IS NULL;

-- Index for lease expiry queries (scan by lease_expiry for expired lease detection)
CREATE INDEX idx_active_streams_lease_expiry ON wh_active_streams(lease_expiry) WHERE lease_expiry IS NOT NULL;

COMMENT ON TABLE wh_active_streams IS 'Ephemeral coordination table tracking which instance owns each active stream. Streams are added when first work arrives and removed when all work completes. Provides sticky assignment and cross-subsystem coordination.';
COMMENT ON COLUMN wh_active_streams.stream_id IS 'UUIDv7 stream identifier - naturally time-ordered';
COMMENT ON COLUMN wh_active_streams.partition_number IS 'Partition number (0-9999) computed via compute_partition(stream_id, partition_count)';
COMMENT ON COLUMN wh_active_streams.assigned_instance_id IS 'Instance that currently owns this stream. NULL = orphaned (available for claiming)';
COMMENT ON COLUMN wh_active_streams.lease_expiry IS 'When the lease expires. NULL = no lease. Expired leases can be claimed by other instances.';
COMMENT ON COLUMN wh_active_streams.created_at IS 'When this stream was first added to active streams table';
COMMENT ON COLUMN wh_active_streams.updated_at IS 'Last time this stream assignment or lease was updated';
