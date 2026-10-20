-- Migration: 215_CollectiveSinkQueueReadsOnlyItsHead.sql
-- Date: 2026-10-20
-- Description: A collective sink run reads the head of its stream's queue, not the whole queue (#1524).
--
--              wh_collective_sink_queue (175) returns every unprocessed __collective__ work row of a stream, joined
--              to its event and sorted by commit_sequence. The order lives on wh_event_store, so no index on the
--              work rows can serve it: each call reads and sorts the whole queue. A sink run applies only the
--              leading rows it holds, so with Q rows queued on one ordering key and B rows per claim, draining the
--              key reads about Q*Q/B rows (100k queued at 100 per claim is about 100M rows read and joined).
--
--              A LIMIT on that function is not a fix. The worker completes a held row that the queue does not
--              return, because such a row was already applied; a LIMIT that hid a held row would complete it
--              without applying it, and the collective would be lost. So the two questions the worker asks are
--              answered separately, and each costs what it returns:
--
--                * wh_collective_sink_queue_head: the queue in order, from the start, while each row is one the
--                  caller names, and the first row that is not. It stops there. A run that holds the head of its
--                  queue reads its own rows and one more.
--                * wh_collective_sink_queue_members: which of the named work rows, or of the rows of the named
--                  events, are still queued. Served by the primary key and uq_perspective_event.
--
--              The order. Within one stream, commit_sequence follows the stream's versions (197: the stamp walks
--              each stream from its lowest unstamped version and stops at the first it cannot number), and a
--              stream's versions are taken under the stream's lock, so a later version is a later commit. Version
--              order is therefore commit order for every stamped row, and for unstamped rows it is the order
--              they will be stamped in, which is the order a replay will read them in. (175 broke ties between
--              unstamped rows by event_id, which is minted before commit and can run backwards; the head follows
--              the version instead, so a live apply and a later replay agree on those rows too.)
--
--              To walk that order on an index, the work row carries its event's version: stream_version, set at
--              insert by a trigger for __collective__ rows only, from the event the row names. A trigger rather
--              than the routing functions, because collective work rows are written by the routing in
--              store_outbox_messages and by dead-letter recovery, and a rolling deploy can still run an older
--              build's copy of either; the trigger fills the column on every path. Rows already queued are
--              filled here. A row whose event is not in the store has no version and is not in the queue, as
--              before (175 joins the event store).
--
--              wh_collective_sink_queue is left as it is, for builds that still call it.
-- Dependencies: 009 (wh_perspective_events), 046 (commit_sequence), 175 (the queue), 197 (stamp order)
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_perspective_events ADD COLUMN IF NOT EXISTS stream_version BIGINT;

COMMENT ON COLUMN __SCHEMA__.wh_perspective_events.stream_version IS
  'The version of the event this row applies, within its stream; set at insert for __collective__ rows only (#1524). '
  'Orders a collective sink stream''s queue on idx_perspective_event_collective_queue.';

-- <docs>fundamentals/messaging/collective-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueueHead_CollectiveRowsCarryTheirEventsVersionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_perspective_events_fill_stream_version() RETURNS TRIGGER AS $$
BEGIN
  NEW.stream_version := (
    SELECT es.version
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = NEW.stream_id
      AND es.event_id = NEW.event_id
  );
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_perspective_events_fill_stream_version() IS
  'Sets stream_version on a new __collective__ work row from its event (#1524), so the sink queue walks an index.';

DROP TRIGGER IF EXISTS wh_perspective_events_fill_stream_version_trg ON __SCHEMA__.wh_perspective_events;
CREATE TRIGGER wh_perspective_events_fill_stream_version_trg
  BEFORE INSERT ON __SCHEMA__.wh_perspective_events
  FOR EACH ROW
  WHEN (NEW.perspective_name = '__collective__' AND NEW.stream_version IS NULL)
  EXECUTE FUNCTION __SCHEMA__._wh_perspective_events_fill_stream_version();

-- Rows already queued. Processed rows are not in any queue and are left alone.
UPDATE __SCHEMA__.wh_perspective_events pe
SET stream_version = es.version
FROM __SCHEMA__.wh_event_store es
WHERE pe.perspective_name = '__collective__'
  AND pe.processed_at IS NULL
  AND pe.stream_version IS NULL
  AND es.stream_id = pe.stream_id
  AND es.event_id = pe.event_id;

-- Only unprocessed collective rows: every other work row pays nothing for it.
CREATE INDEX IF NOT EXISTS idx_perspective_event_collective_queue
  ON __SCHEMA__.wh_perspective_events (stream_id, stream_version)
  WHERE perspective_name = '__collective__' AND processed_at IS NULL AND stream_version IS NOT NULL;

COMMENT ON INDEX __SCHEMA__.idx_perspective_event_collective_queue IS
  'A collective sink stream''s queue in apply order (#1524), walked by wh_collective_sink_queue_head.';

-- ONE overload: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('wh_collective_sink_queue_head');

-- <docs>fundamentals/messaging/collective-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueueHead_StopsAtTheFirstRowTheCallerDoesNotNameAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueueHead_IsInVersionOrder_WhenIdsRunBackwardAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueueHead_ReadsOnlyTheHead_NotTheWholeQueueAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_collective_sink_queue_head(
  p_stream_id UUID,
  p_through UUID[]
) RETURNS TABLE(
  out_event_work_id UUID,
  out_event_id UUID,
  out_commit_sequence BIGINT
) AS $$
DECLARE
  v_row RECORD;
BEGIN
  -- A FOR loop reads its query through a cursor, so the walk stops reading the index where the loop exits.
  FOR v_row IN
    SELECT pe.event_work_id, pe.event_id
    FROM __SCHEMA__.wh_perspective_events pe
    WHERE pe.stream_id = p_stream_id
      AND pe.perspective_name = '__collective__'
      AND pe.processed_at IS NULL
      AND pe.stream_version IS NOT NULL
    ORDER BY pe.stream_version
  LOOP
    out_event_work_id := v_row.event_work_id;
    out_event_id := v_row.event_id;
    out_commit_sequence := (
      SELECT es.commit_sequence
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = p_stream_id
        AND es.event_id = v_row.event_id
    );
    RETURN NEXT;
    EXIT WHEN NOT (v_row.event_work_id = ANY(COALESCE(p_through, '{}'::UUID[])));
  END LOOP;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_collective_sink_queue_head(UUID, UUID[]) IS
  'The head of one stream''s collective sink queue (#1524): its unprocessed __collective__ work rows in apply order '
  '(the stream''s version order, which is its commit order), while each row is one p_through names, then the first '
  'row it does not. Reads only what it returns. A read, not a claim.';

SELECT __SCHEMA__.drop_all_overloads('wh_collective_sink_queue_members');

-- <docs>fundamentals/messaging/collective-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:SinkQueueMembers_AnswersForNamedRowsAndEventsOnlyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_collective_sink_queue_members(
  p_stream_id UUID,
  p_work_ids UUID[],
  p_event_ids UUID[]
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
    AND pe.stream_version IS NOT NULL
    AND (pe.event_work_id = ANY(COALESCE(p_work_ids, '{}'::UUID[]))
      OR pe.event_id = ANY(COALESCE(p_event_ids, '{}'::UUID[])));
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_collective_sink_queue_members(UUID, UUID[], UUID[]) IS
  'Which of the named work rows, or rows of the named events, are still in one stream''s collective sink queue '
  '(#1524). Reads only the named rows, in no particular order. A read, not a claim.';
