-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 458_FullHistoryCompareRunsInSlices.sql
-- Date: 2026-11-16
-- Description: The full-history compare is bounded by a lookback and runs in slices (#2361).
--
--              The compare healed the whole digest table in one statement and asked each origin for its whole history,
--              so its cost grew with the store. Now:
--              - wh_heal_stream_digests_slice: one run takes the next p_max_streams stream ids in key order after a
--                cursor kept in wh_settings, read from both the lifecycle ledger and the digest table (a phantom bucket
--                has no ledger row), heals those last written within p_lookback (and those the ledger cannot date)
--                with _wh_heal_stream_digests, and stores where it stopped. A slice that reads fewer than
--                p_max_streams ids reached the end: the pass is complete and the next slice starts from the first.
--              - wh_full_history_compare_start: where the ask to an origin starts, the first sequence of the oldest
--                epoch of its lane sealed within the lookback, never above the seal.
--
--              Cost: a slice reads at most p_max_streams entries of each of two primary-key-ordered indexes
--              (wh_stream_lifecycle_pkey, idx_stream_digests_stream) and heals at most that many streams; the start
--              reads one lane's seal rows. No index is added: last_appended_bucket is written on the hottest path
--              (260), so the lookback is a filter on the slice, never a scan of its own.
--
--              Number 458: after 457 in this bundle.
--
-- Reversal: reversible (the two functions and the cursor setting go; the next compare starts a fresh pass)
-- Dependencies: 446 (_wh_heal_stream_digests), 260 (wh_stream_lifecycle), 087 (wh_stream_digests),
--               450 (wh_digest_epoch_seals), 028 (wh_settings)
-- Objects: wh_heal_stream_digests_slice, wh_full_history_compare_start

SELECT __SCHEMA__.drop_all_overloads('wh_heal_stream_digests_slice');

-- One slice of the full-history compare's heal. Returns the settled buckets of the slice's streams, the drift healed by
-- kind, the streams the slice took, whether it completed a pass and the runs that pass has taken.
-- <docs>resilience/stream-integrity#full-history-compare</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.Slices.cs:ASlice_HealsAtMostMaxPerRunStreams_AndTheNextResumesAfterItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.Slices.cs:AStreamLastWrittenBeforeTheLookback_IsNotHealedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.Slices.cs:APhantomBucket_OfAStreamTheLedgerDoesNotRecord_IsRemovedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_heal_stream_digests_slice(
  p_settle INTERVAL, p_lookback INTERVAL, p_max_streams INTEGER
) RETURNS TABLE(buckets_checked INTEGER, drift_updated INTEGER, drift_removed INTEGER, drift_added INTEGER,
                streams_checked INTEGER, pass_completed BOOLEAN, pass_runs INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  c_key CONSTANT TEXT := 'integrity_full_history_compare_cursor';
  v_state JSONB;
  v_after UUID;
  v_runs INTEGER;
  v_page UUID[];
  v_heal UUID[];
  v_last UUID;
  v_completed BOOLEAN;
BEGIN
  SELECT s.setting_value::jsonb INTO v_state FROM __SCHEMA__.wh_settings s WHERE s.setting_key = c_key FOR UPDATE;
  v_after := NULLIF(v_state ->> 'after', '')::uuid;
  v_runs := COALESCE((v_state ->> 'runs')::int, 0) + 1;

  -- The next ids after the cursor from each source, merged: at most p_max_streams entries read from each index.
  SELECT COALESCE(array_agg(p.stream_id ORDER BY p.stream_id), '{}') INTO v_page
  FROM (
    SELECT u.stream_id FROM (
      (SELECT l.stream_id FROM __SCHEMA__.wh_stream_lifecycle l
        WHERE v_after IS NULL OR l.stream_id > v_after ORDER BY l.stream_id LIMIT p_max_streams)
      UNION
      (SELECT DISTINCT d.stream_id FROM (
         SELECT d0.stream_id FROM __SCHEMA__.wh_stream_digests d0
         WHERE v_after IS NULL OR d0.stream_id > v_after ORDER BY d0.stream_id LIMIT p_max_streams) d)
    ) u ORDER BY u.stream_id LIMIT p_max_streams
  ) p;

  -- Within the lookback, or undatable (no ledger row): an older stream's history is the sealed epochs'.
  SELECT COALESCE(array_agg(s), '{}') INTO v_heal
  FROM unnest(v_page) s
  WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stream_lifecycle l
                    WHERE l.stream_id = s AND l.last_appended_bucket < NOW() - p_lookback);

  v_completed := cardinality(v_page) < p_max_streams;
  v_last := v_page[cardinality(v_page)];
  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
  VALUES (c_key,
          CASE WHEN v_completed THEN '{}' ELSE jsonb_build_object('after', v_last, 'runs', v_runs)::text END,
          'json', 'Where the next slice of the full-history compare starts, and the runs of this pass (#2361)')
  ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value, updated_at = NOW();

  RETURN QUERY
  WITH heal AS (
    SELECT h.kind FROM __SCHEMA__._wh_heal_stream_digests(v_heal, p_settle, false) h
  )
  SELECT (SELECT COUNT(*)::int FROM __SCHEMA__.wh_stream_digests d
          WHERE d.stream_id = ANY(v_heal) AND d.updated_at < NOW() - p_settle),
         (SELECT COUNT(*)::int FROM heal WHERE heal.kind = 'drift_updated'),
         (SELECT COUNT(*)::int FROM heal WHERE heal.kind = 'drift_removed'),
         (SELECT COUNT(*)::int FROM heal WHERE heal.kind = 'drift_added'),
         cardinality(v_page),
         v_completed,
         v_runs;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_heal_stream_digests_slice(INTERVAL, INTERVAL, INTEGER) IS
'#2361: one slice of the full-history compare''s heal: the next p_max_streams streams after the stored cursor, healed when written within p_lookback; stores where it stopped.';

SELECT __SCHEMA__.drop_all_overloads('wh_full_history_compare_start');

-- Where the full-history compare's ask to an origin starts.
-- <docs>resilience/stream-integrity#full-history-compare</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.Slices.cs:TheAsksStart_IsTheOldestEpochSealedWithinTheLookback_NeverAboveTheSealAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_full_history_compare_start(
  p_origin UUID, p_lookback INTERVAL, p_epoch_width BIGINT, p_seal BIGINT
) RETURNS BIGINT
LANGUAGE sql STABLE
AS $$
  SELECT LEAST(p_seal, COALESCE(MIN(s.epoch_id) * p_epoch_width, p_seal))
  FROM __SCHEMA__.wh_digest_epoch_seals s
  WHERE s.origin_service_id = p_origin AND s.sealed_at >= NOW() - p_lookback;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_full_history_compare_start(UUID, INTERVAL, BIGINT, BIGINT) IS
'#2361: the sequence the full-history compare asks an origin from: the oldest epoch of its lane sealed within the lookback, never above the seal.';
