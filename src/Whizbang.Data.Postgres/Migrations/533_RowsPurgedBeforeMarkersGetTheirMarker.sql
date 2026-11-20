-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 533_RowsPurgedBeforeMarkersGetTheirMarker.sql
-- Date: 2026-11-20
-- Description: The purge markers a row purged before markers existed never got are written, where the purge is proven
--              (#2314).
--
--              The companion cleanup (403, #2294) removes a purged row's cursor and snapshots only for a pair that carries
--              its perspective's purge marker (wh_stream_purge_markers, 180). A row purged before markers were written
--              has none, so its cursor and snapshots were kept forever.
--
--              A missing row is not proof of a purge: the row may never have been created (every Apply returned no
--              model while the run still wrote a cursor), an eviction before #1483 may have removed it, or its stream
--              may still have work to apply. A marker on any of them makes the runner skip the stream's later events,
--              which loses data silently. So a pair is marked only when its cursor's last event is a type whose Apply on
--              that perspective can only purge (the generator's terminal-purge rule, passed in by the caller), the
--              table the registry says the perspective folds into holds no row for the stream, no marker (its own or an
--              operator's '*') exists, and no work is pending. The marker names that event. A perspective that no
--              registry row names, or that more than one names, is never marked. Every other missing row is left as it
--              is; wh_count_unmarked_rowless_pairs() counts them so they are visible.
--
-- Reversal: reversible (the functions go; markers already written stay and keep their rows purged, as the runner would
--           have written them)
-- Dependencies: 180 (wh_stream_purge_markers), 211 (wh_perspective_registry.perspective_names), 403 (the companion
--               cleanup that acts on the markers)
-- Objects: wh_backfill_purge_markers, wh_count_unmarked_rowless_pairs
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_backfill_purge_markers');

-- Writes at most p_batch_size purge markers for pairs whose row is missing and whose cursor's last event is one of the
-- given (perspective, event type) pairs, an event whose Apply can only purge. Each registry table is probed by an
-- anti-join, so the limit counts pairs actually marked. Returns the markers written.
-- <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs:Backfill_AMissingRowWhoseLastEventCanOnlyPurge_IsMarkedWithThatEvent_AndItsCompanionsThenGoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs:Backfill_ALastEventThatPurgesOnlyConditionally_IsNotMarkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs:Backfill_ARowThatWasNeverCreated_IsNotMarkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs:Backfill_ALiveRow_APendingStream_AndAnUnregisteredPerspective_AreNotMarkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs:Backfill_AnExistingMarker_AndAnOperatorMarker_AreLeftAsTheyAreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs:Backfill_ExaminesAtMostTheBatchSize_AndTheRestOnALaterCallAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_backfill_purge_markers(
  p_perspectives TEXT[],
  p_event_types TEXT[],
  p_batch_size INTEGER
) RETURNS INTEGER AS $$
DECLARE
  v_table   RECORD;
  v_left    INTEGER := GREATEST(p_batch_size, 0);
  v_written INTEGER;
  v_total   INTEGER := 0;
BEGIN
  IF COALESCE(cardinality(p_perspectives), 0) = 0 OR v_left = 0 THEN
    RETURN 0;
  END IF;

  -- Each existing table the registry names, with the given perspectives that fold into it and into no other table.
  FOR v_table IN
    SELECT r.table_name, array_agg(DISTINCT n.perspective_name) AS names
      FROM __SCHEMA__.wh_perspective_registry r
     CROSS JOIN LATERAL unnest(r.perspective_names) AS n(perspective_name)
     WHERE n.perspective_name = ANY(p_perspectives)
       AND EXISTS (SELECT 1 FROM information_schema.tables t
                    WHERE t.table_schema = replace('__SCHEMA__', '"', '')
                      AND t.table_name = r.table_name)
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_registry o
                        WHERE o.table_name <> r.table_name
                          AND n.perspective_name = ANY(o.perspective_names))
     GROUP BY r.table_name
     ORDER BY r.table_name
  LOOP
    EXIT WHEN v_left = 0;
    EXECUTE format(
      'INSERT INTO __SCHEMA__.wh_stream_purge_markers (stream_id, perspective_name, purged_at, purge_event_id)
       SELECT c.stream_id, c.perspective_name, COALESCE(c.processed_at, NOW()), c.last_event_id
         FROM unnest($1, $2) AS p(pname, etype)
         JOIN __SCHEMA__.wh_perspective_cursors c ON c.perspective_name = p.pname
         JOIN __SCHEMA__.wh_event_store e ON e.event_id = c.last_event_id AND e.stream_id = c.stream_id
        WHERE p.pname = ANY($3)
          AND e.event_type = __SCHEMA__.normalize_event_type(p.etype)
          AND NOT EXISTS (SELECT 1 FROM %1$I.%2$I t WHERE t.id = c.stream_id)
          AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stream_purge_markers m
                           WHERE m.stream_id = c.stream_id AND m.perspective_name IN (c.perspective_name, ''*''))
          AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
                           WHERE pe.stream_id = c.stream_id AND pe.perspective_name = c.perspective_name
                             AND pe.processed_at IS NULL)
        LIMIT $4
       ON CONFLICT (stream_id, perspective_name) DO NOTHING',
      replace('__SCHEMA__', '"', ''), v_table.table_name)
      USING p_perspectives, p_event_types, v_table.names, v_left;
    GET DIAGNOSTICS v_written = ROW_COUNT;
    v_total := v_total + v_written;
    v_left := v_left - v_written;
  END LOOP;

  RETURN v_total;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_backfill_purge_markers(TEXT[], TEXT[], INTEGER) IS
  '#2314: writes at most p_batch_size purge markers for rows purged before markers existed: the row is missing from '
  'the one registry table its perspective folds into, no marker exists, no work is pending, and the cursor''s last event '
  'is one of the given (perspective, event type) pairs, a type whose Apply can only purge. The marker names that event. '
  'Returns the markers written.';

SELECT __SCHEMA__.drop_all_overloads('wh_count_unmarked_rowless_pairs');

-- Counts, up to p_cap, the pairs whose row is missing that the backfill leaves unmarked: settled, no marker, and a cursor
-- whose last event is not one of the given purging pairs (or is not held at all). They keep their cursor and snapshots.
-- <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs:CountUnmarked_CountsOnlyTheMissingRowsTheBackfillCannotProve_UpToTheCapAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_count_unmarked_rowless_pairs(
  p_perspectives TEXT[],
  p_event_types TEXT[],
  p_cap INTEGER
) RETURNS BIGINT AS $$
DECLARE
  v_table RECORD;
  v_found BIGINT;
  v_total BIGINT := 0;
BEGIN
  FOR v_table IN
    SELECT r.table_name, array_agg(DISTINCT n.perspective_name) AS names
      FROM __SCHEMA__.wh_perspective_registry r
     CROSS JOIN LATERAL unnest(r.perspective_names) AS n(perspective_name)
     WHERE EXISTS (SELECT 1 FROM information_schema.tables t
                    WHERE t.table_schema = replace('__SCHEMA__', '"', '')
                      AND t.table_name = r.table_name)
     GROUP BY r.table_name
     ORDER BY r.table_name
  LOOP
    EXIT WHEN v_total >= p_cap;
    EXECUTE format(
      'SELECT count(*) FROM (
         SELECT 1
           FROM __SCHEMA__.wh_perspective_cursors c
           LEFT JOIN __SCHEMA__.wh_event_store e ON e.event_id = c.last_event_id
          WHERE c.perspective_name = ANY($3)
            AND NOT EXISTS (SELECT 1 FROM %1$I.%2$I t WHERE t.id = c.stream_id)
            AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stream_purge_markers m
                             WHERE m.stream_id = c.stream_id AND m.perspective_name IN (c.perspective_name, ''*''))
            AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
                             WHERE pe.stream_id = c.stream_id AND pe.perspective_name = c.perspective_name
                               AND pe.processed_at IS NULL)
            AND NOT EXISTS (SELECT 1 FROM unnest($1, $2) AS p(pname, etype)
                             WHERE p.pname = c.perspective_name
                               AND e.event_type = __SCHEMA__.normalize_event_type(p.etype))
          LIMIT $4) x',
      replace('__SCHEMA__', '"', ''), v_table.table_name)
      INTO v_found
      USING COALESCE(p_perspectives, '{}'::TEXT[]), COALESCE(p_event_types, '{}'::TEXT[]), v_table.names, p_cap - v_total;
    v_total := v_total + v_found;
  END LOOP;

  RETURN v_total;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_count_unmarked_rowless_pairs(TEXT[], TEXT[], INTEGER) IS
  '#2314: counts, up to p_cap, the (stream, perspective) pairs whose row is missing that wh_backfill_purge_markers '
  'leaves unmarked (settled, no marker, last event not a type that can only purge), so they are visible.';
