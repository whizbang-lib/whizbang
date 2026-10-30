-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 263_TruncateLeavesDigestExemptTypesAlone.sql
-- Date: 2026-10-29
-- Description: wh_truncate_stream_prefix leaves out the digest-exempt types exactly as every fold does (#1891).
--
--              The emit chains, the direct append, the epoch fold and the recomputes never fold an event of a
--              digest-exempt type (__DIGEST_EXEMPT_TYPES__, the audit records, 216, #1492). The truncate left
--              them in on its side: it counted them into the per-lane ranges it refolds and, through them, bumped
--              the origin generation, which tells every consumer that folded history changed and has it reset its
--              seals and re-verify. A prefix holding only audit records changed no folded history at all. Its
--              digest subtraction also read them, and matched a bucket only where an earlier release had folded
--              one, which the verification sweep removes rather than heals.
--
--              Both reads now carry the predicate the folds use, matched on the type name without its assembly:
--
--                split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
--
--              so the truncate subtracts, refolds and reports exactly what the folds added. Nothing else in the
--              function changes; the body is the 240 definition copied whole (rule 5), at the same arity.
-- Dependencies: 240 (wh_truncate_stream_prefix, the last word before this file), 216 (__DIGEST_EXEMPT_TYPES__),
--               093 (epoch refold, origin generation)
-- Objects: wh_truncate_stream_prefix
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>fundamentals/events/event-streams#truncating-a-stream-prefix</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:TruncateStreamPrefix_AfterMigration_IsTheOnlyNameTheTruncateGoesByAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:TruncateStreamPrefix_TheLastMigrationNamingCloseStream_AlsoDefinesTheNewNameAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_ConsumptionGate_BlocksThenTruncatesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_NoCarryForward_RefusesTotalTruncationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_DebugMode_SkippedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_ActiveDestructionHoldInsideTheRange_IsRefused_ALapsedOneIsRemovedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_CursorBehindABoundaryEventItsPerspectiveStillOwes_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_CursorOfAPerspectiveWithNothingLeftToConsume_IsReanchoredOntoTheBoundaryEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_RemovesEverySnapshotThatNeedsATruncatedEvent_KeepsTheExactFloorsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_RemovesProcessedWorkRowsOfTruncatedEvents_KeepsTheirDeduplicationEntriesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_Archive_MovesDetailToColdStorage_RetrievableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:CloseStream_TruncatingAnAtMostOnceEvent_SubtractsOnlyWhatWasFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:CloseStream_SubtractsTruncatedRows_SurvivorDigestRemainsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:TruncateStreamPrefix_APrefixOfOnlyAnAuditRecord_IsNotAMutationOfFoldedHistoryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:TruncateStreamPrefix_APrefixHoldingAnAuditRecord_LeavesTheDigestEqualToAFreshFoldAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamCloseCompanionTests.cs:TruncateStreamPrefix_OnADapperMigratedDatabase_ReplacesCloseStreamAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamCloseCompanionTests.cs:CloseStream_OnADapperMigratedDatabase_ReanchorsTheCursor_RemovesStrandedSnapshotsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_truncate_stream_prefix(
  p_stream_id UUID, p_through_version BIGINT, p_archive BOOLEAN DEFAULT FALSE)
RETURNS TABLE(truncate_status TEXT, events_truncated BIGINT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_blocked BIGINT;
  v_carry BIGINT;
  v_held BIGINT;
  v_boundary UUID;
  v_behind BIGINT;
  v_deleted BIGINT;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- debug_mode retains forensic history; a prefix truncate destroys detail, so skip it entirely (like the reaper).
  SELECT (setting_value = 'true') INTO v_debug FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode';
  IF COALESCE(v_debug, FALSE) THEN
    RETURN QUERY SELECT 'debug_skipped'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (1) Consumption gate: any perspective work item for an event in this stream at/below the close point that
  -- is still UNPROCESSED blocks the close.
  SELECT count(*) INTO v_blocked
  FROM __SCHEMA__.wh_perspective_events pe
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND pe.processed_at IS NULL;

  IF v_blocked > 0 THEN
    RETURN QUERY SELECT 'blocked'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (2) Carry-forward guard: refuse to discard a stream's entire history; there must be a surviving event
  -- above the close point (the domain's closing event / new origin).
  SELECT count(*) INTO v_carry
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version > p_through_version;

  IF v_carry = 0 THEN
    RETURN QUERY SELECT 'no_carry_forward'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Migration 212 (#1486), (3) Hold guard: an active destruction hold on an event in the range is a recorded
  -- PreDestruction decision to keep that event (Cancel, or Defer until a time not yet reached).
  SELECT count(*) INTO v_held
  FROM __SCHEMA__.wh_event_destruction_hold h
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND h.hold_until > NOW();

  IF v_held > 0 THEN
    RETURN QUERY SELECT 'held'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Migration 212 (#1486), (4) Cursor guard: a cursor inside the range moves onto the boundary event (the
  -- first survivor above the close point). The cursor is the read position (the next apply reads events with
  -- an id above it), so moving it onto a boundary event its perspective still owes would skip that event.
  -- Refuse until the perspective has consumed it.
  SELECT es.event_id INTO v_boundary
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version > p_through_version
  ORDER BY es.version
  LIMIT 1;

  SELECT count(*) INTO v_behind
  FROM __SCHEMA__.wh_perspective_cursors c
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = c.last_event_id
  JOIN __SCHEMA__.wh_perspective_events pe
    ON pe.stream_id = c.stream_id
   AND pe.perspective_name = c.perspective_name
   AND pe.event_id = v_boundary
  WHERE c.stream_id = p_stream_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND pe.processed_at IS NULL;

  IF v_behind > 0 THEN
    RETURN QUERY SELECT 'cursor_behind'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Archive BEFORE truncate. Same transaction as the DELETEs below => atomic: a failure here rolls back the
  -- whole close, so the detail is never lost (stays hot) nor left half-archived. Body columns come from
  -- wh_event_body (post-split every body lives there); LEFT JOIN tolerates an already-reaped body defensively.
  IF p_archive THEN
    INSERT INTO __SCHEMA__.wh_event_archive
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at)
    SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
           eb.event_data, eb.metadata, es.scope, es.version, es.created_at
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND es.version <= p_through_version
    ON CONFLICT (event_id) DO NOTHING;
  END IF;

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows this
  -- close is about to truncate (263, #1891: a digest-exempt type is not fold-relevant, so a prefix of only
  -- audit records refolds nothing and leaves the origin generation alone); the sealed epochs covering those ranges refold AFTER the delete
  -- (they must fold the post-close world), and the ranges are unknowable once the rows are gone.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = p_stream_id
      AND es.version <= p_through_version
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)  -- 263 (#1891)
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Migration 087 (A1c): subtract the about-to-be-truncated rows from wh_stream_digests BEFORE
  -- the delete (the rows must still exist to be read). Only rows that ever
  -- contributed to a digest (non-ephemeral, not at-most-once, not a digest-exempt type, 263) are XOR-ed back out; XOR is
  -- self-inverse, so folding the same hashes again removes them. A missing bucket is a no-op;
  -- the periodic full-sweep verification heals any residual drift.
  UPDATE __SCHEMA__.wh_stream_digests d
  SET digest_lo = d.digest_lo # s.digest_lo,
      digest_hi = d.digest_hi # s.digest_hi,
      event_count = d.event_count - s.event_count,
      updated_at = NOW()
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS origin_service_id,
           COALESCE(es.scope ->> 't', '') AS scope_tenant,
           es.event_type,
           es.stream_id,
           bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
           bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
           COUNT(*)::int AS event_count
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = p_stream_id
      AND es.version <= p_through_version
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)  -- 263 (#1891)
    GROUP BY 1, 2, 3, 4
  ) s
  WHERE d.origin_service_id = s.origin_service_id
    AND d.scope_tenant = s.scope_tenant
    AND d.event_type = s.event_type
    AND d.stream_id = s.stream_id;

  -- Drop buckets the subtraction emptied (a fully-truncated (type, stream) bucket). Separate
  -- statement so the UPDATE above is visible.
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.stream_id = p_stream_id AND d.event_count <= 0;

  -- Migration 212 (#1486): snapshots a rewind could restore and then not replay. Kept: a snapshot at or
  -- above the close point, and, for a perspective whose cursor is inside the range, one at or after that
  -- cursor's event (the perspective applied nothing after it). Removed: every other versioned snapshot below
  -- that floor, and a versionless snapshot on a truncated event. Runs before the re-anchor below, which moves
  -- the cursor this floor is read from.
  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn
  WHERE sn.stream_id = p_stream_id
    AND (
      (sn.snapshot_version IS NULL
       AND EXISTS (
         SELECT 1 FROM __SCHEMA__.wh_event_store es
         WHERE es.event_id = sn.snapshot_event_id
           AND es.stream_id = p_stream_id
           AND es.version <= p_through_version))
      OR sn.snapshot_version < COALESCE(
        (SELECT es.version
         FROM __SCHEMA__.wh_perspective_cursors c
         JOIN __SCHEMA__.wh_event_store es ON es.event_id = c.last_event_id
         WHERE c.stream_id = p_stream_id
           AND c.perspective_name = sn.perspective_name
           AND es.stream_id = p_stream_id
           AND es.version <= p_through_version),
        p_through_version));

  -- Migration 212 (#1486): re-anchor the cursors inside the range onto the boundary event. The cursor guard
  -- above proved none of their perspectives still owes it. fk_perspective_cursors_event is ON DELETE
  -- RESTRICT on the EF Core schema, so this has to happen before the events go.
  UPDATE __SCHEMA__.wh_perspective_cursors c
  SET last_event_id = v_boundary
  FROM __SCHEMA__.wh_event_store es
  WHERE c.stream_id = p_stream_id
    AND es.event_id = c.last_event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  -- Migration 212 (#1486): finished work rows and lapsed destruction holds of the truncated events. The
  -- gates above proved no unprocessed work row and no active hold remains in the range.
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  USING __SCHEMA__.wh_event_store es
  WHERE pe.event_id = es.event_id
    AND pe.stream_id = p_stream_id
    AND pe.processed_at IS NOT NULL
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_destruction_hold h
  USING __SCHEMA__.wh_event_store es
  WHERE h.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  -- Truncate the detail at/below the close point: bodies first, then pointers. Cursors were re-anchored
  -- above, so fk_perspective_cursors_event (EF Core schema) no longer references anything removed here.
  DELETE FROM __SCHEMA__.wh_event_body eb
  USING __SCHEMA__.wh_event_store es
  WHERE eb.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version <= p_through_version;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;

  -- Migration 093 (#80-F): refold the sealed epochs the truncation touched (post-delete world),
  -- then bump the generation ONCE; consumers reset their seals and re-verify instead of
  -- alarming on deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT 'truncated'::TEXT, v_deleted;  -- 240 (#1731): was 'closed'
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_truncate_stream_prefix(UUID, BIGINT, BOOLEAN) IS
'Gated prefix truncate of one stream through a version (close_stream until 240, #1731). Refuses with debug_skipped, blocked (unprocessed work in the range), no_carry_forward (no event above the truncate point), held (an active destruction hold in the range) or cursor_behind (a cursor in the range whose perspective still owes the boundary event). On truncated it re-anchors cursors in the range onto the boundary event, removes snapshots a rewind could no longer replay from, removes processed work rows and lapsed holds of the truncated events, and keeps their deduplication entries (#1486). At-most-once events (flags & 32), never folded, are never subtracted (214), nor are the digest-exempt types (the audit records, 216), so a prefix of only those refolds nothing and leaves the origin generation alone (263, #1891).';
