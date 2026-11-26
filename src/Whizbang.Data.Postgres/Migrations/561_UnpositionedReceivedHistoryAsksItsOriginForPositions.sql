-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 561_UnpositionedReceivedHistoryAsksItsOriginForPositions.sql
-- Date: 2026-11-23
-- Description: A received event stored without its origin's commit sequence asks its origin for it, and the answer fills
--              the position after the fact (#2544).
--
--              An origin's outbox can publish an event before its commit-order stamper has stamped it; the receiver then
--              stores the event with origin_service_id set and origin_commit_sequence NULL. Such an event sits in no
--              window, so a windowed audit against the origin cannot confirm the windows it belongs to (they are left
--              undecided), the seal for the origin never advances, and the cut gate holds every stream with a positioned
--              event from that origin. Only the origin knows the position. The receiver now asks for it through the
--              integrity request and answer exchange, and the answer fills only positions that are still NULL.
--
--                the walk        wh_origin_position_walk, one cursor per origin over idx_event_store_unsequenced_received
--                                (452, the held rows by (origin, created_at)). wh_origin_position_ask takes the next
--                                p_batch_size held rows of one origin after its cursor (the origin served least recently
--                                first), leaves out the ones the origin answered unknown, and advances the cursor past
--                                every row it read. A lap that reaches the end restarts after p_lap_pause, so an event the
--                                origin has not stamped yet is asked again on the next lap, never in a tight loop.
--                the answer      wh_origin_positions_of, on the origin: per asked event id, its own stored commit
--                                sequence ('positioned'), 'unstamped' while its stamper has not reached it, its archived
--                                commit sequence when the store no longer holds it but the archive table does
--                                ('archived'), and 'unknown' otherwise (an event it never wrote, or no longer holds).
--                the fill        wh_origin_position_fill_plan locks the streams that hold a fillable event (the emit
--                                chain's per-stream lock, in its order) and returns that origin's events of them, so the
--                                caller can tell, by the replay's own rule, which streams the fill reorders.
--                                wh_fill_origin_positions then fills through the redelivery reconcile's batch call
--                                (454): only NULL positions, never an overwrite, and every sealed epoch that now holds a
--                                filled event refolded once. The held fold (452) follows by its row triggers, so the
--                                event leaves the open window's held count and joins its positioned window. Unknown
--                                answers are recorded (wh_origin_position_unknown) and never asked again; a stream whose
--                                order the fill changed is rewound from its start, as the received-stream re-sequence
--                                (555) rewinds one.
--                the count       wh_origin_positions_unfilled, per origin, from the maintained held totals (452): the
--                                eligible events still waiting for a position, read in O(types) for the metric.
--
--              Cost: one ask reads at most p_batch_size index entries of one origin and probes the unknown table by
--              primary key for each; the origin skip-scan reads one index entry per origin. A fill reads the batch's
--              events by key, that origin's events of the batch's streams, and refolds each touched sealed epoch once.
--              Rows that are not held pay nothing: the unknown table's triggers fire only for held rows leaving the set.
--
--              Number 561: 560 is the cut gate change of the same issue; 559 is taken by a change in flight.
--
-- Reversal: reversible (the functions, triggers and tables go; positions already filled stay, which is correct)
-- Dependencies: 085 and 262 (wh_event_archive and its commit_sequence), 452 (idx_event_store_unsequenced_received, the
--               held totals and changes), 454 (wh_reconcile_redelivered_bundles_counting_satisfied), 537 (the emit chains'
--               per-stream lock key), 555 (the rewind a re-sequence performs)
-- Objects: wh_origin_position_walk, wh_origin_position_unknown, _wh_forget_origin_position_unknown,
--          _wh_clear_origin_position_unknown, wh_event_store_forgets_position_unknown_delete_trg,
--          wh_event_store_forgets_position_unknown_update_trg, wh_event_store_clears_position_unknown_trg,
--          wh_origin_position_ask, wh_origin_positions_of, wh_origin_position_fill_plan, _wh_rewind_streams_from_start,
--          wh_fill_origin_positions, wh_origin_positions_unfilled
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_origin_position_walk (
  origin_service_id UUID        NOT NULL PRIMARY KEY,
  after_created_at  TIMESTAMPTZ,              -- the walk's cursor: every held row at or before it was asked this lap
  after_event_id    UUID,
  next_lap_at       TIMESTAMPTZ,              -- set when a lap reached the end: the next lap starts no earlier
  updated_at        TIMESTAMPTZ                -- when this origin was last served; the least recent goes first
);

COMMENT ON TABLE __SCHEMA__.wh_origin_position_walk IS
'561 (#2544): one cursor per origin over the received events held without their origin''s commit sequence, walked by wh_origin_position_ask in bounded batches. A lap that reaches the end restarts after the lap pause.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_origin_position_unknown (
  event_id          UUID        NOT NULL PRIMARY KEY,
  origin_service_id UUID        NOT NULL,
  recorded_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_origin_position_unknown IS
'561 (#2544): received events held without a position whose origin answered that it does not know it (it never wrote the event, or no longer holds it and its archive keeps no sequence). Never asked again. A row goes when its event leaves the held set (deleted, or positioned by a later redelivery), through the row triggers on wh_event_store.';

-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Unknown_IsForgotten_WhenItsEventIsDeletedOrPositionedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_forget_origin_position_unknown() RETURNS TRIGGER AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_origin_position_unknown u WHERE u.event_id = OLD.event_id;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Unknown_IsForgotten_WhenTheEventStoreIsTruncatedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_clear_origin_position_unknown() RETURNS TRIGGER AS $$
BEGIN
  TRUNCATE __SCHEMA__.wh_origin_position_unknown;
  DELETE FROM __SCHEMA__.wh_origin_position_walk;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Only held rows leaving the held set fire: an ordinary write is filtered by the WHEN clause before the function runs.
DROP TRIGGER IF EXISTS wh_event_store_forgets_position_unknown_delete_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_forgets_position_unknown_delete_trg
  AFTER DELETE ON __SCHEMA__.wh_event_store
  FOR EACH ROW
  WHEN (OLD.origin_service_id IS NOT NULL AND OLD.origin_commit_sequence IS NULL)
  EXECUTE FUNCTION __SCHEMA__._wh_forget_origin_position_unknown();

DROP TRIGGER IF EXISTS wh_event_store_forgets_position_unknown_update_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_forgets_position_unknown_update_trg
  AFTER UPDATE OF origin_service_id, origin_commit_sequence ON __SCHEMA__.wh_event_store
  FOR EACH ROW
  WHEN (OLD.origin_service_id IS NOT NULL AND OLD.origin_commit_sequence IS NULL
        AND (NEW.origin_commit_sequence IS NOT NULL OR NEW.origin_service_id IS DISTINCT FROM OLD.origin_service_id))
  EXECUTE FUNCTION __SCHEMA__._wh_forget_origin_position_unknown();

DROP TRIGGER IF EXISTS wh_event_store_clears_position_unknown_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_clears_position_unknown_trg
  AFTER TRUNCATE ON __SCHEMA__.wh_event_store
  FOR EACH STATEMENT
  EXECUTE FUNCTION __SCHEMA__._wh_clear_origin_position_unknown();

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The next batch to ask for: at most p_batch_size held rows of one origin after its cursor, the origin served least
-- recently first among those whose lap pause has passed. Rows the origin answered unknown are read (so the cursor passes
-- them) but not returned. One row, or none when no origin is due.
SELECT __SCHEMA__.drop_all_overloads('wh_origin_position_ask');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_WalksOneOriginInBoundedBatches_AndRestartsTheLapOnlyAfterThePauseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_ServesTheOriginServedLeastRecently_AndLeavesOutUnknownAnswersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_OnALargeStore_ReadsOnlyItsBatchOfTheHeldIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_position_ask(
  p_batch_size INTEGER,
  p_lap_pause INTERVAL,
  p_now TIMESTAMPTZ
) RETURNS TABLE(origin_service_id UUID, event_ids UUID[], lap_ended BOOLEAN) AS $$
#variable_conflict use_column
DECLARE
  v_origin UUID;
  v_after_at TIMESTAMPTZ;
  v_after_id UUID;
  v_ids UUID[];
  v_read INTEGER;
  v_last_at TIMESTAMPTZ;
  v_last_id UUID;
BEGIN
  -- Every origin holding a row without its position, one index probe per origin (a skip scan of the held index).
  INSERT INTO __SCHEMA__.wh_origin_position_walk (origin_service_id)
  WITH RECURSIVE o(id) AS (
    (SELECT es.origin_service_id FROM __SCHEMA__.wh_event_store es
     WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL
     ORDER BY es.origin_service_id LIMIT 1)
    UNION ALL
    SELECT (SELECT es.origin_service_id FROM __SCHEMA__.wh_event_store es
            WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL AND es.origin_service_id > o.id
            ORDER BY es.origin_service_id LIMIT 1)
    FROM o WHERE o.id IS NOT NULL
  )
  SELECT o.id FROM o WHERE o.id IS NOT NULL
  ON CONFLICT (origin_service_id) DO NOTHING;

  SELECT w.origin_service_id, w.after_created_at, w.after_event_id INTO v_origin, v_after_at, v_after_id
  FROM __SCHEMA__.wh_origin_position_walk w
  WHERE w.next_lap_at IS NULL OR w.next_lap_at <= p_now
  ORDER BY w.updated_at ASC NULLS FIRST, w.origin_service_id
  LIMIT 1
  FOR UPDATE SKIP LOCKED;
  IF v_origin IS NULL THEN
    RETURN;
  END IF;

  SELECT COALESCE(array_agg(b.event_id ORDER BY b.created_at, b.event_id)
                    FILTER (WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_origin_position_unknown u WHERE u.event_id = b.event_id)),
                  ARRAY[]::UUID[]),
         count(*)::int,
         (array_agg(b.created_at ORDER BY b.created_at DESC, b.event_id DESC))[1],
         (array_agg(b.event_id ORDER BY b.created_at DESC, b.event_id DESC))[1]
    INTO v_ids, v_read, v_last_at, v_last_id
  FROM (
    SELECT es.event_id, es.created_at
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = v_origin
      AND es.origin_commit_sequence IS NULL
      AND (v_after_at IS NULL OR (es.created_at, es.event_id) > (v_after_at, v_after_id))
    ORDER BY es.created_at, es.event_id
    LIMIT GREATEST(p_batch_size, 1)
  ) b;

  IF v_read < GREATEST(p_batch_size, 1) THEN
    -- The lap reached the end: start over after the pause, so an event the origin has not stamped yet is asked again.
    UPDATE __SCHEMA__.wh_origin_position_walk w
    SET after_created_at = NULL, after_event_id = NULL, next_lap_at = p_now + p_lap_pause, updated_at = p_now
    WHERE w.origin_service_id = v_origin;
  ELSE
    UPDATE __SCHEMA__.wh_origin_position_walk w
    SET after_created_at = v_last_at, after_event_id = v_last_id, next_lap_at = NULL, updated_at = p_now
    WHERE w.origin_service_id = v_origin;
  END IF;

  RETURN QUERY SELECT v_origin, v_ids, v_read < GREATEST(p_batch_size, 1);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_position_ask(INTEGER, INTERVAL, TIMESTAMPTZ) IS
'561 (#2544): the next batch of received events held without a position to ask their origin for: at most p_batch_size held rows of the origin served least recently, after its cursor, without those it answered unknown. A lap that reaches the end restarts after p_lap_pause.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The origin's answer for the asked event ids: its own commit sequence, or why it has none.
SELECT __SCHEMA__.drop_all_overloads('wh_origin_positions_of');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:PositionsOf_AnswersStamped_Unstamped_Archived_AndUnknownAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_positions_of(p_event_ids UUID[])
RETURNS TABLE(event_id UUID, state TEXT, commit_sequence BIGINT) AS $$
  SELECT u.eid,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL AND es.commit_sequence IS NOT NULL THEN 'positioned'
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN 'unstamped'
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL AND a.commit_sequence IS NOT NULL THEN 'archived'
           ELSE 'unknown'
         END,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN es.commit_sequence
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL THEN a.commit_sequence
         END
  FROM (SELECT DISTINCT x.eid FROM unnest(p_event_ids) AS x(eid)) u
  LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
  LEFT JOIN __SCHEMA__.wh_event_archive a ON a.event_id = u.eid
  ORDER BY u.eid;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_positions_of(UUID[]) IS
'561 (#2544): per asked event id, this service''s commit sequence for an event it wrote: positioned (from the store), unstamped (held, not stamped yet: ask again), archived (from wh_event_archive), or unknown (never written here, received from another service, or no longer held with a sequence).';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The fill's plan: the streams that hold a fillable event (held from p_origin without a position, and named), locked in
-- the emit chain's order, and every event of p_origin in those streams, so the caller can compare the replay order before
-- and after the fill. Positions are only ever compared within one origin, so the other origins' events are not read.
SELECT __SCHEMA__.drop_all_overloads('wh_origin_position_fill_plan');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:FillPlan_ReturnsTheOriginsEventsOfTheStreamsTheFillTouches_OnlyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_position_fill_plan(p_origin UUID, p_event_ids UUID[])
RETURNS TABLE(stream_id UUID, event_id UUID, version BIGINT, origin_commit_sequence BIGINT) AS $$
DECLARE
  v_streams UUID[];
BEGIN
  SELECT array_agg(DISTINCT es.stream_id ORDER BY es.stream_id) INTO v_streams
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(p_event_ids)
    AND es.origin_service_id = p_origin
    AND es.origin_commit_sequence IS NULL;
  IF v_streams IS NULL THEN
    RETURN;
  END IF;

  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM unnest(v_streams) AS s(sid);

  RETURN QUERY
  SELECT es.stream_id, es.event_id, es.version::bigint, es.origin_commit_sequence
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = ANY(v_streams)
    AND es.origin_service_id = p_origin
  ORDER BY es.stream_id, es.version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_position_fill_plan(UUID, UUID[]) IS
'561 (#2544): locks the streams holding a named event received from p_origin without a position (the emit chain''s per-stream lock, in stream order) and returns p_origin''s events of those streams by version, for the caller to decide which streams the fill reorders.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Rewinds every perspective of the streams from their start, as a re-sequence does (555): the snapshots go (each holds
-- a fold in the old order), every cursor but the collective sink's is flagged RewindRequired at the stream's first event
-- with its replay frontier cleared, and that trigger is queued for each. Returns the cursors flagged.
SELECT __SCHEMA__.drop_all_overloads('_wh_rewind_streams_from_start');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatChangesAStreamsOriginOrder_RewindsItsPerspectivesFromTheStartAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_rewind_streams_from_start(p_stream_ids UUID[], p_now TIMESTAMPTZ)
RETURNS INTEGER AS $$
DECLARE
  v_rewound INTEGER;
BEGIN
  IF COALESCE(cardinality(p_stream_ids), 0) = 0 THEN
    RETURN 0;
  END IF;

  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(p_stream_ids);

  WITH firsts AS (
    SELECT DISTINCT ON (es.stream_id) es.stream_id, es.event_id AS trigger_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = ANY(p_stream_ids)
    ORDER BY es.stream_id, es.version ASC
  ),
  flagged AS (
    UPDATE __SCHEMA__.wh_perspective_cursors c
    SET status = c.status | 32,  -- RewindRequired flag (1 << 5)
        rewind_trigger_event_id = f.trigger_id,
        rewind_flagged_at = p_now,
        rewind_first_flagged_at = COALESCE(c.rewind_first_flagged_at, p_now),
        replayed_through_commit_sequence = NULL
    FROM firsts f
    WHERE c.stream_id = f.stream_id
      AND c.perspective_name <> '__collective__'
    RETURNING c.stream_id, c.perspective_name, f.trigger_id
  ),
  queued AS (
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    SELECT gen_random_uuid(), f.stream_id, f.perspective_name, f.trigger_id,
      __SCHEMA__.compute_partition(f.stream_id, 10000), 1, 0, p_now, __PRIORITY_BACKGROUND__
    FROM flagged f
    -- A processed row of the trigger (debug mode keeps it) is made pending again; a pending one is left as it is.
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO UPDATE
      SET processed_at = NULL, status = EXCLUDED.status, attempts = 0, error = NULL, failure_reason = NULL,
          instance_id = NULL, lease_expiry = NULL, claimed_at = NULL, scheduled_for = NULL
      WHERE __SCHEMA__.wh_perspective_events.processed_at IS NOT NULL
    RETURNING 1
  )
  SELECT count(*) INTO v_rewound FROM flagged;
  RETURN v_rewound;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_rewind_streams_from_start(UUID[], TIMESTAMPTZ) IS
'561 (#2544): removes the streams'' snapshots and flags every cursor but the collective sink''s RewindRequired at the stream''s first event, queuing that trigger, so its perspectives replay the stream from the start in its replay order, as a re-sequence (555) does. Returns the cursors flagged.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Applies one origin's answer: fills the positions it gave (only NULL ones, through the redelivery reconcile's batch
-- call, which refolds each sealed epoch that now holds a filled event once), records the events it does not know, and
-- rewinds the streams the caller found reordered. Returns what it filled, recorded and rewound.
SELECT __SCHEMA__.drop_all_overloads('wh_fill_origin_positions');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_FillsOnlyNullPositions_AndNeverOverwritesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RefoldsTheSealedEpochAFilledEventFallsIn_AndTheHeldFoldLetsItGoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_RecordsUnknownAnswers_AndTheAskNeverReturnsThemAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Fill_ThatChangesAStreamsOriginOrder_RewindsItsPerspectivesFromTheStartAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_fill_origin_positions(
  p_origin UUID,
  p_event_ids UUID[],
  p_sequences BIGINT[],
  p_unknown_ids UUID[],
  p_rewind_streams UUID[],
  p_now TIMESTAMPTZ
) RETURNS TABLE(filled INTEGER, unknown_recorded INTEGER, perspectives_rewound INTEGER) AS $$
DECLARE
  v_ids UUID[];
  v_seqs BIGINT[];
  v_filled INTEGER := 0;
  v_unknown INTEGER := 0;
  v_rewound INTEGER;
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_sequences) THEN
    RAISE EXCEPTION 'wh_fill_origin_positions: % event ids and % sequences must pair by position',
      cardinality(p_event_ids), cardinality(p_sequences);
  END IF;

  -- Only events still held from this origin without a position, and only real positions: never an overwrite.
  SELECT array_agg(m.eid ORDER BY m.eid), array_agg(m.seq ORDER BY m.eid) INTO v_ids, v_seqs
  FROM unnest(p_event_ids, p_sequences) AS m(eid, seq)
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
  WHERE es.origin_service_id = p_origin AND es.origin_commit_sequence IS NULL AND m.seq > 0;

  IF v_ids IS NOT NULL THEN
    SELECT COALESCE(SUM(r.adopted_count), 0)::int INTO v_filled
    FROM __SCHEMA__.wh_reconcile_redelivered_bundles_counting_satisfied(
      ARRAY[p_origin], ARRAY[NULL::text], array_fill(1, ARRAY[cardinality(v_ids)]), v_ids, v_seqs) r;
  END IF;

  WITH recorded AS (
    INSERT INTO __SCHEMA__.wh_origin_position_unknown (event_id, origin_service_id, recorded_at)
    SELECT es.event_id, p_origin, p_now
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(COALESCE(p_unknown_ids, ARRAY[]::UUID[]))
      AND es.origin_service_id = p_origin AND es.origin_commit_sequence IS NULL
    ON CONFLICT (event_id) DO NOTHING
    RETURNING 1
  )
  SELECT count(*)::int INTO v_unknown FROM recorded;

  v_rewound := __SCHEMA__._wh_rewind_streams_from_start(p_rewind_streams, p_now);

  RETURN QUERY SELECT v_filled, v_unknown, v_rewound;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_fill_origin_positions(UUID, UUID[], BIGINT[], UUID[], UUID[], TIMESTAMPTZ) IS
'561 (#2544): applies an origin''s position answer: fills only NULL positions of events held from p_origin (through wh_reconcile_redelivered_bundles_counting_satisfied, which refolds each touched sealed epoch once), records the events it answered unknown so they are not asked again, and rewinds the streams the caller found reordered from their start.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Per origin, the eligible events still held without a position: the maintained totals (452) plus the changes not folded
-- yet. O(types + pending changes); never reads the held rows.
SELECT __SCHEMA__.drop_all_overloads('wh_origin_positions_unfilled');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Unfilled_CountsTheEligibleHeldEventsPerOrigin_AndFallsAsTheyFillAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_positions_unfilled()
RETURNS TABLE(origin_service_id UUID, unfilled BIGINT) AS $$
  SELECT x.origin, SUM(x.n)::bigint
  FROM (
    SELECT t.origin_service_id AS origin, t.event_count AS n FROM __SCHEMA__.wh_unsequenced_digest_totals t
    UNION ALL
    SELECT c.origin_service_id, c.event_count::bigint FROM __SCHEMA__.wh_unsequenced_digest_changes c
  ) x
  GROUP BY x.origin
  HAVING SUM(x.n) > 0
  ORDER BY x.origin;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_positions_unfilled() IS
'561 (#2544): per origin, the digest-eligible events this service holds from it without its commit sequence, from the maintained held totals and their pending changes (452). The gauge behind whizbang.stream_integrity.origin_positions_unfilled.';
