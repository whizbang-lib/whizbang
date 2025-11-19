-- Whizbang Messaging Infrastructure - PostgreSQL Schema
-- Description: Schema for inbox, outbox, request/response store, event store, and sequences

-- Inbox table for message deduplication (ExactlyOnce receiving)
-- Uses 3-column JSONB pattern (event_data, metadata, scope) like event store
CREATE TABLE IF NOT EXISTS whizbang_inbox (
  message_id UUID PRIMARY KEY,
  handler_name VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB,
  processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_whizbang_inbox_processed_at ON whizbang_inbox(processed_at);
CREATE INDEX IF NOT EXISTS ix_whizbang_inbox_event_type ON whizbang_inbox(event_type);

-- Outbox table for transactional outbox pattern (ExactlyOnce sending)
-- Uses 3-column JSONB pattern (event_data, metadata, scope) like event store
CREATE TABLE IF NOT EXISTS whizbang_outbox (
  message_id UUID PRIMARY KEY,
  destination VARCHAR(500) NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  published_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_whizbang_outbox_published_at ON whizbang_outbox(published_at) WHERE published_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_whizbang_outbox_event_type ON whizbang_outbox(event_type);
CREATE INDEX IF NOT EXISTS ix_whizbang_outbox_created ON whizbang_outbox(created_at);

-- Request/Response store for request-response pattern on pub/sub transports
CREATE TABLE IF NOT EXISTS whizbang_request_response (
  correlation_id UUID PRIMARY KEY,
  request_id UUID NOT NULL,
  response_envelope TEXT NULL,
  expires_at TIMESTAMPTZ NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_whizbang_request_response_expires_at ON whizbang_request_response(expires_at);

-- Event store for streaming/replay capability
-- Uses 3-column JSONB pattern (event_data, metadata, scope)
-- Stream ID inferred from event's [AggregateId] property (UUID)
CREATE TABLE IF NOT EXISTS whizbang_event_store (
  seq_id BIGSERIAL PRIMARY KEY,
  event_id UUID NOT NULL UNIQUE,
  stream_id UUID NOT NULL,
  sequence_number BIGINT NOT NULL,
  event_type VARCHAR(500) NOT NULL,
  event_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT uq_whizbang_event_store_stream_sequence UNIQUE (stream_id, sequence_number)
);

-- Indexes
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_stream ON whizbang_event_store(stream_id, sequence_number);
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_type ON whizbang_event_store(event_type);
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_created ON whizbang_event_store(created_at);

-- Selective GIN index on metadata only (for searching correlation IDs, etc.)
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_metadata_gin ON whizbang_event_store USING GIN (metadata jsonb_path_ops);

-- Expression indexes for common queries
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_tenant ON whizbang_event_store((scope->>'tenant_id')) WHERE scope IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_whizbang_event_store_correlation ON whizbang_event_store((metadata->>'correlation_id'));

-- Sequence provider for monotonic sequence generation
CREATE TABLE IF NOT EXISTS whizbang_sequences (
  sequence_key VARCHAR(500) PRIMARY KEY,
  current_value BIGINT NOT NULL DEFAULT 0,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
