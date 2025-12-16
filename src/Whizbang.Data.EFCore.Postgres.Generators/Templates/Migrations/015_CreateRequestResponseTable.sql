-- Migration 015: Create request/response tracking table
-- Date: 2025-12-15
-- Description: Creates the wh_request_response table for async request/response pattern tracking

-- Request/Response - Async request/response tracking
CREATE TABLE IF NOT EXISTS wh_request_response (
  request_id UUID NOT NULL PRIMARY KEY,
  correlation_id UUID NOT NULL,
  request_type VARCHAR(500) NOT NULL,
  request_data JSONB NOT NULL,
  response_type VARCHAR(500) NULL,
  response_data JSONB NULL,
  status VARCHAR(50) NOT NULL DEFAULT 'Pending',
  created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  completed_at TIMESTAMPTZ NULL,
  expires_at TIMESTAMPTZ NULL
);

-- Index for correlation-based queries
CREATE INDEX IF NOT EXISTS idx_request_response_correlation ON wh_request_response (correlation_id);

-- Index for status-based queries (pending requests)
CREATE INDEX IF NOT EXISTS idx_request_response_status_created ON wh_request_response (status, created_at);

-- Index for expiration-based cleanup
CREATE INDEX IF NOT EXISTS idx_request_response_expires ON wh_request_response (expires_at);

-- Add comments for documentation
COMMENT ON COLUMN wh_request_response.request_id IS 'Unique request identifier (UUIDv7)';
COMMENT ON COLUMN wh_request_response.correlation_id IS 'Correlation ID linking request to response';
COMMENT ON COLUMN wh_request_response.request_type IS 'Fully-qualified request type name';
COMMENT ON COLUMN wh_request_response.request_data IS 'Serialized request payload (JSONB)';
COMMENT ON COLUMN wh_request_response.response_type IS 'Fully-qualified response type name (null if pending)';
COMMENT ON COLUMN wh_request_response.response_data IS 'Serialized response payload (JSONB, null if pending)';
COMMENT ON COLUMN wh_request_response.status IS 'Request status: Pending, Completed, TimedOut, Failed';
COMMENT ON COLUMN wh_request_response.created_at IS 'Timestamp when request was created';
COMMENT ON COLUMN wh_request_response.completed_at IS 'Timestamp when response was received';
COMMENT ON COLUMN wh_request_response.expires_at IS 'Timestamp when request expires (timeout)';
