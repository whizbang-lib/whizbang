-- Migration: 151_PriorityOnTheWire
-- Date: 2026-09-10
-- Description: Priority step 1 on the wire. fetch_outbox_batch is re-created VERBATIM from 096 plus the row's
--   priority column, so the outbox drain publishes the number the producer declared instead of rebuilding the wire
--   envelope without it (every consumer received a declared message as undeclared). recover_dead_letter is re-created
--   VERBATIM from 125 with every row it re-creates declared BACKGROUND: recovery is repair work nobody waits on, and
--   a recovered row that re-entered at the standard number sat ahead of live work.
-- Dependencies: 149_MessagePriority (the column), 096_OutboxDrainFetchByteBudget (fetch_outbox_batch), 125_RecoveryCountsItsRedelivery (recover_dead_letter)
-- Objects: fetch_outbox_batch, recover_dead_letter
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from Migrations/constants.txt at apply time (README rule 12).

-- The RETURNS TABLE changes, which CREATE OR REPLACE cannot do: drop every overload first (the 4-argument one 096 left).
SELECT __SCHEMA__.drop_all_overloads('fetch_outbox_batch');

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
  error TEXT,
  -- 151 (priority step 1 on the wire): the row's number, so the drain publishes what the producer declared.
  priority INTEGER
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
        OR ((o.stream_id IS NULL OR o.stream_id = __EMPTY_UUID__::uuid)
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
    b.error,
    b.priority
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.es_commit_sequence ASC NULLS LAST, b.message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_outbox_batch(UUID[], UUID, INTEGER, BIGINT) IS
'Per-stream-id payload fetch for OutboxDrainWorker, bounded by row count (p_max_per_stream) and payload bytes (p_max_bytes, NULL = unbounded). Returns every column the drain publishes from, including the row''s priority (151) so the wire envelope carries the number the producer declared. Always returns at least one row per stream; the byte budget trims the tail of a slice, never the head.';

CREATE OR REPLACE FUNCTION __SCHEMA__.recover_dead_letter(
  p_dead_letter_id UUID
) RETURNS BOOLEAN AS $$
DECLARE
  v_source_table   TEXT;
  v_source_id      UUID;
  v_redelivered    BIGINT;
  v_stream_id      UUID;
  v_message_type   TEXT;
  v_destination    TEXT;
  v_perspective    TEXT;
  v_envelope       JSONB;
  v_metadata       JSONB;
  v_event_data     JSONB;
  v_partition      INTEGER;
BEGIN
  -- Atomically claim the row by transitioning to Recovering AND fetch its forensic
  -- payload. If another worker raced us OR the row is already terminal, the UPDATE
  -- affects zero rows and we return false.
  WITH claimed AS (
    UPDATE __SCHEMA__.wh_dead_letters
    SET recovery_status = 1,                  -- Recovering
        recovery_attempts = recovery_attempts + 1,
        last_recovery_at = NOW()
    WHERE dead_letter_id = p_dead_letter_id
      AND recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
      AND recovered_at IS NULL
    RETURNING source_table, source_id, stream_id, message_type, destination, perspective_name, envelope, metadata
  )
  SELECT c.source_table, c.source_id, c.stream_id, c.message_type, c.destination, c.perspective_name, c.envelope, c.metadata
  INTO v_source_table, v_source_id, v_stream_id, v_message_type, v_destination, v_perspective, v_envelope, v_metadata
  FROM claimed c;

  IF v_source_table IS NULL THEN
    RETURN FALSE;  -- already claimed by another worker or already terminal
  END IF;

  -- v0.657 slice 4: DLQ replay self-repair. If the DLQ row preserves a
  -- Guid.Empty stream_id (a pattern observed in production — producer bug from before the
  -- v0.657 storage-time Reject guard shipped), normalize to NULL on the
  -- INSERT back into the source table. Otherwise the recovered row immediately
  -- re-sticks under the same silent-stuck pattern that DLQ'd it in the first
  -- place: stream_id=Empty bypasses the NULL-only `??` coalesce in the C#
  -- coordinator (pre-v0.657) and the slice-3 coordinator backstop sees Empty
  -- as "no real stream identity" → WorkId fallback. NULL is the documented
  -- singleton-stream marker; that's the value we want on recovery.
  IF v_stream_id = __EMPTY_UUID__::uuid THEN
    v_stream_id := NULL;
  END IF;

  -- Extract the original event_data from the envelope JSONB.
  v_event_data := v_envelope -> 'event_data';
  v_partition := CASE WHEN v_stream_id IS NULL THEN 0 ELSE 0 END;  -- partition recomputed on store_*_messages path; fixed to 0 here is fine because claim_orphaned_* recomputes via wh_active_streams

  -- Re-emit into the appropriate source table with attempts=0.
  IF v_source_table = 'wh_outbox' THEN
    INSERT INTO __SCHEMA__.wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number, priority)
    VALUES (v_source_id, v_destination, v_message_type, 'recovered', v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition, __PRIORITY_BACKGROUND__)
    ON CONFLICT (message_id) DO NOTHING;  -- already re-published; idempotent
  ELSIF v_source_table = 'wh_inbox' THEN
    INSERT INTO __SCHEMA__.wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number, priority)
    VALUES (v_source_id, COALESCE(v_perspective, 'recovered'), v_message_type, v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition, __PRIORITY_BACKGROUND__)
    ON CONFLICT (message_id) DO NOTHING;
    -- 125: count the re-delivery this recovery just caused.
    --
    -- 121 replaced count-based poison detection with an observation counter the framework keeps
    -- itself, because a broker delivery counter cannot bound a redelivery loop. store_inbox_messages
    -- increments wh_message_deduplication.observation_count on every arrival and
    -- PoisonMessageDetector reads that count. Recovery re-delivers by INSERTing straight into
    -- wh_inbox, which bypasses that path, so before this every recovery-driven arrival was
    -- invisible: a message could be recovered without limit because no pass was ever observed and
    -- attempts is reset to 0 on the way in.
    --
    -- Charged only when the INSERT actually inserted. ON CONFLICT DO NOTHING means a double-recovery
    -- race delivered nothing, and charging for a delivery that did not happen would push a healthy
    -- message toward quarantine.
    GET DIAGNOSTICS v_redelivered = ROW_COUNT;
    IF v_redelivered > 0 THEN
      INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
        (message_id, first_seen_at, observation_count)
      VALUES (v_source_id, NOW(), 1)
      ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
        SET observation_count = dedup.observation_count + 1;
    END IF;

  ELSIF v_source_table = 'wh_perspective_events' THEN
    -- Perspective recovery uses the event_id snapshot to recreate the work row.
    INSERT INTO __SCHEMA__.wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    VALUES (v_source_id, v_stream_id, v_perspective, (v_envelope ->> 'event_id')::UUID, v_partition, 0, 0, NOW(), __PRIORITY_BACKGROUND__)
    ON CONFLICT (event_work_id) DO NOTHING;
  ELSIF v_source_table = 'broker' THEN
    -- Broker-imported rows (wh_import_dead_letter, migration 119) re-enter through the inbox
    -- front door: normal dispatch, composite fan-out, and the internal max-attempts ladder all
    -- apply unchanged. A row that still cannot be processed on the current build parks again in
    -- wh_dead_letters via move_to_dead_letters — visible, fingerprinted, attempt-accounted —
    -- instead of orbiting the broker's opaque DLQ.
    INSERT INTO __SCHEMA__.wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number, priority)
    VALUES (v_source_id, 'broker-recovered', v_message_type, v_event_data, v_metadata, 0, 0, NOW(), v_stream_id, v_partition, __PRIORITY_BACKGROUND__)
    ON CONFLICT (message_id) DO NOTHING;
    -- 125: count the re-delivery this recovery just caused.
    --
    -- 121 replaced count-based poison detection with an observation counter the framework keeps
    -- itself, because a broker delivery counter cannot bound a redelivery loop. store_inbox_messages
    -- increments wh_message_deduplication.observation_count on every arrival and
    -- PoisonMessageDetector reads that count. Recovery re-delivers by INSERTing straight into
    -- wh_inbox, which bypasses that path, so before this every recovery-driven arrival was
    -- invisible: a message could be recovered without limit because no pass was ever observed and
    -- attempts is reset to 0 on the way in.
    --
    -- Charged only when the INSERT actually inserted. ON CONFLICT DO NOTHING means a double-recovery
    -- race delivered nothing, and charging for a delivery that did not happen would push a healthy
    -- message toward quarantine.
    GET DIAGNOSTICS v_redelivered = ROW_COUNT;
    IF v_redelivered > 0 THEN
      INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
        (message_id, first_seen_at, observation_count)
      VALUES (v_source_id, NOW(), 1)
      ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
        SET observation_count = dedup.observation_count + 1;
    END IF;

  ELSE
    -- Unknown source table — leave as Recovering for an operator to investigate.
    RAISE WARNING 'recover_dead_letter: unsupported source table %', v_source_table;
    RETURN FALSE;
  END IF;

  -- Mark Recovered.
  UPDATE __SCHEMA__.wh_dead_letters
  SET recovery_status = 3,
      recovered_at = NOW(),
      retried_on_generations =
        CASE WHEN generation = ANY(retried_on_generations) THEN retried_on_generations
             ELSE array_append(retried_on_generations, generation) END
  WHERE dead_letter_id = p_dead_letter_id;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.recover_dead_letter(UUID) IS
'Re-creates a dead-lettered row in its source table (wh_outbox, wh_inbox, wh_perspective_events, or the inbox front door for a broker import) and marks the dead letter recovered. Every re-created row is declared BACKGROUND (151): recovery is repair work nobody waits on. Charges the redelivery to wh_message_deduplication.observation_count when the insert delivered (125).';
