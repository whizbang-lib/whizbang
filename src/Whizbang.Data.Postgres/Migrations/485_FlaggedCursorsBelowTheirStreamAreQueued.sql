-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 485_FlaggedCursorsBelowTheirStreamAreQueued.sql
-- Date: 2026-11-17
-- Description: A cursor flagged for a rewind, sitting below its stream's last version with no work left to claim, gets
--              its trigger queued on upgrade, so the fixed rewind runs at once (#2379).
--
--              A stream whose first event carries its highest id rewound forever: the rewind restored the snapshot at
--              that event and read the events after its id, which are none. The fix reads after an event's version. A
--              stream left looping that still has pending work is claimed again and settles with one rewind; nothing
--              is needed for it. A stream whose work was retired while it looped (or before) keeps its cursor below the
--              stream's last version with the RewindRequired flag set and nothing to claim: the flag waits for the
--              stream's next event and holds a blocking startup scan on every start meanwhile.
--              wh_queue_unreachable_rewinds() queues the trigger of each such pair, unleased, as 455 does for its own
--              shape (a trigger whose processed row is not purged yet has that row made pending again). The flag is
--              kept. A pair with pending work, a cursor at its stream's last version, an unflagged cursor and a flag
--              whose trigger its stream does not hold are left alone. Called once here; a re-run finds every queued
--              pair holding pending work and queues nothing.
--
--              Number 485: this bundle's range for #2379 (the integrator renumbers).
--
-- Reversal: reversible (the function goes; a queued row is ordinary work and is processed as any other)
-- Dependencies: 009 (wh_perspective_events), 455 (the one-order cursor completion that clears the flag)
-- Objects: wh_queue_unreachable_rewinds

-- Queues the trigger of every cursor flagged RewindRequired whose pair has no pending work and whose stream holds an
-- event above the cursor's version (#2379). Returns the pairs queued.
-- <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:QueueUnreachable_QueuesOnePerFlaggedPairBelowItsStreamWithNoWork_NothingElse_AndARerunQueuesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorOneOrderSqlTests.cs:QueueUnreachable_ATriggerWhoseProcessedRowIsNotYetPurged_IsQueuedAgainOnThatRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRewindLoopTests.IdOrder.cs:AStreamWhoseFirstEventHasTheHighestId_WithItsWorkRetired_IsQueuedOnUpgrade_AndConvergesAfterOneRewindAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_queue_unreachable_rewinds() RETURNS INTEGER AS $$
DECLARE
  v_queued INTEGER;
BEGIN
  WITH stuck AS (
    SELECT c.stream_id, c.perspective_name, c.rewind_trigger_event_id AS trigger_event_id
    FROM __SCHEMA__.wh_perspective_cursors c
    JOIN __SCHEMA__.wh_event_store ce ON ce.event_id = c.last_event_id
    JOIN __SCHEMA__.wh_event_store te ON te.event_id = c.rewind_trigger_event_id AND te.stream_id = c.stream_id
    WHERE (c.status & 32) = 32
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store above
        WHERE above.stream_id = c.stream_id AND above.version > ce.version)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = c.stream_id AND pe.perspective_name = c.perspective_name
          AND pe.processed_at IS NULL)
  ),
  queued AS (
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
    SELECT gen_random_uuid(), s.stream_id, s.perspective_name, s.trigger_event_id,
      __SCHEMA__.compute_partition(s.stream_id, 10000), 1, 0, NOW(), 150
    FROM stuck s
    -- The trigger's processed row may not be purged yet (debug mode keeps it): it is made pending again, so the rewind
    -- runs, instead of failing the migration on the row's unique key.
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO UPDATE
      SET processed_at = NULL, status = EXCLUDED.status, attempts = 0, error = NULL, failure_reason = NULL,
          instance_id = NULL, lease_expiry = NULL, claimed_at = NULL, scheduled_for = NULL
    RETURNING stream_id
  )
  SELECT count(*) INTO v_queued FROM queued;
  RETURN v_queued;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_queue_unreachable_rewinds() IS
'#2379: queues the trigger of each cursor flagged RewindRequired that sits below its stream''s last version with no pending work, unleased, so the rewind the flag asks for runs at once instead of waiting for the stream''s next event. The flag is kept. Returns the pairs queued; a re-run queues nothing.';

SELECT __SCHEMA__.wh_queue_unreachable_rewinds();
