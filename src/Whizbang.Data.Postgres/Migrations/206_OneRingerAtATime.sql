-- Migration: 206_OneRingerAtATime.sql
-- Date: 2026-10-12
-- Description: One ringer rings at a time per schema, and it drains; the others return at once (#1336).
--
--              146 moved NOTIFY out of the hot transactions: they queue a doorbell row and the driver runs
--              ring_doorbells() after each hot call commits. The rows are taken FOR UPDATE SKIP LOCKED, so ringers
--              never wait on each other's rows, but every ringer that notified still committed on the one
--              database-wide NOTIFY lock (PreCommit_Notify: locktype=object, classid=pg_database, objid=0). Under
--              load every hot call is followed by a ring, so the rings queued behind each other one commit at a
--              time, and each hot caller waited for its ring. Measured under bulk imports across 13 service
--              databases: 3,111 waits in 20 minutes, median 371 ms, p95 1,028 ms, max 2,266 ms, the blocker another
--              ring_doorbells() in 30,606 of 30,796 blocked samples.
--
--              A ring now:
--                1. Returns 0 at once when nothing is queued, without taking the turn.
--                2. Tries the turn: a transaction-scoped advisory lock keyed to this schema's queue table. When
--                   another ringer has it, returns 0 at once, leaving the queue to that ringer. Transaction-scoped
--                   on purpose: a session lock is unsafe on a pooled connection behind PgBouncer.
--                3. Rings one bounded batch, as before.
--
--              The driver (DoorbellRinger) calls again while a call rang anything, so the batch bounds one commit,
--              not the drain. Nothing is left behind: a caller queues its doorbell and commits before it rings, so
--              when it finds the turn taken, the holder is mid-pass and the doorbell is already committed. The
--              holder stops only on a call whose step 1 found the queue empty, and that call starts after the
--              holder's previous commit, which is after the busy caller's doorbell committed; so it sees the
--              doorbell and rings it. A holder that itself finds the turn taken hands the same duty to the ringer
--              that has it.
--
--              Unchanged: which doorbells are queued and which the debounce suppresses (137, 141, 143); the
--              coalescing of identical (channel, payload) pairs within a batch; the claim worker's drain linger
--              (#665). The handoff adds at most one batch of delivery delay, well inside the linger's margin over
--              the debounce window.
--
--              A ring inside a caller's own transaction that has already written (every hot call writes before
--              it rings) does not take the turn: it would hold it until that caller commits and would not pass again
--              afterwards. It rings its batch as 146 did. The library rings after its own commits; this covers a
--              caller that runs a coordinator call inside a transaction of its own.
--
--              The turn is per schema because the queue is: services sharing one database each keep one ringer.
--
-- Dependencies: 146 (wh_doorbell_queue, _queue_doorbell, ring_doorbells)
-- Objects: ring_doorbells

-- <docs>fundamentals/work-coordinator/notifications-and-pgbouncer#doorbells-ring-after-commit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DoorbellsRingAfterCommitSqlTests.cs:RingDoorbells_WhileAnotherRingerHasTheTurn_ReturnsAtOnceAndLeavesTheQueueToItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DoorbellsRingAfterCommitSqlTests.cs:RingDoorbells_TheHolderRingsWhatABusyRingerLeft_OnItsNextPassAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DoorbellsRingAfterCommitSqlTests.cs:RingDoorbells_OnAnEmptyQueue_DoesNotTakeTheTurnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DoorbellsRingAfterCommitSqlTests.cs:RingDoorbells_InsideATransactionThatHasWritten_DoesNotTakeTheTurnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DoorbellsRingAfterCommitSqlTests.cs:RingDoorbells_DeliversQueuedRingsAndCoalescesDuplicatesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DoorbellsRingAfterCommitSqlTests.cs:RingDoorbells_TwoRingersDoNotDoubleRingOrBlockEachOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DoorbellRingerTests.cs:RingAsync_MoreQueuedThanOneBatch_KeepsRingingUntilTheQueueIsEmptyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.ring_doorbells(
  p_max INTEGER DEFAULT 1000
) RETURNS INTEGER AS $$
DECLARE
  v_rung INTEGER := 0;
  r RECORD;
BEGIN
  -- 1. Nothing queued: return without taking the turn.
  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_doorbell_queue) THEN
    RETURN 0;
  END IF;

  -- 2. Another ringer has the turn: it rings what is queued, this one included. A ring inside a caller's
  --    transaction that has already written never takes the turn: it would keep it until that caller commits
  --    and would not pass again afterwards, so it rings its batch as 146 did and leaves the turn to others.
  IF pg_current_xact_id_if_assigned() IS NULL
     AND NOT pg_try_advisory_xact_lock(
       ((('__SCHEMA__.wh_doorbell_queue'::regclass)::oid::bigint) << 32)
       | (hashtext('ring_doorbells')::bigint & 4294967295)) THEN
    RETURN 0;
  END IF;

  -- 3. One bounded batch: coalesce identical rings, delete, notify.
  FOR r IN
    WITH taken AS (
      DELETE FROM __SCHEMA__.wh_doorbell_queue q
      WHERE q.id IN (
        SELECT id FROM __SCHEMA__.wh_doorbell_queue
        ORDER BY id
        LIMIT GREATEST(p_max, 1)
        FOR UPDATE SKIP LOCKED
      )
      RETURNING q.channel, q.payload
    )
    SELECT DISTINCT channel, payload FROM taken
  LOOP
    PERFORM pg_notify(r.channel, r.payload);
    v_rung := v_rung + 1;
  END LOOP;
  RETURN v_rung;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.ring_doorbells(INTEGER) IS
  'Rings queued doorbells (146, 206): returns 0 at once when nothing is queued or another ringer has the turn (a '
  'transaction-scoped advisory lock per schema); otherwise deletes up to p_max queued rows, coalesces identical '
  '(channel, payload) pairs and issues pg_notify for each. The driver calls again while a call rang anything, so one '
  'ringer drains and the others never queue on the NOTIFY lock. Returns the number of distinct notifications sent.';
