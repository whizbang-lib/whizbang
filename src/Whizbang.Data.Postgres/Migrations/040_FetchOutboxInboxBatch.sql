-- Migration: 040_FetchOutboxInboxBatch.sql
-- Date: 2026-04-30
-- Description: Per-stream-id payload-fetch functions used by the new OutboxDrainWorker /
--              InboxDrainWorker. ClaimWorker emits stream_ids only via claim_work; the
--              drainer worker reads the stream_id from a channel and calls these functions
--              to pull the actual leased message rows for that stream in FIFO (created_at)
--              order. Restores the archive-specified design: poller cheap (small payload),
--              drainer fetches bodies on demand, per-stream serialization via channel reader.
-- Dependencies: 020 (wh_outbox), 021 (wh_inbox)

SELECT __SCHEMA__.drop_all_overloads('fetch_outbox_batch');

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_outbox_batch(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  RETURN QUERY
  WITH ranked AS (
    SELECT
      o.*,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.created_at, o.message_id) AS rank_in_stream
    FROM __SCHEMA__.wh_outbox o
    WHERE o.stream_id = ANY(p_stream_ids)
      AND o.instance_id = p_instance_id
      AND o.lease_expiry > NOW()
      AND o.processed_at IS NULL
      AND o.published_at IS NULL  -- skip debug-mode forensic rows
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= NOW())
  )
  SELECT
    r.message_id,
    r.stream_id,
    r.destination::VARCHAR(200),
    r.message_type::VARCHAR(500),
    r.envelope_type::VARCHAR(500),
    r.event_data::TEXT,
    r.metadata,
    r.scope,
    r.status,
    r.attempts,
    r.partition_number,
    r.is_event
  FROM ranked r
  WHERE r.rank_in_stream <= p_max_per_stream
  ORDER BY r.stream_id, r.created_at, r.message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_outbox_batch IS
'Per-stream-id payload fetch for OutboxDrainWorker. Returns leased outbox rows for the given stream_ids (owned by p_instance_id, lease still valid, not yet processed or debug-retained), ordered by (stream_id, created_at, message_id) for stream-FIFO. p_max_per_stream caps how many rows per stream to return so one busy stream cannot monopolize a fetch. ClaimWorker emits stream_ids via claim_work; this is the drainer''s payload fetch.';

SELECT __SCHEMA__.drop_all_overloads('fetch_inbox_batch');

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_inbox_batch(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  handler_name VARCHAR(200),
  message_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  RETURN QUERY
  WITH ranked AS (
    SELECT
      i.*,
      ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS rank_in_stream
    FROM __SCHEMA__.wh_inbox i
    WHERE i.stream_id = ANY(p_stream_ids)
      AND i.instance_id = p_instance_id
      AND i.lease_expiry > NOW()
      AND i.processed_at IS NULL  -- inbox uses processed_at as both production-marker and debug-kept-marker
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= NOW())
  )
  SELECT
    r.message_id,
    r.stream_id,
    r.handler_name::VARCHAR(200),
    r.message_type::VARCHAR(500),
    r.event_data::TEXT,
    r.metadata,
    r.scope,
    r.status,
    r.attempts,
    r.partition_number,
    r.is_event
  FROM ranked r
  WHERE r.rank_in_stream <= p_max_per_stream
  ORDER BY r.stream_id, r.received_at, r.message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_inbox_batch IS
'Per-stream-id payload fetch for InboxDrainWorker. Returns leased inbox rows for the given stream_ids (owned by p_instance_id, lease still valid, not yet processed), ordered by (stream_id, received_at, message_id) for stream-FIFO. p_max_per_stream caps per-stream return count. Inbox uses processed_at as both the production-marker (DELETE on production-mode complete) and the debug-kept-marker (eligible_inbox already filters processed_at IS NULL). Mirror of fetch_outbox_batch.';
