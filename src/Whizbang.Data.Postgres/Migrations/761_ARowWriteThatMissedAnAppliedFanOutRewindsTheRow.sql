-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 761_ARowWriteThatMissedAnAppliedFanOutRewindsTheRow.sql
-- Date: 2026-11-28
-- Description: A live write of a row a fan-out targets that missed a foreign event the collective sink applied rewinds
--              the row (#2622).
--
--              The sink applies a fan-out (IPerspectiveFanOutFor) as one set-based update of the rows whose key holds
--              the event's stream. A row's runner reads the foreign streams its keys hold before it writes the row
--              (#2621, #2622). The two can cross: the runner reads a customer's stream and finds no rename, the sink
--              commits a rename whose update finds no row with the customer yet, and the runner then writes the row
--              with the customer and without the rename. Neither side sees the other, and the row would keep the old
--              name until its next rewind. After each live write of such a row the runner calls this function, which
--              finds that rename and rewinds the row, so its replay folds it.
--
--                wh_rewind_row_fan_outs_missed  for one written row: is there an event of the model's fan-out types on
--                                 a stream one of its keys holds, in its tenant, committed after the point the row took
--                                 the key (its commit sequence, or its id when either side has none), that the write did
--                                 not fold, and whose sink row is no longer pending (the sink applied it)? If not, it
--                                 returns 0 and writes nothing. If so, the row's cursor is created when its first
--                                 completion has not created it yet, the row is rewound from the batch's first event
--                                 through _wh_rewind_perspectives_from (600), and the function returns what it flagged.
--
--              Cost: one read, by the event store's (stream_id, version) index on each key's stream and the sink row's
--              unique key (stream_id, perspective_name, event_id); no write unless a fan-out was missed, which needs the
--              race above. Called once per live write of a row a fan-out targets, never for any other model.
-- Reversal: reversible (the down file drops the function; a row that misses a fan-out in the race then keeps it missed
--           until its next rewind, the behavior of the build the reversal returns to)
-- Dependencies: 600 (_wh_rewind_perspectives_from), 009 (wh_perspective_events), wh_perspective_cursors
-- Objects: wh_rewind_row_fan_outs_missed
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_rewind_row_fan_outs_missed');
-- <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/FanOutReplayIntegrationTests.Interleaving.cs:Live_TheSinkCommitsARenameBetweenTheOrdersReadAndItsWrite_TheOrderIsRewound_AsReplayAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanOutMissedRewindSqlTests.cs:AMissedAppliedFanOut_RewindsTheRow_AndAnyOtherFanOutDoesNotAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_rewind_row_fan_outs_missed(
  p_stream_id UUID,
  p_perspective_name TEXT,
  p_trigger_event_id UUID,
  p_tenant TEXT,
  p_event_types TEXT[],
  p_key_stream_ids UUID[],
  p_after_sequences BIGINT[],
  p_after_event_ids UUID[],
  p_folded_event_ids UUID[],
  p_now TIMESTAMPTZ
) RETURNS INTEGER AS $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM unnest(p_key_stream_ids, p_after_sequences, p_after_event_ids) AS k(sid, after_sequence, after_id)
    JOIN __SCHEMA__.wh_event_store es ON es.stream_id = k.sid
    WHERE es.event_type = ANY(p_event_types)
      AND es.scope ->> 't' = p_tenant
      -- Committed after the row took the key: by commit sequence when both carry one, else by id (CollectiveReplayStart).
      AND CASE
            WHEN k.after_sequence IS NOT NULL AND es.commit_sequence IS NOT NULL THEN es.commit_sequence > k.after_sequence
            WHEN k.after_id IS NOT NULL THEN es.event_id > k.after_id
            ELSE TRUE
          END
      AND es.event_id <> ALL(p_folded_event_ids)
      -- Applied by the sink: its sink row is no longer pending. A pending one the sink applies after this write.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = es.stream_id
          AND pe.perspective_name = '__collective__'
          AND pe.event_id = es.event_id
          AND pe.processed_at IS NULL)
  ) THEN
    RETURN 0;
  END IF;

  -- The race is a row's first write: it runs before the batch's completion creates the row's cursor, and the rewind
  -- flags a cursor. The completion that follows moves the cursor and keeps its flag, as it does for any rewind.
  INSERT INTO __SCHEMA__.wh_perspective_cursors (stream_id, perspective_name, status)
  VALUES (p_stream_id, p_perspective_name, 0)
  ON CONFLICT (stream_id, perspective_name) DO NOTHING;

  RETURN __SCHEMA__._wh_rewind_perspectives_from(
    ARRAY[p_stream_id], ARRAY[p_perspective_name], ARRAY[p_trigger_event_id], p_now);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_rewind_row_fan_outs_missed(UUID, TEXT, UUID, TEXT, TEXT[], UUID[], BIGINT[], UUID[], UUID[], TIMESTAMPTZ) IS
'761 (#2622): after a live write of row p_stream_id, rewinds it from p_trigger_event_id when an event of p_event_types on one of p_key_stream_ids, in p_tenant, committed after the matching start (p_after_sequences, else p_after_event_ids), not among p_folded_event_ids, has no pending __collective__ sink row: the sink applied it to no row while the write was in flight. Returns 0 and writes nothing otherwise.';
