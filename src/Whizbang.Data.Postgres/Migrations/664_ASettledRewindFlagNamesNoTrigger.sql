-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 664_ASettledRewindFlagNamesNoTrigger.sql
-- Date: 2026-11-28
-- Description: A cursor that is not flagged RewindRequired holds no rewind trigger and no flag times.
--
--              Every path that settles a flag cleared the status bit (32) alone: update_perspective_cursors when a pair's
--              work is complete, _wh_settle_rewind_flags when a flush retires a pair's last row, and
--              wh_settle_stale_rewind_flags. rewind_trigger_event_id, rewind_flagged_at and rewind_first_flagged_at stayed
--              behind. Every reader of the flag tests the bit, so the leftovers looked inert, but the writers that raise a
--              new flag test the column:
--
--                the trigger        the emit chains merge a new straggler with the stored trigger whenever the column
--                                   is set, keeping the earlier of the two. A new flag inherited the dead trigger, so
--                                   its rewind started from an event the cursor had long since settled, a wider
--                                   replay than the late event asks for
--                the tip snapshot   620's trigger drops the tip at or above a late event only when the trigger column
--                                   changes. A new flag that kept the dead trigger changed nothing, so the tip taken
--                                   without the late event stayed
--                the first time     rewind_first_flagged_at is COALESCEd, so a new flag dated from the settled one
--
--              A BEFORE trigger now clears the three columns of any cursor row written without the bit, so every path
--              that settles a flag (these three, and any later one) settles all of it, on any instance of any version.
--              Its WHEN clause is evaluated before the function is called, so an ordinary cursor write (no bit, no
--              trigger) costs no function call. The cursors an older build left with a dead trigger are cleared once
--              here; the statement is idempotent (with the trigger in place a rerun finds nothing).
--
--              Number 664: 663 is taken on develop.
--
-- Dependencies: 374 (_wh_settle_rewind_flags, wh_settle_stale_rewind_flags), 531 (update_perspective_cursors),
--               620 (trg_wh_drop_stale_tip_snapshots_on_flag, which now sees every new flag as a change)
-- Objects: _wh_unflagged_cursor_names_no_trigger, trg_wh_unflagged_cursor_names_no_trigger
-- Reversal: reversible (the down file drops the trigger and its function; the dead triggers the cleanup cleared are not
--           restored, and nothing read them)

-- A cursor row written without RewindRequired names no trigger and no flag times.
-- <docs>fundamentals/perspectives/rewind#a-settled-flag-names-no-trigger</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_RetiringTheLastRowSettlesTheFlag_ClearsItsTriggerAndTimesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_ACursorCompletionWithNothingPending_ClearsTheTriggerAndTimesItSettlesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:SettleStaleRewindFlags_ClearsTheTriggerAndTimesOfEveryFlagItSettlesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_unflagged_cursor_names_no_trigger()
RETURNS TRIGGER AS $$
BEGIN
  NEW.rewind_trigger_event_id := NULL;
  NEW.rewind_flagged_at := NULL;
  NEW.rewind_first_flagged_at := NULL;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_unflagged_cursor_names_no_trigger() IS
  'Trigger (664): a cursor row written without RewindRequired (status bit 32) holds no rewind trigger and no flag times, '
  'so a settled flag is never merged into the next one.';

DROP TRIGGER IF EXISTS trg_wh_unflagged_cursor_names_no_trigger ON __SCHEMA__.wh_perspective_cursors;
CREATE TRIGGER trg_wh_unflagged_cursor_names_no_trigger
  BEFORE INSERT OR UPDATE ON __SCHEMA__.wh_perspective_cursors
  FOR EACH ROW
  WHEN ((NEW.status & 32) = 0
        AND (NEW.rewind_trigger_event_id IS NOT NULL
             OR NEW.rewind_flagged_at IS NOT NULL
             OR NEW.rewind_first_flagged_at IS NOT NULL))
  EXECUTE FUNCTION __SCHEMA__._wh_unflagged_cursor_names_no_trigger();

-- The cursors an older build settled without clearing what the flag named. The trigger above clears the columns.
UPDATE __SCHEMA__.wh_perspective_cursors
SET rewind_trigger_event_id = NULL
WHERE (status & 32) = 0
  AND (rewind_trigger_event_id IS NOT NULL OR rewind_flagged_at IS NOT NULL OR rewind_first_flagged_at IS NOT NULL);
