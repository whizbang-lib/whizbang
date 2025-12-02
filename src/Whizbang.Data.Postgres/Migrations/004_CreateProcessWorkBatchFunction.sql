-- Migration: Create process_work_batch function for lease-based coordination
-- Date: 2025-12-02
-- Description: Single SQL function that handles all work coordination operations:
--              - Update instance heartbeat
--              - Mark completed/failed messages (outbox and inbox separately)
--              - Claim orphaned work (expired leases)
--              - Return orphaned work to process
--              This minimizes database round-trips and ensures atomic operations

CREATE OR REPLACE FUNCTION process_work_batch(
  p_instance_id UUID,
  p_outbox_completed_ids UUID[] DEFAULT ARRAY[]::UUID[],
  p_outbox_failed_messages JSONB DEFAULT '[]'::JSONB,
  p_inbox_completed_ids UUID[] DEFAULT ARRAY[]::UUID[],
  p_inbox_failed_messages JSONB DEFAULT '[]'::JSONB,
  p_lease_seconds INT DEFAULT 300
)
RETURNS TABLE (
  source VARCHAR(10),  -- 'outbox' or 'inbox'
  message_id UUID,
  destination VARCHAR(500),
  event_type VARCHAR(500),
  event_data JSONB,
  metadata JSONB,
  scope JSONB,
  attempts INTEGER
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  v_now TIMESTAMPTZ;
BEGIN
  -- Calculate lease expiry timestamp
  v_now := NOW();
  v_lease_expiry := v_now + (p_lease_seconds || ' seconds')::INTERVAL;

  -- 1. Update instance heartbeat (upsert)
  INSERT INTO wb_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at)
  VALUES (
    p_instance_id,
    'WorkCoordinator',  -- Will be updated by caller with actual service name
    'localhost',        -- Will be updated by caller with actual hostname
    0,                  -- Will be updated by caller with actual PID
    v_now
  )
  ON CONFLICT (instance_id)
  DO UPDATE SET last_heartbeat_at = v_now;

  -- 2. Mark completed outbox messages
  IF array_length(p_outbox_completed_ids, 1) > 0 THEN
    UPDATE wb_outbox
    SET status = 'Published',
        published_at = v_now,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE message_id = ANY(p_outbox_completed_ids)
      AND instance_id = p_instance_id;
  END IF;

  -- 3. Mark failed outbox messages
  IF jsonb_array_length(p_outbox_failed_messages) > 0 THEN
    UPDATE wb_outbox
    SET status = 'Failed',
        attempts = attempts + 1,
        instance_id = NULL,
        lease_expiry = NULL
    FROM jsonb_array_elements(p_outbox_failed_messages) AS failed(item)
    WHERE wb_outbox.message_id = (failed.item->>'MessageId')::UUID
      AND wb_outbox.instance_id = p_instance_id;
  END IF;

  -- 4. Mark completed inbox messages
  IF array_length(p_inbox_completed_ids, 1) > 0 THEN
    UPDATE wb_inbox
    SET status = 'Completed',
        processed_at = v_now,
        instance_id = NULL,
        lease_expiry = NULL
    WHERE message_id = ANY(p_inbox_completed_ids)
      AND instance_id = p_instance_id;
  END IF;

  -- 5. Mark failed inbox messages
  IF jsonb_array_length(p_inbox_failed_messages) > 0 THEN
    UPDATE wb_inbox
    SET status = 'Failed',
        instance_id = NULL,
        lease_expiry = NULL
    FROM jsonb_array_elements(p_inbox_failed_messages) AS failed(item)
    WHERE wb_inbox.message_id = (failed.item->>'MessageId')::UUID
      AND wb_inbox.instance_id = p_instance_id;
  END IF;

  -- 6. Claim and return orphaned outbox work
  RETURN QUERY
  WITH claimed_outbox AS (
    UPDATE wb_outbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        status = 'Publishing',
        attempts = attempts + 1
    WHERE message_id IN (
      SELECT message_id
      FROM wb_outbox
      WHERE (
        -- Orphaned work: lease expired
        (status = 'Publishing' AND lease_expiry < v_now)
        OR
        -- New work: never processed
        (status = 'Pending' AND instance_id IS NULL)
      )
      ORDER BY created_at ASC
      LIMIT 100  -- Configurable batch size
      FOR UPDATE SKIP LOCKED  -- Skip rows locked by other instances
    )
    RETURNING
      'outbox'::VARCHAR AS source,
      message_id,
      destination,
      event_type,
      event_data,
      metadata,
      scope,
      attempts
  )
  SELECT * FROM claimed_outbox;

  -- 7. Claim and return orphaned inbox work
  RETURN QUERY
  WITH claimed_inbox AS (
    UPDATE wb_inbox
    SET instance_id = p_instance_id,
        lease_expiry = v_lease_expiry,
        status = 'Processing'
    WHERE message_id IN (
      SELECT message_id
      FROM wb_inbox
      WHERE (
        -- Orphaned work: lease expired
        (status = 'Processing' AND lease_expiry < v_now)
        OR
        -- New work: never processed
        (status = 'Pending' AND instance_id IS NULL)
      )
      AND processed_at IS NULL  -- Not already completed
      ORDER BY received_at ASC
      LIMIT 100  -- Configurable batch size
      FOR UPDATE SKIP LOCKED  -- Skip rows locked by other instances
    )
    RETURNING
      'inbox'::VARCHAR AS source,
      message_id,
      NULL::VARCHAR AS destination,  -- Inbox doesn't have destination
      event_type,
      event_data,
      metadata,
      scope,
      0 AS attempts  -- Inbox doesn't track attempts in same way
  )
  SELECT * FROM claimed_inbox;

END;
$$ LANGUAGE plpgsql;

-- Add comment for documentation
COMMENT ON FUNCTION process_work_batch IS 'Atomic work coordination: heartbeat, mark completed/failed, claim orphaned work';
