-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 298_ConvertHistoricalMetadata.sql
-- Date: 2026-11-02
-- Description: Rows written with full metadata before slim stored metadata (285) are converted to the slim form plus
--              their route id, once, by maintenance (#1727, WS-2 S10).
--
--              Every row written since 285 carries its route id (rid), slim or full, so a row without one is a row
--              written before it. wh_convert_event_metadata_history, called in batches by the
--              metadata-history-conversion maintenance step under the maintainer duty, walks wh_event_body in event id
--              order from a cursor and, for each row without a route id whose type is not kept full:
--
--                1. computes its route id from its type name and hops, as every write path does (_wh_route_id);
--                2. rewrites the row to the slim form plus that id (_wh_slim_event_metadata), exactly what a write path
--                   stores for it now;
--                3. under Linked, counts it on its route with one upsert per route per batch (first and last seen are
--                   when the event was stored), and writes its trace when the event is still inside the trace window, so
--                   reconstruction stays lossless for as long as it would have for a distilled event. An event older
--                   than the window gets no trace: retention would remove it in the same cycle.
--
--              It is the only place event rows are rewritten. Each batch is one transaction of at most p_batch_size
--              rows read, so its WAL and dead tuples are bounded; storage reclamation returns the space. It never
--              waits: the cursor row is taken with SKIP LOCKED (a second instance skips), and so is each event row (a
--              row another transaction holds, such as a reaper deleting it, is left for the next batch and the cursor
--              stops before it). A row that already carries a route id is never rewritten, so a batch that re-reads
--              rows it converted changes nothing. No work table is locked, so the canonical work-table lock order is
--              not involved; routes are upserted in route id order, as distillation does.
--
--              Skipped under debug mode (every row stays full), under the Full form (rows keep their full metadata),
--              and when metadata_convert_history is false. Rows of types kept full (metadata_full_for), rows whose
--              metadata is not a JSON object and bodies without their pointer are never touched.
--
--              When a batch reads to the end of the table with nothing held, the conversion is complete and later
--              calls read nothing. Deleting the row of wh_metadata_history_conversion starts it over from the first
--              event (for example after restoring archived streams written before 285, or after removing a type from
--              the full list); rows already converted are passed over.
--
--              Number 298: reserved for this change.
--
-- Dependencies: 285 (_wh_metadata_write_settings, _wh_metadata_kept_full, _wh_route_id, _wh_slim_event_metadata),
--               286 (wh_routes, wh_traces, _wh_route_hops, _wh_trace_hops, _wh_first_current_hop_at,
--               _wh_hops_duration_ms, _wh_try_uuid), 077 (wh_event_body), 028 (wh_settings)
-- Objects: wh_metadata_history_conversion, wh_convert_event_metadata_history
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_convert_event_metadata_history');

-- ===========================================================================================
-- The cursor
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_metadata_history_conversion (
  only_row BOOLEAN NOT NULL DEFAULT TRUE,
  last_event_id UUID NULL,
  rows_converted BIGINT NOT NULL DEFAULT 0,
  completed_at TIMESTAMPTZ NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT pk_metadata_history_conversion PRIMARY KEY (only_row),
  CONSTRAINT ck_metadata_history_conversion_only_row CHECK (only_row)
);

INSERT INTO __SCHEMA__.wh_metadata_history_conversion (only_row) VALUES (TRUE) ON CONFLICT DO NOTHING;

COMMENT ON TABLE __SCHEMA__.wh_metadata_history_conversion IS
  'The progress of the one-time conversion of rows written with full metadata before 285 (298, #1727): the last event '
  'id the conversion passed, the rows it converted, and when it read to the end. Delete the row to start over.';

-- ===========================================================================================
-- One batch
-- ===========================================================================================

-- <docs>data/event-store#history-conversion</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_ARowWithoutARouteId_BecomesSlimWithItsRouteId_AndARowWithOneIsUntouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_AConvertedRow_IsNotTouchedAgain_EvenFromTheStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_ARowOfATypeKeptFull_IsSkipped_AndCountedAsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_RowsItCannotRead_AreLeftAlone_AndNeverStopTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderDebugMode_SkipsTheTask_AndRunsOnceItIsOffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderTheFullFormOrSwitchedOff_SkipsTheTaskAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_AnyOtherConvertHistoryValue_RunsTheTaskAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderLinked_CountsTheRoute_AndTracesOnlyWhatIsInsideTheTraceWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_AnExistingRoute_OnlyCountsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderSlim_WritesNoRoutesOrTracesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_TakesAtMostTheBatch_ResumesFromItsCursor_AndCompletesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_ARowAnotherTransactionHolds_IsLeftForTheNextBatch_WithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_WhileAnotherInstanceConverts_SkipsWithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AddWhizbangPostgres_RegistersTheHistoryConversionStep_ThatConvertsTheStoresOwnFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/MetadataHistoryConversionCostScenarioTests.cs:Conversion_OnACopiedHistory_ReportAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_convert_event_metadata_history(p_batch_size INTEGER DEFAULT 1000)
RETURNS TABLE(
  rows_scanned BIGINT,
  rows_affected BIGINT,
  rows_kept_full BIGINT,
  routes_added BIGINT,
  routes_updated BIGINT,
  traces_written BIGINT,
  status TEXT
) AS $$
DECLARE
  v_batch INTEGER := GREATEST(COALESCE(p_batch_size, 1), 1);
  v_form TEXT;
  v_full_for TEXT[];
  v_debug BOOLEAN;
  v_convert BOOLEAN;
  v_trace_from TIMESTAMPTZ;
  v_from UUID;
  v_completed TIMESTAMPTZ;
  v_scanned_ids UUID[];
  v_scanned BIGINT;
  v_need_ids UUID[];
  v_need_types TEXT[];
  v_need_times TIMESTAMPTZ[];
  v_kept BIGINT;
  v_affected BIGINT := 0;
  v_added BIGINT := 0;
  v_updated BIGINT := 0;
  v_traces BIGINT := 0;
  v_held UUID;
  v_held_count BIGINT;
  v_cursor UUID;
BEGIN
  -- The settings, read once. A debug flag or an off switch is read in the spellings PostgreSQL gives a boolean; any
  -- other value of metadata_convert_history is the default, on. A trace window that is not a whole number is the
  -- default, as a missing one is.
  SELECT s.write_form, s.full_for INTO v_form, v_full_for FROM __SCHEMA__._wh_metadata_write_settings() s;
  SELECT
    COALESCE(bool_or(CASE WHEN st.setting_key = 'debug_mode'
                          THEN lower(btrim(st.setting_value)) IN ('true', 't', '1', 'yes', 'y', 'on') END), FALSE),
    NOT COALESCE(bool_or(CASE WHEN st.setting_key = 'metadata_convert_history'
                              THEN lower(btrim(st.setting_value)) IN ('false', 'f', '0', 'no', 'n', 'off') END), FALSE),
    NOW() - (COALESCE(MAX(CASE WHEN st.setting_key = 'trace_retention_days' AND btrim(st.setting_value) ~ '^[0-9]{1,6}$'
                               THEN btrim(st.setting_value)::INTEGER END), 30) * INTERVAL '1 day')
  INTO v_debug, v_convert, v_trace_from
  FROM __SCHEMA__.wh_settings st
  WHERE st.setting_key IN ('debug_mode', 'metadata_convert_history', 'trace_retention_days');

  IF v_debug THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;
  IF NOT v_convert THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (metadata_convert_history=false)'::TEXT;
    RETURN;
  END IF;
  IF v_form = 'Full' THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (metadata_write_form=Full)'::TEXT;
    RETURN;
  END IF;

  -- The cursor. A deleted row starts the conversion over; a row another instance holds means that instance is
  -- converting, and this call leaves it to it rather than waiting.
  INSERT INTO __SCHEMA__.wh_metadata_history_conversion (only_row) VALUES (TRUE) ON CONFLICT DO NOTHING;
  SELECT c.last_event_id, c.completed_at INTO v_from, v_completed
  FROM __SCHEMA__.wh_metadata_history_conversion c
  WHERE c.only_row
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (another instance is converting)'::TEXT;
    RETURN;
  END IF;
  IF v_completed IS NOT NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'complete'::TEXT;
    RETURN;
  END IF;

  -- The next rows by event id, through the key index, and which of them need converting: a row with its pointer (for
  -- its type) whose metadata is an object without a route id. Rows of types kept full are counted and left alone.
  SELECT
    array_agg(s.event_id ORDER BY s.event_id),
    count(*),
    array_agg(s.event_id ORDER BY s.event_id) FILTER (WHERE s.needs AND NOT s.kept),
    array_agg(s.type_name ORDER BY s.event_id) FILTER (WHERE s.needs AND NOT s.kept),
    array_agg(s.created_at ORDER BY s.event_id) FILTER (WHERE s.needs AND NOT s.kept),
    count(*) FILTER (WHERE s.needs AND s.kept)
  INTO v_scanned_ids, v_scanned, v_need_ids, v_need_types, v_need_times, v_kept
  FROM (
    SELECT
      b.event_id,
      t.type_name,
      es.created_at,
      (es.event_id IS NOT NULL AND jsonb_typeof(b.metadata) = 'object' AND NOT (b.metadata ? 'rid')) AS needs,
      __SCHEMA__._wh_metadata_kept_full(t.type_name, v_full_for) AS kept
    FROM (
      SELECT eb.event_id, eb.metadata
      FROM __SCHEMA__.wh_event_body eb
      WHERE eb.event_id > COALESCE(v_from, __EMPTY_UUID__::uuid)
      ORDER BY eb.event_id
      LIMIT v_batch
    ) b
    LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = b.event_id
    CROSS JOIN LATERAL (SELECT split_part(es.event_type, ',', 1) AS type_name) t
  ) s;

  IF v_scanned = 0 THEN
    UPDATE __SCHEMA__.wh_metadata_history_conversion SET completed_at = NOW(), updated_at = NOW() WHERE only_row;
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'complete'::TEXT;
    RETURN;
  END IF;

  IF v_need_ids IS NOT NULL THEN
    WITH needed AS (
      SELECT n.event_id, n.type_name, n.created_at
      FROM unnest(v_need_ids, v_need_types, v_need_times) AS n(event_id, type_name, created_at)
    ),
    -- Only the rows no other transaction holds, and still without a route id once locked.
    locked AS MATERIALIZED (
      SELECT b.event_id, b.metadata
      FROM __SCHEMA__.wh_event_body b
      WHERE b.event_id = ANY(v_need_ids)
        AND jsonb_typeof(b.metadata) = 'object'
        AND NOT (b.metadata ? 'rid')
      ORDER BY b.event_id
      FOR UPDATE SKIP LOCKED
    ),
    -- Each event's route id, computed once, from the same inputs the write paths use.
    batch AS MATERIALIZED (
      SELECT l.event_id, n.type_name, n.created_at, l.metadata AS full_meta, x.hops,
             __SCHEMA__._wh_route_id(n.type_name, x.hops) AS route_id
      FROM locked l
      JOIN needed n ON n.event_id = l.event_id
      CROSS JOIN LATERAL (SELECT COALESCE(l.metadata -> 'Hops', l.metadata -> 'hops') AS hops) x
    ),
    converted AS (
      UPDATE __SCHEMA__.wh_event_body b
      SET metadata = __SCHEMA__._wh_slim_event_metadata(bt.full_meta, bt.route_id)
      FROM batch bt
      WHERE b.event_id = bt.event_id
      RETURNING b.event_id
    ),
    -- Under Linked, what distillation would have made of the staged form (286): occurrences, durations, traces.
    occurrences AS (
      SELECT bt.event_id, bt.created_at, bt.route_id, bt.type_name, COALESCE(bt.hops, '[]'::jsonb) AS hops,
             NULLIF((SELECT jsonb_object_agg(e.key, e.value) FROM jsonb_each(bt.full_meta) e WHERE e.key IN ('dc', 'rin')),
                    '{}'::jsonb) AS extra,
             __SCHEMA__._wh_hops_duration_ms(bt.hops) AS duration_ms
      FROM batch bt
      WHERE v_form = 'Linked'
    ),
    route_rows AS (
      INSERT INTO __SCHEMA__.wh_routes AS r
        (route_id, event_type, hops, first_seen, last_seen, occurrences,
         duration_count, duration_sum_ms, duration_min_ms, duration_max_ms, duration_buckets)
      SELECT
        o.route_id,
        (array_agg(o.type_name ORDER BY o.event_id))[1],
        __SCHEMA__._wh_route_hops((array_agg(o.hops ORDER BY o.event_id))[1]),
        MIN(o.created_at),
        MAX(o.created_at),
        COUNT(*),
        COUNT(*) FILTER (WHERE o.duration_ms > 0),
        COALESCE(SUM(o.duration_ms) FILTER (WHERE o.duration_ms > 0), 0),
        MIN(o.duration_ms) FILTER (WHERE o.duration_ms > 0),
        MAX(o.duration_ms) FILTER (WHERE o.duration_ms > 0),
        ARRAY[
          COUNT(*) FILTER (WHERE o.duration_ms > 0 AND o.duration_ms <= 1),
          COUNT(*) FILTER (WHERE o.duration_ms > 1 AND o.duration_ms <= 10),
          COUNT(*) FILTER (WHERE o.duration_ms > 10 AND o.duration_ms <= 100),
          COUNT(*) FILTER (WHERE o.duration_ms > 100 AND o.duration_ms <= 1000),
          COUNT(*) FILTER (WHERE o.duration_ms > 1000 AND o.duration_ms <= 10000),
          COUNT(*) FILTER (WHERE o.duration_ms > 10000)]::BIGINT[]
      FROM occurrences o
      GROUP BY o.route_id
      ORDER BY o.route_id
      ON CONFLICT (route_id) DO UPDATE SET
        first_seen = LEAST(r.first_seen, EXCLUDED.first_seen),
        last_seen = GREATEST(r.last_seen, EXCLUDED.last_seen),
        occurrences = r.occurrences + EXCLUDED.occurrences,
        duration_count = r.duration_count + EXCLUDED.duration_count,
        duration_sum_ms = r.duration_sum_ms + EXCLUDED.duration_sum_ms,
        duration_min_ms = LEAST(r.duration_min_ms, EXCLUDED.duration_min_ms),
        duration_max_ms = GREATEST(r.duration_max_ms, EXCLUDED.duration_max_ms),
        duration_buckets = ARRAY(
          SELECT u.a + u.b FROM unnest(r.duration_buckets, EXCLUDED.duration_buckets) WITH ORDINALITY AS u(a, b, ord) ORDER BY u.ord)
      RETURNING (xmax = 0) AS inserted
    ),
    -- A trace only inside the trace window, kept for the window from the event's own time as a distilled one is.
    traces AS (
      INSERT INTO __SCHEMA__.wh_traces (event_id, recorded_at, route_id, occurred_at, correlation_id, causation_id, hops, extra)
      SELECT o.event_id, o.created_at, o.route_id,
             __SCHEMA__._wh_first_current_hop_at(o.hops),
             __SCHEMA__._wh_try_uuid(o.hops -> 0 ->> 'co'),
             __SCHEMA__._wh_try_uuid(o.hops -> 0 ->> 'ca'),
             __SCHEMA__._wh_trace_hops(o.hops),
             o.extra
      FROM occurrences o
      WHERE o.created_at >= v_trace_from
      ON CONFLICT DO NOTHING
      RETURNING 1
    )
    SELECT
      (SELECT COUNT(*) FROM converted),
      (SELECT COUNT(*) FILTER (WHERE rr.inserted) FROM route_rows rr),
      (SELECT COUNT(*) FILTER (WHERE NOT rr.inserted) FROM route_rows rr),
      (SELECT COUNT(*) FROM traces)
    INTO v_affected, v_added, v_updated, v_traces;
  END IF;

  -- A row this batch needed that still has no route id was held by another transaction. The cursor stops before the
  -- first of them, so the next batch reads it again; the rows after it that were converted are passed over then.
  -- (uuid has no min or max aggregate, so the first and last are read by order.)
  SELECT count(*) INTO v_held_count
  FROM __SCHEMA__.wh_event_body b
  WHERE b.event_id = ANY(v_need_ids)
    AND jsonb_typeof(b.metadata) = 'object'
    AND NOT (b.metadata ? 'rid');
  IF v_held_count = 0 THEN
    v_cursor := v_scanned_ids[cardinality(v_scanned_ids)];
  ELSE
    SELECT b.event_id INTO v_held
    FROM __SCHEMA__.wh_event_body b
    WHERE b.event_id = ANY(v_need_ids)
      AND jsonb_typeof(b.metadata) = 'object'
      AND NOT (b.metadata ? 'rid')
    ORDER BY b.event_id
    LIMIT 1;
    SELECT u.id INTO v_cursor FROM unnest(v_scanned_ids) AS u(id) WHERE u.id < v_held ORDER BY u.id DESC LIMIT 1;
    v_cursor := COALESCE(v_cursor, v_from);
  END IF;

  UPDATE __SCHEMA__.wh_metadata_history_conversion SET
    last_event_id = v_cursor,
    rows_converted = rows_converted + v_affected,
    completed_at = CASE WHEN v_held_count = 0 AND v_scanned < v_batch THEN NOW() END,
    updated_at = NOW()
  WHERE only_row;

  RETURN QUERY SELECT v_scanned, v_affected, v_kept, v_added, v_updated, v_traces,
    CASE WHEN v_held_count = 0 THEN 'ok'
         ELSE 'held (' || v_held_count || ' locked row(s) left for the next batch)' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_convert_event_metadata_history(INTEGER) IS
  'Issue #1727: converts up to p_batch_size event rows past the cursor in wh_metadata_history_conversion that carry no '
  'route id (rows written before 285) to the slim form plus their route id; under Linked counts their routes and '
  'traces the ones inside trace_retention_days. Skips rows of types kept full, rows it cannot read and rows another '
  'transaction holds, never waits, and is skipped under debug mode, the Full form and metadata_convert_history=false. '
  'Called by the metadata-history-conversion maintenance step.';
