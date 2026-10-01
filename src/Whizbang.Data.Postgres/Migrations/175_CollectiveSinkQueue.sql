-- Migration: 175_CollectiveSinkQueue
-- Date: 2026-09-29
-- Description: The collective sink queue: a sink stream's unprocessed collectives, in the order they apply (#963).
--
--   A collective event can carry an ordering key. Collectives sharing a key in one scope share one stream (the
--   stream id is derived from the scope and the key), so the collective routing in 061 already puts them on one
--   __collective__ sink stream, and the perspective worker already works one stream at a time. What it did not do
--   is apply that stream's collectives in commit order: the sink read the stream's events after its cursor in
--   event_id order. An event_id is minted before commit, so two producers can commit in the opposite order to their
--   ids; the earlier commit then applied last and won, and a later-committed collective whose id sat below the cursor
--   was not read at all.
--
--   This function is the queue the sink applies instead: every unprocessed __collective__ work row of the stream,
--   joined to its event, in commit_sequence order with an unstamped row last and event_id breaking ties. That is the
--   ORDER BY get_stream_events uses and the order a replay reads a stream in, so live and replay apply a key's
--   collectives in the same order. The sink applies the leading rows it holds and stops at the first it does not,
--   so a collective waiting its turn (leased elsewhere, or backing off after a failure) holds every collective
--   behind it in place.
--
--   A read, not a claim: leasing is unchanged, and the rows the sink applies are completed by event_work_id as
--   before. Served by uq_perspective_event's leading (stream_id, perspective_name) and the event store's primary key.
-- Dependencies: 000 (drop_all_overloads), 046 (commit_sequence), 061 (collective routing)
-- Objects: wh_collective_sink_queue
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_collective_sink_queue');

-- <docs>fundamentals/messaging/collective-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueue_TwoCollectivesSharingAKey_AreInCommitOrder_WhenIdsRunBackwardAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueue_LeavesOutProcessedRowsOtherPerspectivesAndOtherStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueue_UnstampedRowsComeLastInEventIdOrderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_collective_sink_queue(
  p_stream_id UUID
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID,
  out_commit_sequence BIGINT
) AS $$
  SELECT pe.event_work_id, pe.event_id, es.commit_sequence
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es
    ON es.stream_id = pe.stream_id
    AND es.event_id = pe.event_id
  WHERE pe.stream_id = p_stream_id
    AND pe.perspective_name = '__collective__'
    AND pe.processed_at IS NULL
  ORDER BY es.commit_sequence ASC NULLS LAST, es.event_id;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_collective_sink_queue(UUID) IS
  'The collective sink queue of one stream (#963): its unprocessed __collective__ work rows with their events, in '
  'commit_sequence order (unstamped last, event_id breaking ties), the order the stream''s collectives apply and the '
  'order a replay reads the stream in. A read, not a claim.';
