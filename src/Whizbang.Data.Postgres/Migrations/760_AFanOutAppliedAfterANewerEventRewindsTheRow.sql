-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 760_AFanOutAppliedAfterANewerEventRewindsTheRow.sql
-- Date: 2026-11-28
-- Description: A foreign event the collective sink applies to a row that has already folded a newer event of its own
--              rewinds that row (#2622).
--
--              A fan-out (IPerspectiveFanOutFor) is applied live by the collective sink as one set-based update of
--              every row whose key column holds the event's stream. A row folds its own events through its runner, on
--              its own schedule, so the sink can apply a rename to a row that already folded an event committed after
--              the rename: live the rename then lands last, where the commit order puts it first, and an own event that
--              wrote the same field is overwritten. A rebuild folds them in commit order (#2621), so the live row and
--              its rebuild would differ. The sink calls this function after each fan-out it applies, and every such row
--              is rewound: its replay folds the rename where it was committed.
--
--                wh_rewind_fan_out_rows_applied_late  for one fan-out (perspective table, key column, the perspective
--                                 whose cursors the rows have) and one applied foreign event: the rows of the event's
--                                 tenant whose key holds the event's stream and whose last folded own event
--                                 (metadata CommitSequence, written with the row) was committed after the event. Each
--                                 is rewound from its first own event committed after the foreign event, through
--                                 _wh_rewind_perspectives_from (600), which floors the trigger, flags the cursor and
--                                 queues it. Returns the rows newly flagged.
--
--              Cost: one read of the rows the event targets, by the key column's index the fan-out declares
--              ([Indexed], WHIZ126), and one walk down each matching row's stream by its version index. Called once per
--              applied fan-out per perspective that fans the event out; never on the claim or store paths.
-- Reversal: reversible (the down file drops the function; a row a fan-out reaches late is then rewound by nothing, the
--           behavior of the build the reversal returns to)
-- Dependencies: 600 (_wh_rewind_perspectives_from)
-- Objects: wh_rewind_fan_out_rows_applied_late
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_rewind_fan_out_rows_applied_late');
-- <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanOutLateApplyRewindSqlTests.cs:ARowPastTheFanOut_IsRewoundFromItsFirstEventAfterIt_AndNoOtherRowIsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanOutLateApplyRewindSqlTests.cs:AnUnstampedFanOut_RewindsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/FanOutReplayIntegrationTests.Interleaving.cs:Live_TheSinkAppliesAnOlderRenameAfterTheOrdersNewerEvent_RewindsTheOrder_AsReplayAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_rewind_fan_out_rows_applied_late(
  p_table TEXT,
  p_key_column TEXT,
  p_perspective_name TEXT,
  p_key_stream_id UUID,
  p_tenant TEXT,
  p_event_id UUID,
  p_now TIMESTAMPTZ
) RETURNS INTEGER AS $$
DECLARE
  v_sequence BIGINT;
  v_rows UUID[];
  v_triggers UUID[];
BEGIN
  SELECT es.commit_sequence INTO v_sequence
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = p_event_id;
  -- An event with no commit position cannot be compared with a row's own events; the sink stamps none it applies.
  IF v_sequence IS NULL THEN
    RETURN 0;
  END IF;

  EXECUTE format(
    'SELECT array_agg(r.id ORDER BY r.id), array_agg(t.event_id ORDER BY r.id)
     FROM __SCHEMA__.%I r
     CROSS JOIN LATERAL (
       SELECT es.event_id
       FROM __SCHEMA__.wh_event_store es
       WHERE es.stream_id = r.id AND es.commit_sequence > $3
       ORDER BY es.version
       LIMIT 1
     ) t
     WHERE r.%I = $1 AND r.scope ->> ''t'' = $2 AND (r.metadata ->> ''CommitSequence'')::bigint > $3',
    p_table, p_key_column)
  INTO v_rows, v_triggers
  USING p_key_stream_id, p_tenant, v_sequence;
  IF v_rows IS NULL THEN
    RETURN 0;
  END IF;

  RETURN __SCHEMA__._wh_rewind_perspectives_from(
    v_rows, array_fill(p_perspective_name, ARRAY[cardinality(v_rows)]), v_triggers, p_now);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_rewind_fan_out_rows_applied_late(TEXT, TEXT, TEXT, UUID, TEXT, UUID, TIMESTAMPTZ) IS
'760 (#2622): after the collective sink applies a fan-out event to the rows of p_table whose p_key_column holds p_key_stream_id in p_tenant, rewinds each of them whose last folded own event (metadata CommitSequence) was committed after the event, from its first own event committed after it, through _wh_rewind_perspectives_from. Returns the rows newly flagged; 0 for an event with no commit sequence.';
