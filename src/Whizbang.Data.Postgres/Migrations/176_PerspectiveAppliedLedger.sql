-- Migration: 176_PerspectiveAppliedLedger.sql
-- Date: 2026-09-29
-- Description: The applied-event ledger (#959): a durable record, shared by every instance, of which
--              events each perspective has applied, so a service can wait until its read model has an
--              event whoever published it.
--
--              Until now the only records of an apply were in-process (the sync tracker) or were gone
--              the moment the apply finished: a perspective's work row is DELETEd on completion, and a
--              deleted row looks exactly like one that was never created. A wait for an event that
--              arrived from another service therefore had nothing to ask and reported "synced" at once.
--
--              wh_perspective_applied holds (event_id, perspective_name) for each completed work row.
--              It is written by process_perspective_event_completions, in the same statement that
--              retires the work row, and by nothing else: a row exists only for an apply the worker
--              committed. The completion is flushed shortly after the model write commits, so the ledger
--              can lag an apply by the flush interval; it is never early. A collective event is applied
--              by the collective sink and recorded once, under the sink's name.
--
--              Rows are pruned an hour after they are recorded, at most 1000 per completion call, by the
--              same function. An event applied longer ago than that reads as "nothing to apply", which
--              settles a wait just the same.
--
--              wh_perspective_applied_status answers one wait: the event (by id, or by stream and local
--              version), and where it stands for one perspective.
-- Dependencies: 009 (wh_perspective_events), 015 (process_perspective_event_completions),
--               wh_event_store
-- Objects: wh_perspective_applied, process_perspective_event_completions, wh_perspective_applied_status
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_perspective_applied (
  event_id UUID NOT NULL,
  perspective_name VARCHAR(200) NOT NULL,
  stream_id UUID NOT NULL,
  applied_at TIMESTAMPTZ NOT NULL,
  CONSTRAINT pk_wh_perspective_applied PRIMARY KEY (event_id, perspective_name)
);

-- The prune reads the oldest rows first.
CREATE INDEX IF NOT EXISTS idx_wh_perspective_applied_applied_at
  ON __SCHEMA__.wh_perspective_applied (applied_at);

COMMENT ON TABLE __SCHEMA__.wh_perspective_applied IS
'Applied-event ledger (#959): one row per event a perspective (or the __collective__ sink) applied, written when the completion retires the work row. Read by wh_perspective_applied_status so a wait for an event from another service can be answered by any instance. Pruned an hour after recording.';

SELECT __SCHEMA__.drop_all_overloads('process_perspective_event_completions');

-- <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveAppliedLedgerSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_perspective_event_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  was_deleted BOOLEAN
) AS $$
BEGIN
  IF jsonb_array_length(p_completions) = 0 THEN RETURN; END IF;

  -- One set-based statement per mode (v0.671). Not-found completions are naturally filtered: the join
  -- only matches IDs that exist in wh_perspective_events, so already-deleted or never-existing IDs neither
  -- appear in RETURNING nor reach the ledger.
  --
  -- #959: each retired row is recorded in wh_perspective_applied in the same statement, so the ledger
  -- holds exactly the applies the worker completed. ON CONFLICT: a redelivered completion, or a debug-mode
  -- row completed twice, records once.
  IF p_debug_mode THEN
    -- Debug mode: retain rows, stamp status | status_flags + processed_at, clear lease columns.
    RETURN QUERY
    WITH completions AS (
      SELECT
        (elem->>'EventWorkId')::UUID AS work_id,
        (elem->>'StatusFlags')::INTEGER AS status_flags
      FROM jsonb_array_elements(p_completions) AS elem
    ),
    completed AS (
      UPDATE __SCHEMA__.wh_perspective_events pe
      SET status = pe.status | c.status_flags,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      FROM completions c
      WHERE pe.event_work_id = c.work_id
      RETURNING pe.event_work_id, pe.stream_id, pe.perspective_name, pe.event_id
    ),
    recorded AS (
      INSERT INTO __SCHEMA__.wh_perspective_applied (event_id, perspective_name, stream_id, applied_at)
      SELECT DISTINCT ON (cd.event_id, cd.perspective_name) cd.event_id, cd.perspective_name, cd.stream_id, p_now
      FROM completed cd
      ON CONFLICT ON CONSTRAINT pk_wh_perspective_applied DO NOTHING
    )
    SELECT cd.event_work_id, cd.stream_id, cd.perspective_name, FALSE AS was_deleted
    FROM completed cd;
  ELSE
    -- Production: DELETE matched rows, returning identity for cursor advancement.
    RETURN QUERY
    WITH completions AS (
      SELECT (elem->>'EventWorkId')::UUID AS work_id
      FROM jsonb_array_elements(p_completions) AS elem
    ),
    completed AS (
      DELETE FROM __SCHEMA__.wh_perspective_events pe
      USING completions c
      WHERE pe.event_work_id = c.work_id
      RETURNING pe.event_work_id, pe.stream_id, pe.perspective_name, pe.event_id
    ),
    recorded AS (
      INSERT INTO __SCHEMA__.wh_perspective_applied (event_id, perspective_name, stream_id, applied_at)
      SELECT DISTINCT ON (cd.event_id, cd.perspective_name) cd.event_id, cd.perspective_name, cd.stream_id, p_now
      FROM completed cd
      ON CONFLICT ON CONSTRAINT pk_wh_perspective_applied DO NOTHING
    )
    SELECT cd.event_work_id, cd.stream_id, cd.perspective_name, TRUE AS was_deleted
    FROM completed cd;
  END IF;

  -- Bounded prune of the ledger: rows older than an hour, oldest first, at most 1000 per call, so a
  -- backlog of expired rows drains across calls rather than stalling one completion.
  DELETE FROM __SCHEMA__.wh_perspective_applied pa
  WHERE pa.ctid IN (
    SELECT old.ctid
    FROM __SCHEMA__.wh_perspective_applied old
    WHERE old.applied_at < p_now - INTERVAL '1 hour'
    ORDER BY old.applied_at
    LIMIT 1000
  );
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_perspective_event_completions IS
'Processes perspective event completions. In production mode, deletes events (ephemeral). In debug mode, retains events for troubleshooting. Either way records each completed (event, perspective) in the wh_perspective_applied ledger (#959) and prunes ledger rows older than an hour. Returns stream/perspective pairs for checkpoint update orchestration.';

SELECT __SCHEMA__.drop_all_overloads('wh_perspective_applied_status');

-- <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveAppliedLedgerSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_perspective_applied_status(
  p_perspective_name TEXT,
  p_event_id UUID,
  p_stream_id UUID,
  p_stream_version INTEGER
) RETURNS TABLE(
  event_id UUID,
  state SMALLINT
) AS $$
DECLARE
  -- A collective event is applied by the sink, once, to every model it targets.
  c_collective_sink CONSTANT TEXT := '__collective__';
  v_event_id UUID := p_event_id;
BEGIN
  -- The event by position: this service's per-stream version.
  IF v_event_id IS NULL THEN
    SELECT es.event_id INTO v_event_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = p_stream_id AND es.version = p_stream_version;
  END IF;

  -- 2 = applied: the perspective, or the collective sink, recorded the apply.
  IF v_event_id IS NOT NULL AND EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_perspective_applied pa
    WHERE pa.event_id = v_event_id AND pa.perspective_name IN (p_perspective_name, c_collective_sink)
  ) THEN
    RETURN QUERY SELECT v_event_id, 2::SMALLINT;
    RETURN;
  END IF;

  -- 0 = not arrived: not in the local event store yet (an inbound event is stored when its inbox row is
  -- claimed). Checked after the ledger: a pruned event store row must not hide a recorded apply.
  IF v_event_id IS NULL OR NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_event_id
  ) THEN
    RETURN QUERY SELECT v_event_id, 0::SMALLINT;
    RETURN;
  END IF;

  -- 1 = pending: work to apply it is outstanding. The event store row and its work rows are written in one
  -- transaction, so a stored event with no outstanding work never gains any later.
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
    WHERE pe.event_id = v_event_id
      AND pe.perspective_name IN (p_perspective_name, c_collective_sink)
      AND pe.processed_at IS NULL
  ) THEN
    RETURN QUERY SELECT v_event_id, 1::SMALLINT;
    RETURN;
  END IF;

  -- 3 = not applicable: stored, never recorded, nothing outstanding. The perspective does not handle it,
  -- or its ledger row has been pruned.
  RETURN QUERY SELECT v_event_id, 3::SMALLINT;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_perspective_applied_status IS
'Where an event stands for one perspective (#959): 0 not arrived, 1 pending, 2 applied (ledger row for the perspective or the __collective__ sink), 3 not applicable. The event is named by id, or by stream and local per-stream version when the id is NULL. Read-only.';
