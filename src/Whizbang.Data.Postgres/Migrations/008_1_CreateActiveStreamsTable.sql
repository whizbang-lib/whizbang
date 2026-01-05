-- Migration: 008_1_CreateActiveStreamsTable.sql
-- Date: 2025-12-25
-- Description: Creates wh_active_streams table for ephemeral stream ownership coordination.
--              This table tracks which instance owns each active stream, enabling sticky
--              assignment and cross-subsystem coordination (outbox/inbox/perspectives).
-- Dependencies: None
-- Note: This table was previously documented as being created from C# schema (ActiveStreamsSchema.cs),
--       but decomposed functions require it to exist, so we're creating it via migration.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_active_streams (
  -- Primary key
  stream_id UUID PRIMARY KEY,

  -- Ownership and coordination
  partition_number INTEGER NOT NULL,
  assigned_instance_id UUID,  -- NULL indicates orphaned stream
  lease_expiry TIMESTAMPTZ,   -- NULL indicates no lease

  -- Timestamps
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_activity_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Index for querying by assigned instance
CREATE INDEX IF NOT EXISTS idx_active_streams_instance
ON __SCHEMA__.wh_active_streams (assigned_instance_id)
WHERE assigned_instance_id IS NOT NULL;

-- Index for finding orphaned streams (partition-based load balancing)
CREATE INDEX IF NOT EXISTS idx_active_streams_partition
ON __SCHEMA__.wh_active_streams (partition_number)
WHERE assigned_instance_id IS NULL;

-- Index for finding expired leases
CREATE INDEX IF NOT EXISTS idx_active_streams_lease_expiry
ON __SCHEMA__.wh_active_streams (lease_expiry)
WHERE lease_expiry IS NOT NULL;

COMMENT ON TABLE __SCHEMA__.wh_active_streams IS
'Ephemeral coordination table tracking which instance owns each active stream. Enables sticky assignment and cross-subsystem coordination for outbox, inbox, and perspective processing. Per-schema table allows multiple services to independently track stream ownership.';

COMMENT ON COLUMN __SCHEMA__.wh_active_streams.stream_id IS
'Unique identifier for the stream (maps to wh_event_store.stream_id)';

COMMENT ON COLUMN __SCHEMA__.wh_active_streams.partition_number IS
'Partition number for load balancing (calculated from stream_id using compute_partition function)';

COMMENT ON COLUMN __SCHEMA__.wh_active_streams.assigned_instance_id IS
'Instance ID that currently owns this stream. NULL indicates orphaned stream available for claiming.';

COMMENT ON COLUMN __SCHEMA__.wh_active_streams.lease_expiry IS
'Timestamp when the current instance lease expires. NULL indicates no active lease.';

COMMENT ON COLUMN __SCHEMA__.wh_active_streams.last_activity_at IS
'Timestamp of last activity on this stream. Used for cleanup of completed streams.';
