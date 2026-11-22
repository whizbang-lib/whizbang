-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 556_RebuildWritesABatchOfCursorsInOneStatement.sql
-- Date: 2026-11-22
-- Description: wh_complete_rebuilt_cursors (#2513): the rebuild's cursor write for a whole flush of cursors, in one
--              statement. A rebuild wrote each stream's cursor with its own call of wh_complete_rebuilt_cursor (273), so
--              its statements grew with its streams. This writes every cursor of a flush exactly as that function writes
--              one: the cursor of a stream archived whole (its lifecycle row Archived) is parked in
--              wh_event_archive_cursors, an existing parked cursor moved to the rebuilt event or a new one parked, and
--              every other stream's cursor is upserted into wh_perspective_cursors with its rewind flags and error
--              cleared. A pair named more than once takes its last entry, as calling the one-cursor function in order
--              left it. The rows are written in key order, so two flushes never wait on each other in opposite orders.
--
--              wh_complete_rebuilt_cursor stays for a caller that writes one cursor.
--
--              Number 556: reserved for this change.
--
-- Dependencies: 260 (wh_stream_lifecycle), 262 (wh_event_archive_cursors), 273 (wh_complete_rebuilt_cursor, whose writes
--               this repeats for many cursors)
-- Objects: wh_complete_rebuilt_cursors
-- Reversal: reversible (the function is dropped; the cursors it wrote stay written, and wh_complete_rebuilt_cursor (273)
--           still writes one cursor at a time)
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_complete_rebuilt_cursors');

-- <docs>fundamentals/perspectives/rebuild#batched-reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Completer_WritesEveryCursorOfAFlush_WithOneStatement_ParkedAndLiveAlikeAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRebuildTests.cs:Completer_WritesEveryCursorOfAFlush_WithOneStatement_ParkedAndLiveAlikeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_complete_rebuilt_cursors(
  p_stream_ids UUID[],
  p_perspective_names TEXT[],
  p_last_event_ids UUID[],
  p_statuses SMALLINT[]
) RETURNS INTEGER AS $$
DECLARE
  v_parked INTEGER;
BEGIN
  WITH given AS (
    -- One entry per pair: its last, as the one-cursor function called in order would have left it.
    SELECT DISTINCT ON (g.stream_id, g.perspective_name)
      g.stream_id, g.perspective_name, g.last_event_id, g.status,
      EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle l
        WHERE l.stream_id = g.stream_id AND l.state = 'Archived'
      ) AS archived
    FROM unnest(p_stream_ids, p_perspective_names, p_last_event_ids, p_statuses) WITH ORDINALITY
      AS g(stream_id, perspective_name, last_event_id, status, ord)
    ORDER BY g.stream_id, g.perspective_name, g.ord DESC
  ),
  parked AS (
    INSERT INTO __SCHEMA__.wh_event_archive_cursors AS p (stream_id, perspective_name, cursor_row)
    SELECT
      g.stream_id,
      g.perspective_name,
      to_jsonb(jsonb_populate_record(NULL::__SCHEMA__.wh_perspective_cursors, jsonb_build_object(
        'stream_id', g.stream_id,
        'perspective_name', g.perspective_name,
        'last_event_id', g.last_event_id,
        'status', g.status,
        'processed_at', NOW())))
    FROM given g
    WHERE g.archived
    ORDER BY g.stream_id, g.perspective_name
    ON CONFLICT (stream_id, perspective_name) DO UPDATE SET
      cursor_row = p.cursor_row || jsonb_build_object(
        'last_event_id', EXCLUDED.cursor_row -> 'last_event_id',
        'status', EXCLUDED.cursor_row -> 'status',
        'processed_at', EXCLUDED.cursor_row -> 'processed_at',
        'error', NULL,
        'rewind_trigger_event_id', NULL,
        'rewind_flagged_at', NULL,
        'rewind_first_flagged_at', NULL)
    RETURNING 1
  ),
  live AS (
    INSERT INTO __SCHEMA__.wh_perspective_cursors
      (stream_id, perspective_name, last_event_id, status, processed_at)
    SELECT g.stream_id, g.perspective_name, g.last_event_id, g.status, NOW()
    FROM given g
    WHERE NOT g.archived
    ORDER BY g.stream_id, g.perspective_name
    ON CONFLICT (stream_id, perspective_name) DO UPDATE SET
      last_event_id = EXCLUDED.last_event_id,
      status = EXCLUDED.status,
      processed_at = EXCLUDED.processed_at,
      error = NULL,
      rewind_trigger_event_id = NULL,
      rewind_flagged_at = NULL,
      rewind_first_flagged_at = NULL
    RETURNING 1
  )
  -- A data-modifying WITH runs to completion whether or not the query reads it, so the live upsert needs no reader.
  SELECT count(*) INTO v_parked FROM parked;
  RETURN v_parked;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_complete_rebuilt_cursors(UUID[], TEXT[], UUID[], SMALLINT[]) IS
  '556 (#2513): writes a flush of a rebuild''s cursors in one statement, each as wh_complete_rebuilt_cursor (273) writes '
  'one: a stream archived whole has its cursor parked in wh_event_archive_cursors, any other stream''s cursor is upserted '
  'into wh_perspective_cursors. A pair named more than once takes its last entry. Returns how many it parked. Called by '
  'both drivers'' rebuild cursor completers.';
