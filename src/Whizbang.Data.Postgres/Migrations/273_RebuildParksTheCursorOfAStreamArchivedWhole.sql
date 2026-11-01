-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 273_RebuildParksTheCursorOfAStreamArchivedWhole.sql
-- Date: 2026-11-01
-- Description: The rebuild's cursor write, wh_complete_rebuilt_cursor (#1931): the cursor of a stream archived whole is
--              parked with the stream instead of pointed at an event the hot store does not hold.
--
--              A rebuild that includes streams archived whole (RebuildArchivedData.IncludeAll) replays such a stream
--              through the archive, so its last event, which the rebuilt cursor names, is archived. The live cursor table
--              must not name it (the EF Core schema's fk_perspective_cursors_event refuses it, and on any schema the
--              cursor would name an event no read finds). A whole-stream archive already parks the stream's cursors in
--              wh_event_archive_cursors (262), as whole rows, and a restore puts them back. This function writes the
--              rebuilt cursor there while the stream's lifecycle row says Archived: it moves an existing parked cursor to
--              the rebuilt event, or parks a new one, and the restore brings it back with the events it names. Any other
--              stream's cursor is upserted into wh_perspective_cursors as both drivers' rebuild completers did before.
--
--              Both drivers' rebuild cursor completers call it, so the two write the same rows. It runs on the rebuild
--              path only; the live drain does not call it.
--
--              Number 273: reserved for this change.
--
-- Dependencies: 260 (wh_stream_lifecycle), 262 (wh_event_archive_cursors, the restore that returns parked cursors)
-- Objects: wh_complete_rebuilt_cursor
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_complete_rebuilt_cursor');

-- <docs>fundamentals/perspectives/rebuild#archived-data</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Completer_TheCursorOfAStreamArchivedWhole_IsParked_AndTheRestoreBringsItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Completer_TheCursorOfAHotStream_IsWrittenLive_AndNothingIsParkedAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRebuildTests.cs:Completer_TheCursorOfAStreamArchivedWhole_IsParked_AndTheRestoreBringsItBackAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_complete_rebuilt_cursor(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_last_event_id UUID,
  p_status SMALLINT
) RETURNS BOOLEAN AS $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.stream_id = p_stream_id AND l.state = 'Archived'
  ) THEN
    INSERT INTO __SCHEMA__.wh_event_archive_cursors AS p (stream_id, perspective_name, cursor_row)
    VALUES (
      p_stream_id,
      p_perspective_name,
      to_jsonb(jsonb_populate_record(NULL::__SCHEMA__.wh_perspective_cursors, jsonb_build_object(
        'stream_id', p_stream_id,
        'perspective_name', p_perspective_name,
        'last_event_id', p_last_event_id,
        'status', p_status,
        'processed_at', NOW()))))
    ON CONFLICT (stream_id, perspective_name) DO UPDATE SET
      cursor_row = p.cursor_row || jsonb_build_object(
        'last_event_id', p_last_event_id,
        'status', p_status,
        'processed_at', NOW(),
        'error', NULL,
        'rewind_trigger_event_id', NULL,
        'rewind_flagged_at', NULL,
        'rewind_first_flagged_at', NULL);
    RETURN TRUE;
  END IF;

  INSERT INTO __SCHEMA__.wh_perspective_cursors
    (stream_id, perspective_name, last_event_id, status, processed_at)
  VALUES (p_stream_id, p_perspective_name, p_last_event_id, p_status, NOW())
  ON CONFLICT (stream_id, perspective_name) DO UPDATE SET
    last_event_id = EXCLUDED.last_event_id,
    status = EXCLUDED.status,
    processed_at = EXCLUDED.processed_at,
    error = NULL,
    rewind_trigger_event_id = NULL,
    rewind_flagged_at = NULL,
    rewind_first_flagged_at = NULL;
  RETURN FALSE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_complete_rebuilt_cursor(UUID, TEXT, UUID, SMALLINT) IS
  '273 (#1931): writes a rebuild''s cursor. A stream archived whole (its lifecycle row Archived) has its cursor parked in '
  'wh_event_archive_cursors, an existing parked cursor moved to the rebuilt event or a new one parked, and the restore '
  'brings it back; any other stream''s cursor is upserted into wh_perspective_cursors. Returns whether it parked. Called '
  'by both drivers'' rebuild cursor completers.';
