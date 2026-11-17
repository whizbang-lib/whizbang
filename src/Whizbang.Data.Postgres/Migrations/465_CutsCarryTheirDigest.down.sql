-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 465_CutsCarryTheirDigest.sql (#2363): every function goes back to its last word before 465 (453 for the
-- epoch fold, the type answer, the heal statement and the cut functions; 454 for store_inbox_messages), and the helpers,
-- the removed rows, the consumer seals and the carried columns go. A digest that carried a cut is then one the heal
-- rewrites from the live events, as it did before 465.

-- ── _wh_epoch_buckets: last word 336, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#what-is-digested</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_epoch_buckets(
  p_lane  UUID,
  p_epoch BIGINT,
  p_width BIGINT
) RETURNS TABLE (b_tenant TEXT, b_type TEXT, b_lo BIGINT, b_hi BIGINT, b_cnt INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_from  BIGINT := p_epoch * p_width;
  v_to    BIGINT := (p_epoch + 1) * p_width;   -- exclusive
BEGIN
  IF p_lane = c_zero THEN
    RETURN QUERY
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type::text,
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)),
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)),
           COUNT(*)::int
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id IS NULL
      AND es.commit_sequence >= v_from AND es.commit_sequence < v_to
      AND __DIGEST_ELIGIBLE__(es)  -- 216 (#1492)
    GROUP BY 1, 2;
  ELSE
    RETURN QUERY
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type::text,
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)),
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)),
           COUNT(*)::int
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = p_lane
      AND es.origin_commit_sequence >= v_from AND es.origin_commit_sequence < v_to
      AND __DIGEST_ELIGIBLE__(es)  -- 216 (#1492)
    GROUP BY 1, 2;
  END IF;
END;
$$;

-- ── compute_type_digests_epoch: last word 336, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#unstamped-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:LocalLane_AnUnstampedRow_IsLeftOutOfTheOpenWindowUntilItIsStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:ReceivedLane_ARowWithNoOriginSequence_StillFoldsInTheOpenWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:EpochComposition_EqualsTheFullFold_AcrossClosedAndOpenRangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch(
  p_origin_lane    UUID,          -- NULL = the local lane (this service's own emissions)
  p_event_types    TEXT[],        -- NULL = all types
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_lane      UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier  BIGINT := -1;
  v_width     BIGINT;
  v_open_from BIGINT := 0;
  v_types     TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_frontier := -1;      -- nothing sealed: the whole lane folds live below
    v_open_from := 0;
  ELSE
    v_open_from := (v_frontier + 1) * v_width;
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id <= v_frontier
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  open_fold AS (
    -- The live remainder. #1791: in the local lane only stamped rows fold. A row the stamper has not yet
    -- sequenced is not settled here: the windowed answer, integrity_settled_max and the stamp fence all wait for its
    -- sequence, and once it has one it folds where that sequence puts it, so it never falls through the crack.
    -- A received row keeps the NULL-sequence branch: it can lack the origin's sequence for good (published
    -- before its stamp, or received before 087), and the open window is the only place it can ever fold.
    SELECT COALESCE(es.scope ->> 't', '') AS b_tenant, es.event_type AS b_type,
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)) AS lo,
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
      AND (CASE WHEN p_origin_lane IS NULL
                THEN es.commit_sequence IS NOT NULL AND es.commit_sequence >= v_open_from
                ELSE es.origin_commit_sequence IS NULL OR es.origin_commit_sequence >= v_open_from
           END)
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND __DIGEST_ELIGIBLE__(es)  -- 216 (#1492)
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM open_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

-- ── _wh_heal_stream_digests_statement: last word 446, copied whole ──
-- The heal statement for a named batch (p_named) or the whole store, with $1 the streams, $2 the settle window and
-- $3 whether to fill id lanes never computed. One text, planned and run as written.
-- <docs>resilience/stream-integrity#the-heal-is-linear-in-its-batch</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestHealPlanSqlTests.cs:TheHealStatement_NeverRescansASetPerRow_ForANamedBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestHealPlanSqlTests.cs:TheHealStatement_NeverRescansASetPerRow_ForTheWholeStoreAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_heal_stream_digests_statement(p_named BOOLEAN)
RETURNS TEXT AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- The stream filter, as an index condition when streams are named; the whole store for the sweep.
  v_events TEXT := CASE WHEN p_named THEN 'es.stream_id = ANY($1)' ELSE 'TRUE' END;
  v_digests TEXT := CASE WHEN p_named THEN 'd.stream_id = ANY($1)' ELSE 'TRUE' END;
BEGIN
  RETURN format($q$
    WITH recomputed AS (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS o, COALESCE(es.scope ->> 't', '') AS t,
             es.event_type AS ty, es.stream_id AS s,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 2)) AS ilo,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 3)) AS ihi,
             COUNT(*)::int AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE %1$s
        AND __DIGEST_ELIGIBLE__(es)
        AND es.created_at < NOW() - $2
      GROUP BY 1, 2, 3, 4
    ),
    stored AS (
      SELECT d.origin_service_id AS o, d.scope_tenant AS t, d.event_type AS ty, d.stream_id AS s,
             d.digest_lo, d.digest_hi, d.event_count, d.id_lo, d.id_hi
      FROM __SCHEMA__.wh_stream_digests d
      WHERE %2$s AND d.updated_at < NOW() - $2
    ),
    -- One comparison of the two sets, as a FULL JOIN: PostgreSQL runs a full join only as a hash or merge join, never
    -- as a nested loop, so its cost stays linear whatever the planner believes about either set (#2337). A bucket on
    -- both sides is compared; one only stored has no events behind it; one only recomputed has no bucket yet.
    compared AS (
      SELECT st.o, st.t, st.ty, st.s, st.digest_lo, st.digest_hi, st.event_count, r.lo, r.hi, r.ilo, r.ihi, r.n,
             r.s IS NULL AS stored_only,
             (st.s IS NOT NULL AND r.s IS NOT NULL) AS on_both,
             (st.digest_lo <> r.lo OR st.digest_hi <> r.hi OR st.event_count <> r.n
              OR st.id_lo <> r.ilo OR st.id_hi <> r.ihi) AS drifted,
             (st.id_lo IS NULL OR st.id_hi IS NULL) AS unfilled
      FROM stored st
      FULL JOIN recomputed r ON r.o = st.o AND r.t = st.t AND r.ty = st.ty AND r.s = st.s
    ),
    rewrite AS (
      SELECT c.*, CASE WHEN c.drifted THEN 'drift_updated' ELSE 'filled' END AS why
      FROM compared c
      WHERE c.on_both AND (c.drifted OR ($3 AND c.unfilled))
    ),
    updated AS (
      UPDATE __SCHEMA__.wh_stream_digests d
      SET digest_lo = w.lo, digest_hi = w.hi, id_lo = w.ilo, id_hi = w.ihi, event_count = w.n, updated_at = NOW()
      FROM rewrite w
      WHERE d.origin_service_id = w.o AND d.scope_tenant = w.t AND d.event_type = w.ty AND d.stream_id = w.s
      RETURNING 1
    ),
    removed AS (
      DELETE FROM __SCHEMA__.wh_stream_digests d
      USING compared c
      WHERE c.stored_only
        AND d.origin_service_id = c.o AND d.scope_tenant = c.t AND d.event_type = c.ty AND d.stream_id = c.s
      RETURNING d.origin_service_id, d.scope_tenant, d.event_type, d.stream_id, d.digest_lo, d.digest_hi, d.event_count
    ),
    added AS (
      INSERT INTO __SCHEMA__.wh_stream_digests
        (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, id_lo, id_hi)
      SELECT r.o, r.t, r.ty, r.s, r.lo, r.hi, r.n, r.ilo, r.ihi
      FROM recomputed r
      WHERE NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_stream_digests d
        WHERE d.origin_service_id = r.o AND d.scope_tenant = r.t AND d.event_type = r.ty AND d.stream_id = r.s)
      ORDER BY 1, 2, 3, 4
      ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO NOTHING
      RETURNING origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count
    )
    SELECT w.why, w.o, w.t, w.ty, w.s, w.digest_lo, w.digest_hi, w.event_count, w.lo, w.hi, w.n
    FROM rewrite w
    UNION ALL
    SELECT 'drift_removed', x.origin_service_id, x.scope_tenant, x.event_type, x.stream_id,
           x.digest_lo, x.digest_hi, x.event_count, NULL::bigint, NULL::bigint, NULL::int
    FROM removed x
    UNION ALL
    SELECT 'drift_added', a.origin_service_id, a.scope_tenant, a.event_type, a.stream_id,
           NULL::bigint, NULL::bigint, NULL::int, a.digest_lo, a.digest_hi, a.event_count
    FROM added a
    $q$, v_events, v_digests);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_heal_stream_digests_statement(BOOLEAN) IS
  '446 (#2337): the statement _wh_heal_stream_digests runs, for a named batch or the whole store: $1 streams, $2 settle '
  'window, $3 fill id lanes. Separate so it can be planned on its own.';

-- ── wh_truncate_stream_prefix: last word 453, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Truncate_ThroughTheFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:RebuildBlueGreen_AfterAPrefixTruncate_DatesTheRowAtTheStreamsStartAsync</tests>
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
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:TruncateStreamPrefix_SubtractsEveryLaneOfTheTruncatedEventsAsync</tests>
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
      AND __DIGEST_ELIGIBLE__(es)  -- 263 (#1891)
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Migration 087 (A1c): subtract the about-to-be-truncated rows from wh_stream_digests BEFORE
  -- the delete (the rows must still exist to be read). Only rows that ever
  -- contributed to a digest (non-ephemeral, not at-most-once, not a digest-exempt type, 263) are XOR-ed back out; XOR is
  -- self-inverse, so folding the same hashes again removes them. A missing bucket is a no-op;
  -- the periodic full-sweep verification heals any residual drift.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
    array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND __DIGEST_ELIGIBLE__(es);  -- 263 (#1891)

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

  -- 358 (#2152): a prefix holding the stream's first event (version 0 or 1) leaves that event's business time on the
  -- stream's ledger row, so a rebuild that writes the stream's row afresh still dates it at the stream's start. Written
  -- once, never over a value already recorded.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET first_event_at = f.business_time
  FROM (
    SELECT COALESCE(__SCHEMA__._wh_event_business_time(eb.metadata), es.created_at) AS business_time
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND es.version <= LEAST(p_through_version, 1)
    ORDER BY es.version
    LIMIT 1
  ) f
  WHERE l.stream_id = p_stream_id AND l.first_event_at IS NULL;

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
'Gated prefix truncate of one stream through a version (close_stream until 240, #1731). Refuses with debug_skipped, blocked (unprocessed work in the range), no_carry_forward (no event above the truncate point), held (an active destruction hold in the range) or cursor_behind (a cursor in the range whose perspective still owes the boundary event). On truncated it re-anchors cursors in the range onto the boundary event, removes snapshots a rewind could no longer replay from, removes processed work rows and lapsed holds of the truncated events, and keeps their deduplication entries (#1486). At-most-once events (flags & 32), never folded, are never subtracted (214), nor are the digest-exempt types (the audit records, 216), so a prefix of only those refolds nothing and leaves the origin generation alone (263, #1891). 358: records the business time of the stream''s first event, when the prefix holds it, on the stream''s ledger row (first_event_at, #2152).';

-- ── wh_archive_stream: last word 299, copied whole ──
-- <docs>fundamentals/events/archive-store#writing-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_AFailureBetweenCopyAndTruncate_LeavesTheStreamFullyHotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ARefusedPrefix_KeepsTheTruncatesStatus_AndCopiesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_LeavesATombstone_AndBumpsTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_UnprocessedWorkOrAnActiveHold_RefusesAndMovesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_NothingHot_ReportsNothingToArchive_AndBumpsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_DebugMode_KeepsEverythingHotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_WithNoLedgerRowYet_BuildsTheTombstoneFromTheEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WaitsForTheStreamsAppendLock_SoNothingIsAppendedWhileItMovesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Prefix_ThroughAVersionBelowOne_IsRefused_ThroughOneIsArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Prefix_ReachingAPerspectivesNewestSnapshot_IsRefused_StoppingBelowItIsArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Prefix_MarksTheSnapshotsAtOrBelowItsCut_AndLeavesTheNewestUnmarkedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:WholeStream_MarksEverySnapshotWithItsLastVersionAsync</tests>
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Drop_RemovesTheRow_ItsParkedCursors_AndAllButTheLatestSnapshot_AndLeavesAnotherModelAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_MovesTheRowBesideTheEvents_AndARestoreBringsItBackExactlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:APrefixArchive_LeavesTheRowWhereItIs_WhateverThePolicyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_archive_stream(
  p_stream_id UUID, p_through_version BIGINT, p_locator TEXT)
RETURNS TABLE(archive_status TEXT, events_archived BIGINT, last_version BIGINT, origin_generation BIGINT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_last BIGINT;
  v_status TEXT;
  v_count BIGINT := 0;
  v_blocked BIGINT;
  v_held BIGINT;
  v_generation BIGINT;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_type TEXT;
  v_tenant TEXT;
  v_first_at TIMESTAMPTZ;
  v_bucket TIMESTAMPTZ;
  v_bytes BIGINT;
  v_kept_bytes BIGINT;
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3), never in a sealed epoch
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), never in a sealed epoch (214)
  c_flags_off_the_ledger CONSTANT INTEGER := 24;  -- ephemeral (8) or a compaction origin (16): no ledger row (260)
BEGIN
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  -- 271 (#1738): a prefix always removes at least version 1, so an archived stream always shows a gap at its start
  -- (a stream's first version is 0 or 1), and a read from the start sees it without asking the archive.
  IF p_through_version IS NOT NULL AND p_through_version < 1 THEN
    RETURN QUERY SELECT 'not_past_first_version'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- debug_mode retains forensic history; an archive removes hot detail, so it is skipped like the truncate.
  SELECT (s.setting_value = 'true') INTO v_debug FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode';
  IF COALESCE(v_debug, FALSE) THEN
    RETURN QUERY SELECT 'debug_skipped'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- The emit chains' and the direct append's per-stream key: nothing is appended to the stream while it moves.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  SELECT MAX(es.version) INTO v_last FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;
  IF v_last IS NULL
     OR (p_through_version IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_event_store es
       WHERE es.stream_id = p_stream_id AND es.version <= p_through_version)) THEN
    RETURN QUERY SELECT 'nothing_to_archive'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- 271 (#1738): a prefix never takes the newest snapshot any perspective holds for the stream, so the snapshot's event
  -- and everything after it stay hot and a rewind to it never reads the archive. Refused rather than shortened: the
  -- caller gets exactly the archive it asked for or none, and chooses a cut below the snapshot itself. A snapshot that
  -- records no version cannot be placed in the stream and is not counted.
  IF p_through_version IS NOT NULL AND EXISTS (
    SELECT 1
    FROM (
      SELECT MAX(sn.snapshot_version) AS newest
      FROM __SCHEMA__.wh_perspective_snapshots sn
      WHERE sn.stream_id = p_stream_id
        AND sn.snapshot_version IS NOT NULL
      GROUP BY sn.perspective_name
    ) latest
    WHERE latest.newest <= p_through_version) THEN
    RETURN QUERY SELECT 'snapshot_in_range'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
    RETURN;
  END IF;

  -- The stream's ledger identity by 260's rules, read while every event is still hot, for a stream the ledger has no row
  -- for yet (the backfill has not reached it): the first event's type, tenant and time, the UTC hour of the newest event.
  -- The first event that counts for the ledger, or else the first event at all, so a stream of only ephemeral events
  -- still gets a tombstone that refuses reuse of its versions.
  SELECT es.aggregate_type, es.scope ->> 't', es.created_at INTO v_type, v_tenant, v_first_at
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
  ORDER BY (COALESCE(es.flags, 0) & c_flags_off_the_ledger <> 0), es.version
  LIMIT 1;
  SELECT date_trunc('hour', MAX(es.created_at), 'UTC') INTO v_bucket
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;

  -- The body bytes the ledger counts (events on the ledger, as stored), split into what this archive moves and what
  -- stays hot, so approx_bytes keeps measuring the hot store.
  SELECT COALESCE(SUM(b.sz) FILTER (WHERE p_through_version IS NULL OR b.version <= p_through_version), 0),
         COALESCE(SUM(b.sz) FILTER (WHERE p_through_version IS NOT NULL AND b.version > p_through_version), 0)
  INTO v_bytes, v_kept_bytes
  FROM (
    SELECT es.version, COALESCE(pg_column_size(eb.event_data), 0) + COALESCE(pg_column_size(eb.metadata), 0) AS sz
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
  ) b;

  IF p_through_version IS NOT NULL THEN
    -- A prefix: copy, then the gated truncate. A refusal raises WHA00 so the subtransaction undoes the copy.
    BEGIN
      INSERT INTO __SCHEMA__.wh_event_archive
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at,
         flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order)
      SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
             eb.event_data, eb.metadata, es.scope, es.version, es.created_at,
             es.flags, es.commit_sequence, es.origin_service_id, es.origin_commit_sequence, es.insert_order
      FROM __SCHEMA__.wh_event_store es
      LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.stream_id = p_stream_id AND es.version <= p_through_version
      ON CONFLICT (event_id) DO NOTHING;

      SELECT t.truncate_status, t.events_truncated INTO v_status, v_count
      FROM __SCHEMA__.wh_truncate_stream_prefix(p_stream_id, p_through_version, FALSE) t;
      IF v_status <> 'truncated' THEN
        RAISE EXCEPTION USING ERRCODE = 'WHA00', MESSAGE = v_status;
      END IF;
    EXCEPTION WHEN SQLSTATE 'WHA00' THEN
      RETURN QUERY SELECT v_status, 0::BIGINT, NULL::BIGINT, v_generation;
      RETURN;
    END;
  ELSE
    -- The whole stream. Consumption gate: no perspective may still owe any of its events.
    SELECT count(*) INTO v_blocked
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
    WHERE es.stream_id = p_stream_id AND pe.processed_at IS NULL;
    IF v_blocked > 0 THEN
      RETURN QUERY SELECT 'blocked'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
      RETURN;
    END IF;

    -- Hold guard: an active destruction hold is a recorded decision to keep that event hot.
    SELECT count(*) INTO v_held
    FROM __SCHEMA__.wh_event_destruction_hold h
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
    WHERE es.stream_id = p_stream_id AND h.hold_until > NOW();
    IF v_held > 0 THEN
      RETURN QUERY SELECT 'held'::TEXT, 0::BIGINT, NULL::BIGINT, v_generation;
      RETURN;
    END IF;

    INSERT INTO __SCHEMA__.wh_event_archive
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at,
       flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order)
    SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
           eb.event_data, eb.metadata, es.scope, es.version, es.created_at,
           es.flags, es.commit_sequence, es.origin_service_id, es.origin_commit_sequence, es.insert_order
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id
    ON CONFLICT (event_id) DO NOTHING;

    -- Park the cursors: nothing hot is left for them to point at, and a restore puts them back as they were.
    INSERT INTO __SCHEMA__.wh_event_archive_cursors (stream_id, perspective_name, cursor_row)
    SELECT c.stream_id, c.perspective_name, to_jsonb(c)
    FROM __SCHEMA__.wh_perspective_cursors c
    WHERE c.stream_id = p_stream_id
    ON CONFLICT (stream_id, perspective_name) DO UPDATE SET cursor_row = EXCLUDED.cursor_row, archived_at = NOW();
    DELETE FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = p_stream_id;

    -- The sealed epochs the stream's folded rows sit in, per lane, captured while the rows exist (as 240 does).
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = p_stream_id
        AND __DIGEST_ELIGIBLE__(es)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;

    -- Every event of the stream leaves, so every digest bucket of the stream is empty: remove them outright rather
    -- than subtract, which would have to repeat each fold predicate exactly (a restore folds them back).
    DELETE FROM __SCHEMA__.wh_stream_digests d WHERE d.stream_id = p_stream_id;

    -- Finished work rows and lapsed holds of the stream's events; the guards above proved nothing else is left.
    DELETE FROM __SCHEMA__.wh_perspective_events pe
    WHERE pe.stream_id = p_stream_id AND pe.processed_at IS NOT NULL;

    DELETE FROM __SCHEMA__.wh_event_destruction_hold h
    USING __SCHEMA__.wh_event_store es
    WHERE h.event_id = es.event_id AND es.stream_id = p_stream_id;

    DELETE FROM __SCHEMA__.wh_event_body eb
    USING __SCHEMA__.wh_event_store es
    WHERE eb.event_id = es.event_id AND es.stream_id = p_stream_id;

    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;
    GET DIAGNOSTICS v_count = ROW_COUNT;

    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
    END IF;
  END IF;

  -- Every archive moves history, so every archive bumps the generation (a prefix whose truncate already bumped it
  -- moves it twice; a consumer resets once per change it sees, so the second is harmless).
  PERFORM __SCHEMA__._wh_bump_origin_generation();
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  IF p_through_version IS NULL THEN
    -- The tombstone: the stream is archived through v_last, and no append may reuse a version at or below it. Nothing
    -- of it is hot, so it counts no bytes.
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
       archive_locator, origin_generation)
    VALUES (p_stream_id, v_type, v_tenant, v_first_at, v_bucket, v_last, 0, 'Archived', p_locator, v_generation)
    ON CONFLICT (stream_id) DO UPDATE SET
      state = 'Archived',
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = 0,
      archive_locator = EXCLUDED.archive_locator,
      origin_generation = EXCLUDED.origin_generation;

    -- 299 (#1739): the stream's rows of the perspectives whose policy is Drop or Archive leave with it, after the
    -- tombstone, which an archived row references.
    PERFORM __SCHEMA__._wh_dispose_archived_stream_rows(p_stream_id);
  ELSE
    -- A prefix: the stream stays as it was; its row records where the prefix went and stops counting its bytes.
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
       archive_locator, origin_generation)
    VALUES (p_stream_id, v_type, v_tenant, v_first_at, v_bucket, v_last, v_kept_bytes, 'Active', p_locator, v_generation)
    ON CONFLICT (stream_id) DO UPDATE SET
      approx_bytes = GREATEST(l.approx_bytes - v_bytes, 0),
      archive_locator = EXCLUDED.archive_locator,
      origin_generation = EXCLUDED.origin_generation;
  END IF;

  -- 271 (#1738): the snapshots the archive covers record how far it reaches, so a rewind from one of them, or a full
  -- rebuild, reads through the archive: a prefix marks those at or below its cut, a whole stream marks them all.
  UPDATE __SCHEMA__.wh_perspective_snapshots sn
  SET archived_through_version = GREATEST(COALESCE(sn.archived_through_version, 0), COALESCE(p_through_version, v_last))
  WHERE sn.stream_id = p_stream_id
    AND (p_through_version IS NULL OR sn.snapshot_version <= p_through_version);

  RETURN QUERY SELECT 'archived'::TEXT, v_count, CASE WHEN p_through_version IS NULL THEN v_last END, v_generation;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_archive_stream(UUID, BIGINT, TEXT) IS
'Archives a stream into wh_event_archive and removes it from the hot store in one transaction (#1736). A prefix (p_through_version given) runs wh_truncate_stream_prefix with all its guards and returns its refusal unchanged; the whole stream (NULL) refuses with blocked or held, parks its cursors in wh_event_archive_cursors and leaves a tombstone (wh_stream_lifecycle state Archived, last_version, archive_locator). Every archive bumps the origin generation. Returns nothing_to_archive when the batch names no hot event, debug_skipped under debug_mode. 271: refuses a prefix through a version below 1 (not_past_first_version) and one that reaches the newest snapshot any perspective holds for the stream (snapshot_in_range), and marks the snapshots an archive covers with how far it reaches (archived_through_version) (#1738). 299: a whole-stream archive drops or archives the stream''s perspective rows by each perspective''s policy (_wh_dispose_archived_stream_rows, #1739).';

-- ── wh_restore_archived_stream: last word 336, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/archive-store#restoring-an-archive</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_PutsTheStreamBackExactly_CursorsDigestsAndPositionsIncludedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_APrefix_UnderItsSurvivingTailAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_IsIdempotent_TheSecondCallFindsNothingArchivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_AHotEventAtAnArchivedVersion_IsAVersionConflict_AndRestoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_ARowArchivedBeforeTheStoreKeptPositions_IsStoredUnflaggedForTheStamperAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_AnArchivedStream_StaysArchived_AndARestoreReturnsItSealedAsync</tests>
-- <docs>fundamentals/events/archive-store#restore-requests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:Restore_MarksTheRequestRestored_AndMakesItsDeadLettersDueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:Restore_ASecondRestoreOfAStreamAlreadyBack_CompletesItsRequestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRulesSqlTests.cs:Restore_ClearsEverySnapshotsArchiveMarkAsync</tests>
-- <docs>fundamentals/events/archive-store#perspective-rows</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs:Archive_MovesTheRowBesideTheEvents_AndARestoreBringsItBackExactlyAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRowsTests.cs:Archive_OnADapperMigratedDatabase_MovesTheRow_AndARestoreBringsItBackAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restore_archived_stream(p_stream_id UUID)
RETURNS TABLE(restore_status TEXT, events_restored BIGINT, origin_generation BIGINT) AS $$
DECLARE
  v_generation BIGINT;
  v_ids UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_bytes BIGINT;
  v_hot_bytes BIGINT;
  v_type TEXT;
  v_tenant TEXT;
  v_first_at TIMESTAMPTZ;
  v_bucket TIMESTAMPTZ;
  v_last BIGINT;
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;
  c_flags_off_the_ledger CONSTANT INTEGER := 24;  -- ephemeral (8) or a compaction origin (16): no ledger row (260)
BEGIN
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id) THEN
    -- 271 (#1738): a stream that is not archived any more (a concurrent restore put it back first) completes its restore
    -- request too, so a second restore of one stream ends the same way as the first. A tombstone whose archive was
    -- deleted is still Archived: nothing completes, and the caller reports the request Failed.
    IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = p_stream_id AND l.state = 'Archived') THEN
      PERFORM __SCHEMA__._wh_complete_archive_restore(p_stream_id);
    END IF;
    RETURN QUERY SELECT 'nothing_archived'::TEXT, 0::BIGINT, v_generation;
    RETURN;
  END IF;

  -- Two events at one version would break every reader of the stream: refuse and move nothing.
  IF EXISTS (
    SELECT 1
    FROM __SCHEMA__.wh_event_archive a
    JOIN __SCHEMA__.wh_event_store es ON es.stream_id = a.stream_id AND es.version = a.version AND es.event_id <> a.event_id
    WHERE a.stream_id = p_stream_id) THEN
    RETURN QUERY SELECT 'version_conflict'::TEXT, 0::BIGINT, v_generation;
    RETURN;
  END IF;

  -- Active again before the events go back. 266 (#1733): Sealed instead
  -- when the stream was sealed, so a restore never reopens a stream that ended.
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET state = CASE WHEN l.state = 'Archived' THEN (CASE WHEN l.sealed_at IS NOT NULL THEN 'Sealed' ELSE 'Active' END)
                   ELSE l.state END,
      archive_locator = NULL
  WHERE l.stream_id = p_stream_id;

  -- Each event at its original version and position: a restored row with its commit sequence is not stamped again,
  -- so it is not read as new by anything that follows the lanes.
  WITH restored AS (
    INSERT INTO __SCHEMA__.wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at,
       flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order)
    SELECT a.event_id, a.stream_id, a.aggregate_id, a.aggregate_type, a.event_type, a.scope, a.version,
           COALESCE(a.created_at, a.archived_at), COALESCE(a.flags, 0), a.commit_sequence, a.origin_service_id,
           a.origin_commit_sequence, COALESCE(a.insert_order, nextval('__SCHEMA__.wh_event_insert_order_seq'))
    FROM __SCHEMA__.wh_event_archive a
    WHERE a.stream_id = p_stream_id
    ORDER BY a.version
    ON CONFLICT DO NOTHING
    RETURNING event_id)
  SELECT COALESCE(array_agg(r.event_id), ARRAY[]::UUID[]) INTO v_ids FROM restored r;

  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  SELECT a.event_id, a.event_data, a.metadata
  FROM __SCHEMA__.wh_event_archive a
  WHERE a.stream_id = p_stream_id AND a.event_id = ANY(v_ids)
    AND a.event_data IS NOT NULL AND a.metadata IS NOT NULL
  ON CONFLICT DO NOTHING;

  -- Fold the restored rows back in with the chain's own predicates.
  PERFORM __SCHEMA__._wh_fold_stream_digests(
    array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
    array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'fold', NOW())
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_ids)
    AND __DIGEST_ELIGIBLE__(es);

  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_ids)
      AND __DIGEST_ELIGIBLE__(es)
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
  END IF;

  -- The parked cursors return as they were; a cursor a perspective has made since is kept.
  INSERT INTO __SCHEMA__.wh_perspective_cursors
  SELECT (jsonb_populate_record(NULL::__SCHEMA__.wh_perspective_cursors, p.cursor_row)).*
  FROM __SCHEMA__.wh_event_archive_cursors p
  WHERE p.stream_id = p_stream_id
  ON CONFLICT DO NOTHING;

  DELETE FROM __SCHEMA__.wh_event_archive_cursors p WHERE p.stream_id = p_stream_id;
  -- 299 (#1739): the perspective rows the archive moved out of their tables come back with the events.
  PERFORM __SCHEMA__._wh_restore_archived_stream_rows(p_stream_id);
  DELETE FROM __SCHEMA__.wh_event_archive a WHERE a.stream_id = p_stream_id;

  PERFORM __SCHEMA__._wh_bump_origin_generation();
  SELECT (s.setting_value)::bigint INTO v_generation
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation';

  -- The ledger row ends Active and counts the bytes that came back. A stream with no row gets one, by 260's rules.
  SELECT COALESCE(SUM(b.sz) FILTER (WHERE b.restored), 0), COALESCE(SUM(b.sz), 0) INTO v_bytes, v_hot_bytes
  FROM (
    SELECT es.event_id = ANY(v_ids) AS restored,
           COALESCE(pg_column_size(eb.event_data), 0) + COALESCE(pg_column_size(eb.metadata), 0) AS sz
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
  ) b;
  SELECT es.aggregate_type, es.scope ->> 't', es.created_at INTO v_type, v_tenant, v_first_at
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id
  ORDER BY (COALESCE(es.flags, 0) & c_flags_off_the_ledger <> 0), es.version
  LIMIT 1;
  SELECT MAX(es.version), date_trunc('hour', MAX(es.created_at), 'UTC') INTO v_last, v_bucket
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id;

  INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
    (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
     archive_locator, origin_generation)
  VALUES (p_stream_id, v_type, v_tenant, v_first_at, v_bucket, v_last, v_hot_bytes, 'Active', NULL, v_generation)
  ON CONFLICT (stream_id) DO UPDATE SET
    state = CASE WHEN l.state = 'Archived' THEN (CASE WHEN l.sealed_at IS NOT NULL THEN 'Sealed' ELSE 'Active' END)
                 ELSE l.state END,
    last_version = GREATEST(l.last_version, EXCLUDED.last_version),
    last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
    approx_bytes = l.approx_bytes + v_bytes,
    archive_locator = NULL,
    origin_generation = EXCLUDED.origin_generation;

  -- 271 (#1738): whoever asked for this restore, the stream's restore request is Restored, its AwaitingRestore dead
  -- letters are due for the recovery worker, and its snapshots no longer read through an archive.
  PERFORM __SCHEMA__._wh_complete_archive_restore(p_stream_id);

  RETURN QUERY SELECT 'restored'::TEXT, cardinality(v_ids)::BIGINT, v_generation;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_restore_archived_stream(UUID) IS
'Puts a stream''s archived events back into the hot store at their original versions and commit positions, refolds their digests and sealed epochs, returns parked cursors, marks the stream Active (266: Sealed when it was sealed, #1733), deletes the archived copy and bumps the origin generation (#1736). Refuses with version_conflict when a hot event holds an archived version; nothing_archived when there is nothing to restore. 271: completes the stream''s restore request, makes its AwaitingRestore dead letters due and clears its snapshots'' archive marks, on a restore and on a nothing_archived of a stream no longer archived (#1738). 299: returns the perspective rows the archive moved out of their tables (#1739).';

-- ── wh_purge_streams: last word 262, copied whole ──
-- <docs>operations/infrastructure/purging-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:Purge_RemovesEveryRowOfTheStreams_AndLeavesOthersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:DryRun_CountsWhatWouldGo_AndChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Purge_AnArchivedStream_TakesItsArchiveParkedCursorsAndTombstoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_purge_streams(
  p_stream_ids UUID[],
  p_dry_run BOOLEAN,
  p_purge_id UUID,
  p_batch_index INTEGER,
  p_requested_by TEXT,
  p_reason TEXT
) RETURNS TABLE(purged_table TEXT, purged_rows BIGINT) AS $$
DECLARE
  v_ids UUID[];
  v_event_ids UUID[];
  v_message_ids UUID[];
  v_schema TEXT;
  v_table TEXT;
  v_n BIGINT;
  v_counts JSONB := '{}'::jsonb;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- The empty stream id is "no stream": purging it would reach every streamless row.
  IF __EMPTY_UUID__::uuid = ANY(p_stream_ids) THEN
    RAISE EXCEPTION 'wh_purge_streams: the empty stream id cannot be purged';
  END IF;

  SELECT COALESCE(array_agg(DISTINCT s), ARRAY[]::uuid[]) INTO v_ids FROM unnest(p_stream_ids) s;

  -- Ids the indirect tables are keyed by, read before anything is deleted.
  SELECT COALESCE(array_agg(es.event_id), ARRAY[]::uuid[]) INTO v_event_ids
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  SELECT v_event_ids || COALESCE(array_agg(i.message_id), ARRAY[]::uuid[]) INTO v_message_ids
  FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);

  -- Work tables, in the canonical lock order.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_outbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Counted before the inbox delete that removes them by cascade.
  SELECT count(*) INTO v_n
  FROM __SCHEMA__.wh_inbox_state st
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = st.message_id
  WHERE i.stream_id = ANY(v_ids);
  purged_table := 'wh_inbox_state'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_inbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_events'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_active_streams'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Deduplication entries of the stream's messages: its events and its inbox messages.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_message_deduplication'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_receptor_processing'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Before the events: fk_perspective_cursors_event is ON DELETE RESTRICT.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_snapshots'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_applied'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Every perspective's row for the streams. The registry names the tables; one it names that does not
  -- exist yet is skipped. Going through regclass resolves this function's own schema whichever way the
  -- runner substituted it (see 105).
  SELECT n.nspname INTO v_schema
  FROM pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
  WHERE c.oid = '__SCHEMA__.wh_event_store'::regclass;

  FOR v_table IN
    SELECT DISTINCT r.table_name FROM __SCHEMA__.wh_perspective_registry r ORDER BY r.table_name
  LOOP
    CONTINUE WHEN to_regclass(format('%I.%I', v_schema, v_table)) IS NULL;
    IF p_dry_run THEN
      EXECUTE format('SELECT count(*) FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) INTO v_n USING v_ids;
    ELSE
      EXECUTE format('DELETE FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) USING v_ids;
      GET DIAGNOSTICS v_n = ROW_COUNT;
    END IF;
    purged_table := v_table; purged_rows := v_n; RETURN NEXT;
    v_counts := v_counts || jsonb_build_object(purged_table, v_n);
  END LOOP;

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_row_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_row_eviction_journal'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- The sealed epochs covering the fold-relevant events refold AFTER the delete, and their ranges are
  -- unknowable once the rows are gone (the same capture close_stream makes).
  IF NOT p_dry_run THEN
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = ANY(v_ids)
        AND __DIGEST_ELIGIBLE__(es)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;
  END IF;

  -- Every event of the streams goes, so every digest bucket and ledger row of the streams goes with them.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_digests'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_integrity_ledger'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_body'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_destruction_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_lifecycle_completions'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- 262 (#1736): the cursors a whole-stream archive parked for its restore; a purged stream is never restored.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive_cursors ac WHERE ac.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive_cursors ac WHERE ac.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_apply_fold_watermarks'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_store'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- 260 (#1732): the stream's lifecycle row goes with its events, after them, in the order the emit chain writes the
  -- two tables. A purged stream is gone, not archived: no row is left for an end-of-life sweep to act on.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_lifecycle'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Refold what the delete touched, then bump the generation ONCE so consumers reset their seals and
  -- re-verify instead of alarming on a deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  -- A purged stream stays purged: mark it for every perspective, so a later event is skipped, not applied.
  v_n := COALESCE(array_length(v_ids, 1), 0);
  IF NOT p_dry_run THEN
    INSERT INTO __SCHEMA__.wh_stream_purge_markers (stream_id, perspective_name, purged_at, purge_event_id, purge_id)
    SELECT s, '*', NOW(), NULL, p_purge_id FROM unnest(v_ids) s
    ON CONFLICT (stream_id, perspective_name) DO UPDATE
      SET purged_at = EXCLUDED.purged_at, purge_event_id = NULL, purge_id = EXCLUDED.purge_id;

    INSERT INTO __SCHEMA__.wh_stream_purge_audit (purge_id, batch_index, requested_by, reason, stream_ids, row_counts)
    VALUES (p_purge_id, p_batch_index, p_requested_by, p_reason, v_ids, v_counts || jsonb_build_object('wh_stream_purge_markers', v_n))
    ON CONFLICT (purge_id, batch_index) DO NOTHING;
  END IF;
  purged_table := 'wh_stream_purge_markers'; purged_rows := v_n; RETURN NEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_purge_streams IS
'Operator stream purge (#1030): removes every row of the listed streams from this service''s store in the caller''s transaction, marks them purged for every perspective, and audits the batch. p_dry_run counts the same rows and changes nothing. Returns one row per table. 214: the refolded epoch range leaves out at-most-once events (flags & 32), which no epoch holds. 260: and the streams'' wh_stream_lifecycle rows (#1732). 262: and the cursors a whole-stream archive parked (#1736).';

-- ── store_inbox_messages: last word 368, copied whole, with an older copy of a held superseding key settled as satisfied ──
-- <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:ARedeliveredOlderCopy_IsNeverStored_IsRecordedSatisfied_AndNoPerspectiveSeesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:ARedeliveredCopyNewerThanTheHeldOne_IsRestoredAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredSupersedingCopySqlTests.cs:AnOlderCopyArrivingForTheFirstTime_AfterTheNewest_IsNeverStored_AndIsRecordedSatisfiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TwoServiceIntegrityConvergenceTests.cs:AnOlderRedeliveredCopy_IsNeverStoredOrApplied_AndThePairConvergesWithNoFurtherResendAsync</tests>
-- store_inbox_messages: last word 252, with a message discarded on receive stored without its body. Signature unchanged.
-- <docs>resilience/stream-integrity#remembered-but-missing</docs>
-- <docs>messaging/transports/transport-consumer#batch-handler</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:SingleMessage_StoresInboxAndDedupAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoreInboxMessagesTests.cs:DuplicateMessageId_SecondCallNoOpsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_IsStoredInTheSameWorkShapeAsADeliveryOfTheSameEnvelopeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveSqlTests.cs:AMessageDiscardedOnReceive_IsStoredSettled_WithItsReason_AndNeverClaimedOrCopiedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_DeduplicationRemembers_TheStoreLost_IsRestored_AndTheBucketHealsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARestoredEvent_IsStoredStateOnly_SoTriggerReceptorsThatRanOnItsFirstArrivalDoNotRunAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ADuplicateOfAStoredEvent_IsStillDropped_EvenAsRepairTrafficAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:OrdinaryTraffic_DeduplicationRemembers_TheStoreLost_IsDroppedAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_StillPendingInTheInbox_IsNotStoredTwiceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEphemeralEvent_IsNotRestoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredMissingEventRestoreSqlTests.cs:ARepairedEvent_OnAStreamWhoseEarlierHistoryWasTruncated_IsRefused_AndReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AMessageDiscardedOnReceive_IsStoredWithoutItsBody_AndIsStillADuplicateOnRedeliveryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AMessageDiscardedOnReceive_RedeliveredForProcessing_IsStillADuplicate_AndNotDispatchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:AnOrdinaryArrival_KeepsItsBodyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxDiscardedOnReceiveKeepsNoBodySqlTests.cs:ADiscardedRedelivery_OfAProcessedMessage_LeavesItsStoredBodyAloneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.store_inbox_messages(
  p_messages JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_newly_created BOOLEAN
) AS $$
#variable_conflict use_column
DECLARE
  c_field_flags CONSTANT TEXT := __ENVELOPE_FIELD_FLAGS__;
  v_msg RECORD;
  v_partition INTEGER;
  v_observations INTEGER;  -- 1 on first sight; N on the Nth redelivery
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_is_new BOOLEAN;           -- 236: a first arrival, or a recovery that claimed its dead letter
  v_claimed INTEGER;          -- 236: rows the recovery claim or the message insert touched
  v_settled BOOLEAN;          -- 242: the consumer skipped it on receive, so the row is stored already settled
  -- 252 (#1843): the flags of events integrity never counts, so their absence from the store is no gap.
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  c_flags_never_repaired CONSTANT INTEGER := 8 | 32;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.AtMostOnce (1 << 5)
  v_restore BOOLEAN;          -- 252: repair traffic deduplication remembers and the store lost, stored again as state only
  v_refusal TEXT;             -- 252: why such a child is refused instead, stored settled with the reason
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      elem->>'HandlerName' as handler_name,
      elem->>'EnvelopeType' as envelope_type,
      elem->>'MessageType' as message_type,
      -- 368: a message the consumer discarded on receive keeps no body. Nothing reads a settled row's envelope or
      -- metadata (it is never claimed, copied, dispatched or dead-lettered, and deduplication is answered by
      -- wh_message_deduplication), so both are left unread here and stored empty by the COALESCE in the insert.
      CASE WHEN NULLIF(elem->>'DiscardedOnReceive', '') IS NULL THEN elem->'Envelope' END as envelope_data,
      CASE WHEN NULLIF(elem->>'DiscardedOnReceive', '') IS NULL THEN elem->'Metadata' END as metadata,
      elem->'Scope' as scope,
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- 149: the effective priority the consumer classified; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) as priority,
      -- EventFlags (062): same read as store_outbox_messages so collective events delivered
      -- cross-service via the inbox also route to the __collective__ sink. 232 (#1612): every bit of a name list.
      CASE
        WHEN elem->>c_field_flags IS NULL OR elem->>c_field_flags = '' THEN 0
        WHEN elem->>c_field_flags ~ '^[0-9]+$' THEN (elem->>c_field_flags)::INTEGER
        ELSE __SCHEMA__._wh_event_flags_from_names(elem->>c_field_flags)
      END as flags,
      -- 146 (#727): a zero GUID is "unknown", not a producer; COALESCE below then falls back to this service.
      NULLIF((elem->>'SourceServiceId')::UUID, __EMPTY_UUID__::UUID) as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence,
      -- 236 (#1664): set only by dead-letter recovery, naming the dead-letter row the message re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID as recovered_from,
      -- 242 (#1720): set by a consumer that skipped the message on receive, naming why.
      NULLIF(elem->>'DiscardedOnReceive', '') as discarded_on_receive,
      -- 252 (#1843): set on the children of a redelivery that repair or an operator asked for.
      COALESCE((elem->>'RestoreIfMissing')::BOOLEAN, false) as restore_if_missing
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID NULLS FIRST, (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID
  LOOP
    -- 236 (#1664): dead-letter recovery re-enters a message HERE, as one more arrival, so every arrival rule
    -- applies to it unchanged. Three things differ, and only for a row that names its dead letter:
    --   1. it claims that dead letter first (Recovering), in this transaction, so two recoveries of one row
    --      cannot both re-enter it, and a dead letter whose message is still in the inbox is left alone;
    --   2. a recovered id is usually already in wh_message_deduplication, so the observation is still counted
    --      below (poison bounding keeps working) but the message is not dropped as a duplicate;
    --   3. the work-state row carries the dead-letter id, so the completion settles the dead letter
    --      (process_inbox_completions) instead of recovery declaring it Recovered before anything ran.
    -- The attempts budget is refunded because the row is new: attempts starts at 0 as for every arrival.
    IF v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 1,                  -- Recovering, until the inbox completes the message
          recovery_attempts = dl.recovery_attempts + 1,
          last_recovery_at = p_now
      WHERE dl.dead_letter_id = v_msg.recovered_from
        AND dl.source_id = v_msg.msg_id
        AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
        AND dl.recovered_at IS NULL
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id);
      GET DIAGNOSTICS v_claimed = ROW_COUNT;
      IF v_claimed = 0 THEN
        CONTINUE;  -- claimed by another recovery, already settled, or the message is still in the inbox
      END IF;
    END IF;

    -- 121: DO UPDATE (was DO NOTHING) so a redelivery is COUNTED rather than silently swallowed.
    -- RETURNING gives the post-write count on both arms, so newness is read from the value
    -- (= 1) instead of from ROW_COUNT, which DO UPDATE would report as 1 either way.
    INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
      (message_id, first_seen_at, observation_count)
    VALUES (v_msg.msg_id, p_now, 1)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
      SET observation_count = dedup.observation_count + 1
    RETURNING dedup.observation_count INTO v_observations;

    -- 252 (#1843): for repair traffic deduplication is checked against the event store, not trusted alone. The
    -- deduplication row is written on receipt and the event-store row can be lost afterwards (a failed fan-out in an
    -- older version, manual cleanup, a store restored from an older backup while deduplication survived), and then
    -- every redelivery repair asked for was dropped here, so the bucket never healed and repair asked forever. A
    -- repair child whose id was seen, that is an event integrity counts, and that neither the event store nor the
    -- inbox holds, is stored again. Its envelope is marked state only: deduplication remembers the message, so the
    -- reactions to it had their chance, and repair restores state, never a second reaction. One case is refused: a
    -- stream whose lowest stored version is above 1 had its earlier history truncated here (wh_truncate_stream_prefix
    -- keeps a stream's deduplication rows precisely so a redelivered copy is not stored below the truncation point),
    -- and a lost event cannot be told from a truncated one. The refused child is stored settled with the reason,
    -- which the redelivery's reconciliation reads (wh_reconcile_redelivered_bundle) to report the bucket once as one
    -- repair cannot change. A child the store or the inbox still holds is a duplicate, as before.
    v_restore := FALSE;
    v_refusal := NULL;
    -- 454 (#2350): a copy of a superseding key that a newer copy already held here supersedes is never stored: stored,
    -- it would sit above the newest by version and be applied after it. It is settled as satisfied, the redelivery's
    -- reconciliation reads it as present, and nothing claims, copies or applies it. This holds for a copy arriving for
    -- the first time, by any path (a live receive out of the origin's order, an integrity or an operator redelivery),
    -- and for a repair child deduplication remembers and the store lost. Newer is the origin's order
    -- (_wh_supersedes); without the origin's sequence on both copies nothing is refused, as before.
    IF v_msg.recovered_from IS NULL
       AND v_msg.discarded_on_receive IS NULL
       AND COALESCE(v_msg.is_event, false)
       AND v_msg.stream_id IS NOT NULL
       AND COALESCE(v_msg.flags, 0) & c_flag_superseding <> 0
       AND v_msg.metadata ->> 'sk' IS NOT NULL
       AND (v_observations = 1
            OR (v_msg.restore_if_missing
                AND COALESCE(v_msg.flags, 0) & c_flags_never_repaired = 0
                AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id)
                AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id)))
       AND EXISTS (
         SELECT 1 FROM __SCHEMA__.wh_event_store n
         WHERE n.stream_id = v_msg.stream_id
           AND n.supersede_key = v_msg.metadata ->> 'sk'
           AND COALESCE(n.supersede_group, n.event_type)
               = COALESCE(v_msg.metadata ->> 'sg', __SCHEMA__.normalize_event_type(v_msg.message_type))
           AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                        v_msg.source_service_id, NULLIF(v_msg.source_commit_sequence, 0), 2147483647)) THEN
      v_refusal := __REPAIR_SATISFIED_SUPERSEDED__;
    ELSIF v_observations > 1
       AND v_msg.restore_if_missing
       AND v_msg.recovered_from IS NULL
       AND v_msg.discarded_on_receive IS NULL
       AND COALESCE(v_msg.is_event, false)
       AND v_msg.stream_id IS NOT NULL
       AND COALESCE(v_msg.flags, 0) & c_flags_never_repaired = 0
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_msg.msg_id)
       AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_msg.msg_id) THEN
      IF (SELECT MIN(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = v_msg.stream_id) > 1 THEN
        v_refusal := __REPAIR_REFUSED_TRUNCATED__;
      ELSE
        v_restore := TRUE;
      END IF;
    END IF;

    -- 236: a recovery that claimed its dead letter stores the message whatever the count says.
    -- 252: and so does a repair child restored or refused above.
    v_is_new := v_observations = 1 OR v_msg.recovered_from IS NOT NULL OR v_restore OR v_refusal IS NOT NULL;
    -- 242 (#1720): a message the consumer skipped on receive (no local consumer, directed at another service, this
    -- service's own echo, an event of an unknown type) is stored like every arrival, so the skip is durable and
    -- inspectable, but already settled: processed, Stored and Published, the reason in its error column. Nothing
    -- claims, copies or dispatches a processed row, so it rings no doorbell, pins no stream and is not new work.
    -- 252: a refused repair child is stored settled the same way.
    v_settled := v_msg.discarded_on_receive IS NOT NULL OR v_refusal IS NOT NULL;
    IF v_is_new THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- 114: emptiness probe, see store_outbox_messages; inbox pending = not processed
      -- and schedule-eligible (the drain-fetch predicate minus the lease dimension).
      IF NOT v_settled AND v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
        v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

        -- 162: all three columns are work state now, so the probe reads the narrow table.
        --
        -- The ORDER BY is the remedy from 160 and 161, applied here BEFORE it bites rather than
        -- after. This is the third instance of the same shape: an emptiness probe by stream id with
        -- processed_at IS NULL under a bare LIMIT 1. The planner prices a sequential scan as though
        -- the first row it reads will match, because that predicate is true of nearly every row in
        -- a table carrying a backlog, and on a stream whose work is all done there is no match at
        -- all, so it reads the whole table. Ordering by the index key removes that plan instead of
        -- out-costing it, and stream_id is pinned by the equality so it changes no result. received_at
        -- is in the clause because ordering by stream_id alone is dropped as redundant.
        PERFORM 1 FROM __SCHEMA__.wh_inbox_state ist
          WHERE ist.stream_id = v_msg.stream_id
            AND ist.processed_at IS NULL
            AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= p_now)
          ORDER BY ist.stream_id, ist.received_at
          LIMIT 1
          FOR SHARE OF ist SKIP LOCKED;   -- 140: a locked pending row is being completed; treat as empty and ring
        IF NOT FOUND THEN
          v_empty_inbox_streams := array_append(v_empty_inbox_streams, v_msg.stream_id);
        END IF;
      END IF;

      -- 162: the message row and its claim state are two inserts now. Both carry a DO NOTHING
    -- conflict clause, so the pair is idempotent under the redelivery this function exists to
    -- absorb: a duplicate leaves the existing message AND its existing claim state untouched,
    -- rather than resetting a lease some instance is holding.
    INSERT INTO __SCHEMA__.wh_inbox (
      message_id,
      handler_name,
      message_type,
      event_data,
      metadata,
      scope,
      stream_id,
      is_event,
      flags,
      received_at,
      source_service_id,
      source_commit_sequence,
      priority
    ) VALUES (
      v_msg.msg_id,
      v_msg.handler_name,
      v_msg.message_type,
      -- 252: a restored repair child is state only ('sto', the envelope's StateOnly on the wire).
      CASE WHEN v_restore
        THEN jsonb_set(COALESCE(v_msg.envelope_data, '{}'::jsonb), ARRAY['sto'], 'true'::jsonb)
        ELSE COALESCE(v_msg.envelope_data, '{}'::jsonb)
      END,
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      COALESCE(v_msg.is_event, false),
      COALESCE(v_msg.flags, 0),
      p_now,
      COALESCE(v_msg.source_service_id, (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)),
      COALESCE(v_msg.source_commit_sequence, 0),
      v_msg.priority
    )
    ON CONFLICT ON CONSTRAINT wh_inbox_pkey DO NOTHING;
    -- 236: a recovery that lost the race to a concurrent arrival of the same id hands its dead letter back,
    -- so the dead letter is never left Recovering behind a row that does not name it.
    GET DIAGNOSTICS v_claimed = ROW_COUNT;
    IF v_claimed = 0 AND v_msg.recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 0
      WHERE dl.dead_letter_id = v_msg.recovered_from AND dl.recovery_status = 1;
      CONTINUE;
    END IF;
    -- 252: a restore that lost the race to a concurrent arrival of the same id leaves that arrival as it is.
    IF v_claimed = 0 AND v_restore THEN
      CONTINUE;
    END IF;

    -- The lease row, unleased and unattempted. A message with no lease row can never be claimed and
    -- would fail silently, so this insert is not optional bookkeeping: it is what makes the message
    -- visible to the claim at all.
    INSERT INTO __SCHEMA__.wh_inbox_state (
      message_id, stream_id, received_at, partition_number, priority, is_event,
      status, processed_at, instance_id, lease_expiry, attempts, recovered_from_dead_letter_id, error
    ) VALUES (
      v_msg.msg_id,
      v_msg.stream_id,
      p_now,
      v_partition,
      v_msg.priority,
      COALESCE(v_msg.is_event, false),
      CASE WHEN v_settled THEN 1 | 4 ELSE 1 END,  -- Stored flag; 242: and Published, for a row settled on receive
      CASE WHEN v_settled THEN p_now END,          -- 242: a settled row is processed as it arrives
      NULL,  -- No lease, immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      0,     -- Initial attempts
      v_msg.recovered_from,  -- 236: NULL for every arrival but a recovery
      -- 242: the reason a settled row records, on the column a processing failure would use; NULL otherwise.
      -- 252: a refused repair child records why it was refused.
      COALESCE(v_refusal, 'discarded on receive: ' || v_msg.discarded_on_receive)
    )
    ON CONFLICT (message_id) DO NOTHING;

    IF v_settled THEN
      CONTINUE;  -- 242: no doorbell, no pin, not new work
    END IF;

      IF v_msg.stream_id IS NOT NULL
         AND v_msg.stream_id = ANY(v_empty_inbox_streams)
         AND NOT (v_msg.stream_id = ANY(v_notify_inbox_streams)) THEN
        v_notify_inbox_streams := array_append(v_notify_inbox_streams, v_msg.stream_id);
      END IF;

      -- Pinning is ownership/routing, unchanged by 114.
      IF v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
        INSERT INTO __SCHEMA__.wh_active_streams
          (stream_id, partition_number, assigned_instance_id, last_activity_at)
        VALUES
          (v_msg.stream_id, COALESCE(v_partition, 0), p_instance_id, p_now)
        ON CONFLICT (stream_id) DO NOTHING;
      END IF;
      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, TRUE AS was_newly_created;
    END IF;  -- end of the single-observation branch (236: or a claimed recovery)
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_INBOX__, v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_inbox_messages(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores received inbox messages with deduplication (121: every arrival counted), priority (149), the work-state row (162). 236 (#1664): a message recovered from a dead letter (RecoveredFromDeadLetterId) re-enters here as one more arrival: it claims its dead letter, is stored even though its id was seen, and its work-state row names the dead letter for the completion to settle. 242 (#1720): a message the consumer skipped on receive (DiscardedOnReceive) is stored settled: processed, Stored and Published, the reason in error, never claimed, copied or returned as new work. 368: and without its body (event_data and metadata stored empty), since nothing reads a settled row''s body and deduplication does not need it. 252 (#1843): a repair child (RestoreIfMissing) whose id was seen but which neither the event store nor the inbox holds is stored again as state only, or stored settled as refused when its stream had earlier history truncated.';


SELECT __SCHEMA__.drop_all_overloads('wh_roll_off_carried_detail');
SELECT __SCHEMA__.drop_all_overloads('wh_record_consumer_seal');
SELECT __SCHEMA__.drop_all_overloads('_wh_uncarry_restored');
SELECT __SCHEMA__.drop_all_overloads('_wh_carry_cut');
SELECT __SCHEMA__.drop_all_overloads('_wh_cut_awaits_seal');
SELECT __SCHEMA__.drop_all_overloads('_wh_epoch_width');
DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_consumer_seals;
DROP TABLE IF EXISTS __SCHEMA__.wh_digest_epoch_removed;
DROP INDEX IF EXISTS __SCHEMA__.idx_stream_digests_carried_at;
ALTER TABLE __SCHEMA__.wh_stream_digests DROP COLUMN IF EXISTS carried_at;
ALTER TABLE __SCHEMA__.wh_stream_digests DROP COLUMN IF EXISTS carried_id_hi;
ALTER TABLE __SCHEMA__.wh_stream_digests DROP COLUMN IF EXISTS carried_id_lo;
ALTER TABLE __SCHEMA__.wh_stream_digests DROP COLUMN IF EXISTS carried_count;
ALTER TABLE __SCHEMA__.wh_stream_digests DROP COLUMN IF EXISTS carried_hi;
ALTER TABLE __SCHEMA__.wh_stream_digests DROP COLUMN IF EXISTS carried_lo;
DELETE FROM __SCHEMA__.wh_settings WHERE setting_key IN ('integrity_cut_waits_for_seal', 'integrity_cut_waits_for_consumers');
