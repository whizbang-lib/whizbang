-- 096_OutboxDrainFetchByteBudget.sql
--
-- Bound the OUTBOX drain fetch by BYTES as well as by row count — the sibling of 091's inbox
-- bound, completing the follow-up 091 explicitly deferred.
--
-- Same defect, other direction: an origin serving a storm of queued redelivery requests drains
-- its outbox in count-bounded slices, and control-plane rows (redelivery composites carrying
-- whole event pages) dwarf ordinary commands by orders of magnitude — "fetch 100 rows" becomes
-- tens of megabytes per round trip, several times that on the managed heap, per concurrent drain
-- consumer. Observed live as an origin service OOM-looping through a raised memory limit while
-- productively serving backfill: each restart re-fetched the same oversized slice and died on it.
--
-- KEY INVARIANT — always return at least one row per stream. A message larger than the whole
-- budget must still be published, or it can never leave the outbox and the stream stalls forever.
-- The budget therefore trims the TAIL of a slice, never the head.
--
-- The running-byte window uses the SAME per-stream order as the returned rows
-- (es.commit_sequence ASC NULLS LAST, message_id — 040's publish-order invariant), so the cut is
-- a suffix of the slice and stream-FIFO publish order is preserved.
--
-- p_max_bytes NULL (the default) preserves the previous behavior exactly, so this is additive for
-- any caller that has not opted in.
--
-- Dependencies: 040 (fetch_outbox_batch), 091 (the inbox sibling + the budget pattern)

-- Adding a defaulted parameter CREATES AN OVERLOAD rather than replacing the function, and a
-- 3-argument call against both would then be ambiguous (42725). Drop the old arity first — the
-- same precaution 091 took for fetch_inbox_batch.
DROP FUNCTION IF EXISTS __SCHEMA__.fetch_outbox_batch(UUID[], UUID, INTEGER);

CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_outbox_batch(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
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
  is_event BOOLEAN,
  -- Slice 26.6b: JOINed event-store fields used by the publisher to populate envelope
  -- SourceServiceId + SourceCommitSequence before serializing to transport. NULL for
  -- non-event outbox rows; commit_sequence may be NULL if stamper hasn't caught up.
  commit_sequence BIGINT,
  origin_service_id UUID,
  origin_commit_sequence BIGINT,
  -- Slice 1 of release/v0.648.0-alpha.1: the row's existing error column (real
  -- exception text from the last process_outbox_failures cycle). The pre-publish
  -- DLQ gate uses this as errorText when promoting via move_to_dead_letters so
  -- the DLQ row's fingerprint reflects the real root cause instead of a
  -- meta-message that collapses every failure mode to one fingerprint.
  error TEXT
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Ordering invariant: sort by (stream_id, commit_sequence NULLS LAST, message_id).
  -- Slice 26.9: live publish order should match the source's commit-completion order so
  -- downstream consumers see events in the same order across live + replay. For unstamped
  -- rows the tail fall-back is message_id (UUIDv7, monotonic-at-generation).
  RETURN QUERY
  WITH ranked AS (
    SELECT
      o.*,
      es.commit_sequence AS es_commit_sequence,
      es.origin_service_id AS es_origin_service_id,
      es.origin_commit_sequence AS es_origin_commit_sequence,
      ROW_NUMBER() OVER (
        PARTITION BY o.stream_id
        ORDER BY es.commit_sequence ASC NULLS LAST, o.message_id
      ) AS rank_in_stream
    FROM __SCHEMA__.wh_outbox o
    LEFT JOIN __SCHEMA__.wh_event_store es
      ON o.is_event AND es.event_id = o.message_id
    -- v0.658 slice 7: singleton-stream / Empty-stream rows are looked up by
    -- message_id-as-sentinel rather than stream_id. The coordinator's claim_work
    -- output emits message_id as the sentinel for rows whose stream_id is NULL
    -- (the documented singleton-stream marker) or Guid.Empty (the producer-side
    -- bug from a production forensic investigation — v0.657 slice 3's Empty→WorkId fallback). The
    -- pre-v0.658 filter `stream_id = ANY(p_stream_ids)` couldn't match either
    -- case: NULL=ANY is NULL/false, and Empty doesn't equal the message_id
    -- sentinel. The additive OR branch only fires when stream_id is non-routable,
    -- so real-stream rows still match exclusively via their stream_id.
    WHERE (
        o.stream_id = ANY(p_stream_ids)
        OR ((o.stream_id IS NULL OR o.stream_id = '00000000-0000-0000-0000-000000000000'::uuid)
            AND o.message_id = ANY(p_stream_ids))
      )
      AND o.instance_id = p_instance_id
      AND o.lease_expiry > NOW()
      AND o.processed_at IS NULL
      AND o.published_at IS NULL  -- skip debug-mode forensic rows
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= NOW())
  ),
  -- Running byte total in the SAME order the rows are returned, so the cut is a suffix of the
  -- slice and stream-FIFO publish order is preserved. Measured on the payload columns because
  -- those are what cross the wire and land on the heap; the fixed-width columns are noise by
  -- comparison.
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id
              ORDER BY r.es_commit_sequence ASC NULLS LAST, r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
    WHERE r.rank_in_stream <= p_max_per_stream
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.destination::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.envelope_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.es_commit_sequence,
    b.es_origin_service_id,
    b.es_origin_commit_sequence,
    b.error
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.es_commit_sequence ASC NULLS LAST, b.message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_outbox_batch(UUID[], UUID, INTEGER, BIGINT) IS
'Per-stream-id payload fetch for OutboxDrainWorker, bounded by BOTH row count (p_max_per_stream) and payload bytes (p_max_bytes, NULL = unbounded as before). The byte budget trims the tail of a slice in publish order and always returns at least one row per stream, so an oversized message is still published rather than stalling its stream.';
