-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 446_StreamDigestHealIsLinearInItsBatch.sql
-- Date: 2026-11-14
-- Description: Healing a batch of stream digests costs time in proportion to the batch (#2337).
--
--              _wh_heal_stream_digests (336) recomputes a batch of streams' buckets from their events and rewrites
--              what differs. The planner cannot estimate the event filters (flag bits, exempt types), so it believes
--              the recomputed set is one row, and a nested loop that rescans that set per stored bucket is quadratic in
--              practice: a 500-stream batch of about 12 thousand buckets ran past the 30 second command timeout on
--              every attempt, and the id-lane backfill (337), which repeats a failed batch, never moved past it.
--
--              The statement now lives in _wh_heal_stream_digests_statement, so it can be planned on its own, and
--              the heal runs exactly that text.
--
--              Number 446: reserved for this change.
--
-- Reversal: reversible (the heal goes back to its 336 definition; the statement helper goes)
-- Dependencies: 336 (_wh_heal_stream_digests, _wh_digest_lane)
-- Objects: _wh_heal_stream_digests_statement, _wh_heal_stream_digests
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('_wh_heal_stream_digests_statement');

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
        AND COALESCE(es.flags, 0) & %3$s = 0
        AND COALESCE(es.flags, 0) & %4$s = 0
        AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
        AND es.created_at < NOW() - $2
      GROUP BY 1, 2, 3, 4
    ),
    stored AS (
      SELECT d.origin_service_id AS o, d.scope_tenant AS t, d.event_type AS ty, d.stream_id AS s,
             d.digest_lo, d.digest_hi, d.event_count, d.id_lo, d.id_hi
      FROM __SCHEMA__.wh_stream_digests d
      WHERE %2$s AND d.updated_at < NOW() - $2
    ),
    compared AS (
      SELECT st.o, st.t, st.ty, st.s, st.digest_lo, st.digest_hi, st.event_count, r.lo, r.hi, r.ilo, r.ihi, r.n,
             (st.digest_lo <> r.lo OR st.digest_hi <> r.hi OR st.event_count <> r.n
              OR st.id_lo <> r.ilo OR st.id_hi <> r.ihi) AS drifted,
             (st.id_lo IS NULL OR st.id_hi IS NULL) AS unfilled
      FROM stored st
      JOIN recomputed r ON r.o = st.o AND r.t = st.t AND r.ty = st.ty AND r.s = st.s
    ),
    rewrite AS (
      SELECT c.*, CASE WHEN c.drifted THEN 'drift_updated' ELSE 'filled' END AS why
      FROM compared c
      WHERE c.drifted OR ($3 AND c.unfilled)
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
      USING stored st
      WHERE d.origin_service_id = st.o AND d.scope_tenant = st.t AND d.event_type = st.ty AND d.stream_id = st.s
        AND NOT EXISTS (SELECT 1 FROM recomputed r WHERE r.o = st.o AND r.t = st.t AND r.ty = st.ty AND r.s = st.s)
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
    FROM rewrite w CROSS JOIN (SELECT count(*) FROM updated) u
    UNION ALL
    SELECT 'drift_removed', x.origin_service_id, x.scope_tenant, x.event_type, x.stream_id,
           x.digest_lo, x.digest_hi, x.event_count, NULL::bigint, NULL::bigint, NULL::int
    FROM removed x
    UNION ALL
    SELECT 'drift_added', a.origin_service_id, a.scope_tenant, a.event_type, a.stream_id,
           NULL::bigint, NULL::bigint, NULL::int, a.digest_lo, a.digest_hi, a.event_count
    FROM added a
    $q$, v_events, v_digests, c_flag_ephemeral, c_flag_at_most_once);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_heal_stream_digests_statement(BOOLEAN) IS
  '446 (#2337): the statement _wh_heal_stream_digests runs, for a named batch or the whole store: $1 streams, $2 settle '
  'window, $3 fill id lanes. Separate so it can be planned on its own.';

SELECT __SCHEMA__.drop_all_overloads('_wh_heal_stream_digests');

-- Re-created from 336: the same contract and result, running the statement its helper returns.
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>resilience/stream-integrity#the-heal-is-linear-in-its-batch</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestHealPlanSqlTests.cs:TheHealFunction_RunsTheStatementItsHelperReturnsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestFoldHelperSqlTests.cs:Heal_ADriftedBucket_IsRewrittenWithEveryLane_AndReportedWithBothValuesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestFoldHelperSqlTests.cs:Heal_ABucketWithoutIdLanes_IsFilledOnlyWhenAsked_AndReportedAsFilledNotDriftAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestFoldHelperSqlTests.cs:Heal_APhantomBucketIsRemoved_AndAMissingOneAdded_EachReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestFoldHelperSqlTests.cs:Heal_ABucketChangedInsideTheSettleWindow_IsLeftAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:VerifyDigestTable_HealsAllThreeDriftShapesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_heal_stream_digests(
  p_streams UUID[],
  p_settle INTERVAL,
  p_fill_id_lanes BOOLEAN
) RETURNS TABLE(kind TEXT, origin_service_id UUID, scope_tenant TEXT, event_type TEXT, stream_id UUID,
                stored_lo BIGINT, stored_hi BIGINT, stored_count INTEGER,
                recomputed_lo BIGINT, recomputed_hi BIGINT, recomputed_count INTEGER) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY EXECUTE __SCHEMA__._wh_heal_stream_digests_statement(p_streams IS NOT NULL)
  USING p_streams, p_settle, p_fill_id_lanes;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_heal_stream_digests(UUID[], INTERVAL, BOOLEAN) IS
  '336, 446 (#2106, #2337): recomputes the settled buckets of the named streams (NULL: all) from their events and '
  'rewrites what differs, every lane together: drift_updated, drift_removed, drift_added, and (when asked) filled for '
  'id lanes never computed. Returns each changed bucket with its stored and recomputed values, so drift is reported, '
  'never hidden. The statement is _wh_heal_stream_digests_statement.';
