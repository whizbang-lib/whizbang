-- 091_DrainFetchByteBudget.sql
--
-- Bound the drain fetch by BYTES as well as by row count.
--
-- fetch_inbox_batch/fetch_outbox_batch cap the slice at p_max_per_stream ROWS. A count-only bound
-- assumes rows are roughly the same size. Control-plane traffic breaks that assumption badly:
-- integrity manifests carry up to MaxDigestsPerManifest digests each, so one row can be six orders
-- of magnitude larger than an ordinary command. A backlog of them turns "fetch 100 rows" into tens
-- of megabytes of JSON in a single round trip, which then deserializes into several times that on
-- the managed heap, per concurrent drain consumer.
--
-- Observed live: services carrying 540-700 queued manifests totalling 83-105 MB each were
-- OOMKilled on every start (exit 137, ~8 restarts apiece) and never drained the backlog — the
-- fetch that was supposed to make progress was the thing that killed the process, so the messages
-- came back on redelivery and the loop repeated.
--
-- This is the same defect the redelivery pump already fixed for its side (see
-- RedeliveryPumpOptions.MaxBytesPerComposite: "a count-only bound lets large-bodied histories
-- build"). The drain fetch never got the equivalent.
--
-- KEY INVARIANT — always return at least one row per stream. A message larger than the whole
-- budget must still be delivered, or it can never be processed and the stream stalls forever. The
-- budget therefore trims the TAIL of a slice, never the head.
--
-- p_max_bytes NULL (the default) preserves the previous behavior exactly, so this is additive for
-- any caller that has not opted in.
--
-- Dependencies: 040 (fetch_outbox_batch / fetch_inbox_batch)

-- Adding a defaulted parameter CREATES AN OVERLOAD rather than replacing the function, and a
-- 3-argument call against both would then be ambiguous (42725). Drop the old arity first — the
-- same precaution migration 085 took for close_stream.
DROP FUNCTION IF EXISTS __SCHEMA__.fetch_inbox_batch(UUID[], UUID, INTEGER);

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_inbox_batch(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
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
  is_event BOOLEAN,
  error TEXT
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Ordering invariant: sort by (stream_id, message_id). UUIDv7 message_ids ARE chronological;
  -- received_at is wall-clock at insert and may diverge under parallel transport delivery.
  -- See plans/ordered-stream-invariant.md.
  RETURN QUERY
  WITH ranked AS (
    SELECT
      i.*,
      ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.message_id) AS rank_in_stream
    FROM __SCHEMA__.wh_inbox i
    -- v0.658 slice 7: mirror of fetch_outbox_batch's Empty/NULL stream handling —
    -- see the matching comment in the outbox query for the full rationale.
    WHERE (
        i.stream_id = ANY(p_stream_ids)
        OR ((i.stream_id IS NULL OR i.stream_id = '00000000-0000-0000-0000-000000000000'::uuid)
            AND i.message_id = ANY(p_stream_ids))
      )
      AND i.instance_id = p_instance_id
      AND i.lease_expiry > NOW()
      AND i.processed_at IS NULL  -- inbox uses processed_at as both production-marker and debug-kept-marker
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= NOW())
  ),
  -- Running byte total in the SAME order the rows are returned, so the cut is a suffix of the
  -- slice and stream-FIFO is preserved. Measured on the payload columns because those are what
  -- cross the wire and land on the heap; the fixed-width columns are noise by comparison.
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id ORDER BY r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
    WHERE r.rank_in_stream <= p_max_per_stream
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.handler_name::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.error
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_inbox_batch(UUID[], UUID, INTEGER, BIGINT) IS
'Per-stream-id payload fetch for InboxDrainWorker, bounded by BOTH row count (p_max_per_stream) and payload bytes (p_max_bytes, NULL = unbounded as before). The byte budget trims the tail of a slice and always returns at least one row per stream, so an oversized message is still delivered rather than stalling its stream.';

-- The OUTBOX side has the same count-only bound and deserves the same treatment, but its
-- signature carries envelope_type plus JOINed event-store columns, so it must be extracted
-- verbatim rather than retyped — a transcription slip there breaks the publish path. Deliberately
-- left for a follow-up; the inbox is where the observed backlog and the OOMs actually were.
