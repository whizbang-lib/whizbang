-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 224_DirectAppendTakesTheStreamLock.sql
-- Date: 2026-10-21
-- Description: A direct event-store append gets its version from the database, under the same per-stream lock the
--              emit chain takes, so concurrent writers to one stream never collide on (stream_id, version) (#1555).
--
--              Both drivers' IEventStore.AppendAsync read MAX(version) in one round trip and inserted at MAX + 1 in
--              another. Two writers appending to one stream at the same moment read the same tail, and one of them
--              hit idx_event_store_stream: the EF Core store turned that into "Concurrent modification detected" and
--              gave up, the Dapper store retried up to ten times with backoff. The system stream ($wb-system) is
--              written by every instance and every scope, so it met that race under ordinary load, and nothing about
--              the race was specific to it.
--
--              wh_append_event takes pg_advisory_xact_lock(hashtext('wh_event_store:' || stream_id)), the key
--              _emit_event_store_chain and _emit_event_store_chain_for_inbox take for the same stream (214), and only
--              then inserts the pointer row at COALESCE(MAX(version), -1) + 1 and the body row, returning the version
--              it assigned. The lock and the INSERT are separate statements of a plpgsql function on purpose: under
--              READ COMMITTED each statement takes a fresh snapshot, so the INSERT sees every append committed while
--              this call waited for the lock. One SQL statement with the lock inside a CTE would read MAX(version)
--              with a snapshot taken before the wait and collide exactly as before. A direct append and the emit
--              chain therefore serialize on one stream as well.
--
--              Direct appends keep numbering from 0 (the emit chain numbers from 1); existing streams and readers are
--              unchanged. The columns are the ones the drivers inserted before: flags, commit_sequence, insert_order
--              and insert_xid keep their column defaults. A duplicate event_id still fails with 23505 on the primary
--              key; a version can no longer collide, so the caller reports that as a duplicate event, never as a
--              race. The lock is a transaction lock: inside a caller's transaction it is held until that transaction
--              ends, as the emit chain's is.
--
--              Number 224: 223 is the perspective cursor fill factor (#1592).
--
-- Dependencies: 077/078 (pointer and body split: wh_event_store without inline body, wh_event_body), 197 and 205
--               (insert_order and insert_xid defaults), 214 (the emit chains' per-stream lock key)
-- Objects: wh_append_event
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');

-- <docs>fundamentals/events/event-store#appending-and-concurrency</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ParallelAppendsToOneStream_AllSucceed_WithVersionsZeroToNMinusOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ADirectAppend_WaitsForTheEmitChainsStreamLockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:TheFunction_ExistsOnce_AndAReRunOfTheMigrationLeavesItSoAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ParallelAppendsToOneStream_AllSucceedWithVersionsZeroToNMinusOneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_append_event(
  p_event_id UUID,
  p_stream_id UUID,
  p_aggregate_id UUID,
  p_aggregate_type TEXT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_metadata JSONB,
  p_scope JSONB,
  p_created_at TIMESTAMPTZ
) RETURNS INTEGER AS $$
DECLARE
  v_version INTEGER;
BEGIN
  -- The emit chains' key for this stream (214), so a direct append and a chain emit serialize too.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  -- A separate statement: its snapshot is taken after the lock was granted.
  INSERT INTO __SCHEMA__.wh_event_store (
    event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at
  )
  SELECT
    p_event_id,
    p_stream_id,
    p_aggregate_id,
    p_aggregate_type,
    p_event_type,
    p_scope,
    COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id), -1) + 1,
    p_created_at
  RETURNING version INTO v_version;

  -- The body half of the pointer/body split (077). Constraint-less ON CONFLICT, as the emit chain writes it:
  -- event_id is the table's only key.
  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  VALUES (p_event_id, p_event_data, p_metadata)
  ON CONFLICT DO NOTHING;

  RETURN v_version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_append_event(UUID, UUID, UUID, TEXT, TEXT, JSONB, JSONB, JSONB, TIMESTAMPTZ) IS
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), -1) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555).';
