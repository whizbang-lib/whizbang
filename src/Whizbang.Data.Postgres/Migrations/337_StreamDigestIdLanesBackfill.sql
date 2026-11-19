-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 337_StreamDigestIdLanesBackfill.sql
-- Date: 2026-11-06
-- Description: Fills the id lanes of the stream digests stored before 336, a bounded batch at a time (#2106).
--
--              336 added wh_stream_digests.id_lo / id_hi. A bucket the fold creates from then on carries them; a bucket
--              stored before keeps NULL (a fold never turns a partial value into a present one), and a compare that
--              reads it falls back to whole-stream sync. Filling them is a read of every stream's events, which cannot
--              run inside a migration: it would hold every stream it touched until the migration committed, bounded by
--              nothing but the store. So this migration only records whether there is anything to do, and a maintenance
--              step (StreamDigestIdLaneBackfill, idle-gated like every maintenance step) calls
--              wh_backfill_stream_digest_id_lanes_batch repeatedly, each call its own transaction over a bounded number
--              of streams.
--
--              A batch recomputes its streams' buckets with _wh_heal_stream_digests: id lanes that were never computed
--              are filled, and any bucket whose hash lanes or count differ from its events is rewritten AND returned
--              with both values, so the caller reports the drift (a Warning per bucket and the
--              stream_digest_drift_corrected count) before anyone relies on the corrected value. Nothing is corrected
--              silently.
--
-- Dependencies: 087 (wh_stream_digests), 301 (idx_stream_digests_stream), 336 (id_lo, id_hi, _wh_heal_stream_digests)
-- Objects: wh_backfill_stream_digest_id_lanes_batch; wh_settings rows stream_digest_id_lanes_backfill_version and
--          stream_digest_id_lanes_backfill_cursor
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- A store whose buckets all carry id lanes (a new database) has nothing to fill. ON CONFLICT DO NOTHING: a re-run of this
-- file never restarts a finished backfill and never finishes an unfinished one. The probe stops at the first NULL row.
INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
SELECT 'stream_digest_id_lanes_backfill_version',
       CASE WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_stream_digests d WHERE d.id_lo IS NULL OR d.id_hi IS NULL)
            THEN '0' ELSE '1' END,
       'integer',
       'Gates the one-time fill of wh_stream_digests id lanes for buckets stored before 336 (#2106). 0 = to do, 1 = done.',
       NOW(),
       'migration:337_StreamDigestIdLanesBackfill'
ON CONFLICT (setting_key) DO NOTHING;

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
VALUES ('stream_digest_id_lanes_backfill_cursor',
        __EMPTY_UUID__,
        'string',
        'The last stream id the stream digest id-lane backfill committed; the next batch starts after it (#2106, migration 337).',
        NOW(),
        'migration:337_StreamDigestIdLanesBackfill')
ON CONFLICT (setting_key) DO NOTHING;

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- One batch: the next p_limit digest streams after the cursor, each under its append lock, recomputed and healed with
-- id lanes filled. Returns, per bucket it changed, the heal's row ('filled', or a drift kind with the stored and
-- recomputed values) with visited = the streams the batch walked; a batch that changed nothing returns one row with a
-- NULL kind. visited = 0 when nothing is left (recorded as done; every later call costs one keyed read), -1 when another
-- caller holds the cursor.
--
--   Bounded     streams are walked in stream id order through idx_stream_digests_stream (301), one index probe per
--               stream, so a call costs its batch, not the store; each stream's events are read by idx_event_store_stream.
--   Exact       the batch takes its streams' append locks (every digest writer takes them) before it reads, in stream id
--               order as the emit chain does, so no fold is in flight while it recomputes: no settle window is needed.
--   Resumable   the cursor moves in the batch's own transaction; a failed batch leaves it and the next call repeats it.
--   Idempotent  a bucket already right is not rewritten.
SELECT __SCHEMA__.drop_all_overloads('wh_backfill_stream_digest_id_lanes_batch');
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestIdLaneBackfillSqlTests.cs:Backfill_FillsEveryStreamsIdLanes_AndRecordsThatItIsDoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestIdLaneBackfillSqlTests.cs:Backfill_ADriftedBucket_IsReturnedWithBothValues_ThenCorrectedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestIdLaneBackfillSqlTests.cs:Backfill_StoppedAfterOneBatch_ResumesAfterItsCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestIdLaneBackfillSqlTests.cs:Backfill_WhileAnotherCallerHoldsTheCursor_ReportsBusyAndWritesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestIdLaneBackfillSqlTests.cs:Backfill_OnceDone_ReadsNothingAndChangesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_backfill_stream_digest_id_lanes_batch(p_limit INTEGER)
RETURNS TABLE(visited INTEGER, kind TEXT, origin_service_id UUID, scope_tenant TEXT, event_type TEXT, stream_id UUID,
              stored_lo BIGINT, stored_hi BIGINT, stored_count INTEGER,
              recomputed_lo BIGINT, recomputed_hi BIGINT, recomputed_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  v_cursor_text TEXT;
  v_streams UUID[];
  v_visited INTEGER;
  v_reported INTEGER := 0;
  c_version_key CONSTANT TEXT := 'stream_digest_id_lanes_backfill_version';
  c_cursor_key CONSTANT TEXT := 'stream_digest_id_lanes_backfill_cursor';
  r RECORD;
BEGIN
  IF COALESCE((SELECT s.setting_value::INTEGER FROM __SCHEMA__.wh_settings s WHERE s.setting_key = c_version_key), 0) >= 1 THEN
    RETURN QUERY SELECT 0, NULL::text, NULL::uuid, NULL::text, NULL::text, NULL::uuid,
                        NULL::bigint, NULL::bigint, NULL::int, NULL::bigint, NULL::bigint, NULL::int;
    RETURN;
  END IF;

  -- One caller walks the cursor at a time. SKIP LOCKED: a caller that finds it held returns at once.
  SELECT s.setting_value INTO v_cursor_text
  FROM __SCHEMA__.wh_settings s
  WHERE s.setting_key = c_cursor_key
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_settings s WHERE s.setting_key = c_cursor_key) THEN
      RETURN QUERY SELECT -1, NULL::text, NULL::uuid, NULL::text, NULL::text, NULL::uuid,
                          NULL::bigint, NULL::bigint, NULL::int, NULL::bigint, NULL::bigint, NULL::int;
      RETURN;
    END IF;
    -- The cursor row is gone (an operator removed it): start again from the beginning, which is safe because a bucket
    -- already right is left alone. A concurrent caller that recreated it first wins and this one steps aside.
    INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
    VALUES (c_cursor_key, __EMPTY_UUID__, 'string',
            'The last stream id the stream digest id-lane backfill committed; the next batch starts after it (#2106, migration 337).',
            NOW(), 'wh_backfill_stream_digest_id_lanes_batch')
    ON CONFLICT (setting_key) DO NOTHING;
    SELECT s.setting_value INTO v_cursor_text
    FROM __SCHEMA__.wh_settings s
    WHERE s.setting_key = c_cursor_key
    FOR UPDATE SKIP LOCKED;
    IF NOT FOUND THEN
      RETURN QUERY SELECT -1, NULL::text, NULL::uuid, NULL::text, NULL::text, NULL::uuid,
                          NULL::bigint, NULL::bigint, NULL::int, NULL::bigint, NULL::bigint, NULL::int;
      RETURN;
    END IF;
  END IF;

  WITH RECURSIVE walk AS (
    (SELECT d.stream_id
     FROM __SCHEMA__.wh_stream_digests d
     WHERE d.stream_id > v_cursor_text::UUID
     ORDER BY d.stream_id
     LIMIT 1)
    UNION ALL
    SELECT (SELECT d.stream_id
            FROM __SCHEMA__.wh_stream_digests d
            WHERE d.stream_id > w.stream_id
            ORDER BY d.stream_id
            LIMIT 1)
    FROM walk w
    WHERE w.stream_id IS NOT NULL
  )
  SELECT array_agg(b.stream_id ORDER BY b.stream_id) INTO v_streams
  FROM (SELECT w.stream_id FROM walk w WHERE w.stream_id IS NOT NULL LIMIT p_limit) b;

  IF v_streams IS NULL THEN
    UPDATE __SCHEMA__.wh_settings
    SET setting_value = '1', updated_at = NOW(), updated_by = 'wh_backfill_stream_digest_id_lanes_batch'
    WHERE setting_key = c_version_key;
    RETURN QUERY SELECT 0, NULL::text, NULL::uuid, NULL::text, NULL::text, NULL::uuid,
                        NULL::bigint, NULL::bigint, NULL::int, NULL::bigint, NULL::bigint, NULL::int;
    RETURN;
  END IF;
  v_visited := cardinality(v_streams);

  -- Each stream's append lock, in stream id order (the emit chain's order). Held to commit.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || l.sid::text))
  FROM (SELECT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY u.sid) AS l;

  -- A separate statement, so its snapshot is taken after the locks were granted: nothing is in flight, so nothing needs
  -- to settle.
  FOR r IN SELECT * FROM __SCHEMA__._wh_heal_stream_digests(v_streams, INTERVAL '0 seconds', true) LOOP
    v_reported := v_reported + 1;
    RETURN QUERY SELECT v_visited, r.kind, r.origin_service_id, r.scope_tenant, r.event_type, r.stream_id,
                        r.stored_lo, r.stored_hi, r.stored_count, r.recomputed_lo, r.recomputed_hi, r.recomputed_count;
  END LOOP;

  UPDATE __SCHEMA__.wh_settings
  SET setting_value = v_streams[cardinality(v_streams)]::text, updated_at = NOW(),
      updated_by = 'wh_backfill_stream_digest_id_lanes_batch'
  WHERE setting_key = c_cursor_key;

  IF v_reported = 0 THEN
    RETURN QUERY SELECT v_visited, NULL::text, NULL::uuid, NULL::text, NULL::text, NULL::uuid,
                        NULL::bigint, NULL::bigint, NULL::int, NULL::bigint, NULL::bigint, NULL::int;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_backfill_stream_digest_id_lanes_batch(INTEGER) IS
  '337 (#2106): one batch of the stream digest id-lane backfill: the next p_limit digest streams after the cursor, '
  'under their append locks, healed with id lanes filled. Returns each changed bucket (filled or drift, with stored and '
  'recomputed values) with visited = streams walked; 0 when done, -1 when another caller holds the cursor. Resumable '
  'and idempotent; StreamDigestIdLaneBackfill drives it and reports drift.';
