-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 577_PerspectiveWritesHoldTheirPartitionEpoch.sql
-- Date: 2026-11-24
-- Description: A perspective row is fenced in the statement that writes it, holding its partition's epoch until the
--              write commits (#2556, phase 4 of #2540).
--
--              The fence was a check just before the write. A flip committed between the two (the deadline takeover of
--              a stuck owner, the one case fencing is for) let the stale write land. A write in map mode now calls
--              wh_hold_partition_epoch(partition, epoch) inside its own statement: the id of an insert or upsert is
--              wh_held_stream(id, partition, epoch), and an update or purge carries the call as a predicate of the rows
--              it touches. The call takes the ownership row FOR SHARE at that epoch, which the flip's UPDATE waits for,
--              so a flip cannot commit while the write is open; a flip that committed first is seen when the lock
--              re-reads the row, and the write raises WHF02 and lands nowhere. The driver isolates the raised row and
--              reports it fenced; the batch's other rows are written.
--
--              Off map mode, and for a write that applies no claimed work (a rebuild), no epoch is named: the statement
--              is as before and calls nothing.
--
--              Cost: in map mode, one primary-key probe and one row lock of wh_partition_ownership per row written.
--
-- Reversal: reversible (the down file drops both functions)
-- Dependencies: 565 (wh_partition_ownership)
-- Objects: wh_hold_partition_epoch, wh_held_stream

SELECT __SCHEMA__.drop_all_overloads('wh_hold_partition_epoch');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWritesAreFencedTests.cs:TheSink_AgainstAFlipThatCommitsWhileItWrites_WaitsForIt_AndIsFencedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWriteHoldSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_hold_partition_epoch(
  p_partition_number INTEGER,
  p_epoch BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
  IF p_epoch IS NULL THEN
    RETURN TRUE;
  END IF;
  -- FOR SHARE: held until the write's transaction ends, so the flip's UPDATE waits for it; under READ COMMITTED a row
  -- a flip changed after the snapshot is re-read, and its new epoch no longer matches.
  PERFORM 1
  FROM __SCHEMA__.wh_partition_ownership o
  WHERE o.partition_number = p_partition_number % 10000 AND o.epoch = p_epoch
  FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'partition % is no longer owned at epoch %; the write is fenced', p_partition_number % 10000, p_epoch
      USING ERRCODE = 'WHF02';
  END IF;
  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_hold_partition_epoch(INTEGER, BIGINT) IS
  '577 (#2556): holds a partition at the epoch a write''s work was claimed under, until the write commits: the ownership '
  'row FOR SHARE, which a flip waits for. TRUE for a write that names no epoch; raises WHF02 when the epoch moved on.';

SELECT __SCHEMA__.drop_all_overloads('wh_held_stream');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWriteHoldSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_held_stream(
  p_stream_id UUID,
  p_partition_number INTEGER,
  p_epoch BIGINT
) RETURNS UUID
LANGUAGE sql
VOLATILE
AS $$
  SELECT CASE WHEN __SCHEMA__.wh_hold_partition_epoch(p_partition_number, p_epoch) THEN p_stream_id END
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_held_stream(UUID, INTEGER, BIGINT) IS
  '577 (#2556): the stream id of a perspective row being inserted or upserted, once its partition is held at the '
  'claimed epoch (wh_hold_partition_epoch): the id expression of a fenced insert, so the fence is in the write''s statement.';
