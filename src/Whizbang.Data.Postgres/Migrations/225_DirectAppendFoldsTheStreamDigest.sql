-- Migration: 225_DirectAppendFoldsTheStreamDigest.sql
-- Date: 2026-10-21
-- Description: A direct event-store append folds its event into the stream digest, exactly as the emit chain does
--              (#1600).
--
--              The emit chains (_emit_event_store_chain and _emit_event_store_chain_for_inbox, last word 216) XOR
--              every newly stored, digest-eligible event into wh_stream_digests under the stream's advisory lock.
--              wh_append_event (224), the path IEventStore.AppendAsync takes on both drivers, stored the event and
--              never folded it. The full recompute the integrity sweep trusts (ComputeStreamDigestsAsync) has
--              always counted directly-appended rows in the local lane, so a stream written by both paths (the
--              global system stream, or any stream an application appends to directly) had a table digest that
--              did not cover all of its events, and close_stream subtracted rows that had never been added.
--
--              wh_append_event now folds the event it stored, inside the same call and under the same stream lock,
--              with the chain's bucket and predicates: the local origin (the zero uuid), the scope's tenant
--              (scope ->> 't', '' when absent), the stored event type, and the two hashtextextended lanes. An
--              ephemeral (flags & 8) or at-most-once (flags & 32) row is not folded, read from the row as stored;
--              nor is a digest-exempt type (__DIGEST_EXEMPT_TYPES__, the audit records, #1492), matched on the
--              type-name part of the stored event type as every other fold matches it. A duplicate event id still
--              fails with 23505 on the pointer insert before anything is folded, so nothing is ever folded twice.
--
--              Rows appended directly before this migration are not refolded here: the integrity audit's
--              trust-but-verify sweep (VerifyDigestTableAsync) already heals a bucket that differs from the recompute,
--              and reported each of these streams as drift on every pass; a refold inside a migration would scan the
--              whole event store.
--
--              Number 225: 224 is the direct append's stream lock (#1555).
--
-- Dependencies: 224 (wh_append_event), 087 (wh_stream_digests), 214 (the at-most-once flag), 216 (the digest
--               exemption by type)
-- Objects: wh_append_event
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');

-- <docs>fundamentals/events/event-store#appending-and-concurrency</docs>
-- <docs>resilience/stream-integrity#direct-appends-digested</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ParallelAppendsToOneStream_AllSucceed_WithVersionsZeroToNMinusOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ADirectAppend_WaitsForTheEmitChainsStreamLockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:TheFunction_ExistsOnce_AndAReRunOfTheMigrationLeavesItSoAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:AppendAsync_ParallelAppendsToOneStream_AllSucceedWithVersionsZeroToNMinusOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:MixedPathStream_TableDigestEqualsARecomputationOverAllItsEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:DirectAppend_FoldsByTenantIntoTheLocalBucketAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:DirectAppend_OfADigestExemptType_IsNotFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:CloseStream_AfterDirectAppends_TableStillMatchesTheRecomputeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:TheFunction_ExistsOnce_AndAReRunOfTheMigrationStillFoldsAsync</tests>
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
  v_flags INTEGER;
  c_flag_ephemeral CONSTANT INTEGER := 8;     -- EventFlags.Ephemeral (1 << 3), as the emit chain tests it
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
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
  RETURNING version, flags INTO v_version, v_flags;

  -- The body half of the pointer/body split (077). Constraint-less ON CONFLICT, as the emit chain writes it:
  -- event_id is the table's only key.
  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  VALUES (p_event_id, p_event_data, p_metadata)
  ON CONFLICT DO NOTHING;

  -- 225 (#1600): fold the event into its stream digest bucket, as the emit chain's digest_folds does, still under
  -- the stream lock taken above. One event, so one row: the chain's bit_xor and COUNT(*) over a single event are
  -- its two hashes and 1.
  IF COALESCE(v_flags, 0) & c_flag_ephemeral = 0
     AND COALESCE(v_flags, 0) & c_flag_at_most_once = 0
     AND split_part(p_event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) THEN
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    VALUES (
      __EMPTY_UUID__::uuid,  -- NOSONAR S1192: the local-origin sentinel recurs per function; PL/pgSQL has no file-level constants
      COALESCE(p_scope ->> 't', ''),
      p_event_type,
      p_stream_id,
      hashtextextended(p_event_id::text, 0),
      hashtextextended(p_event_id::text, 1),
      1,
      p_created_at
    )
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at;
  END IF;

  RETURN v_version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_append_event(UUID, UUID, UUID, TEXT, TEXT, JSONB, JSONB, JSONB, TIMESTAMPTZ) IS
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), -1) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555). 225: folds the stored event into wh_stream_digests as the emit chain does, unless it is ephemeral, at-most-once or of a digest-exempt type (#1600).';
