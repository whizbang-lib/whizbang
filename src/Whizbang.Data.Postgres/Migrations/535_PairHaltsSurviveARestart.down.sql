-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 531_PairHaltsSurviveARestart.sql (#2377): the pair guard's halt functions and table go, and the
-- operational retention sweep goes back to 457's word. Kept halts do not come back.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_integrity_pair_halt_record(TEXT, SMALLINT, SMALLINT, TEXT[], INTEGER, INTEGER, TEXT, INTEGER, INTEGER, TEXT, TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_integrity_pair_halt_alerted(TEXT, SMALLINT, TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_integrity_pair_halt_resumed(TEXT, SMALLINT, TIMESTAMPTZ);

SELECT __SCHEMA__.drop_all_overloads('wh_sweep_operational_retention');

-- One batch of the operational retention sweep. p_retention_seconds maps a category name (OperationalCategory) to its
-- resting period in seconds; a category it does not name removes nothing and is still reported. Returns one row per
-- swept table: the category, the table, the rows this batch removed, the Rolling rows left and the age of the oldest
-- one left (NULL when none is).
-- <docs>operations/infrastructure/operational-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:TheRewriteLog_LosesItsRowsPastTheRetention_AndItsStartMovesWithThemAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:TheRewriteLog_IsRemovedOldestGenerationFirst_InBoundedBatchesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:ARestoredRequest_IsRemovedPastTheRetention_AndAnOpenOrFailedOneStaysAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:ACategoryWithNoRetentionGiven_RemovesNothing_ButIsStillReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:AnEmptyTable_ReportsNoRows_AndNoAgeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalTableRegistryGuardTests.cs:TheSweep_RemovesExactlyTheTablesTheRegistryMarksSweptHereAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:AnAdvisoryFinding_UntouchedPastTheRetention_GoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:ADeadLetterSummary_GoesOnlyOnceItsDeadLettersAreGoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:AFinishedProbeCampaign_GoesOnceItsCohortIsGone_ARunningOneNeverAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:AStack_GoesWithItsLinks_OnceNothingNamesIt_AndThenItsUnusedFramesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:ASignalCursor_OfAGoneInstance_GoesPastTheRetention_ALivePodsNeverAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalRetentionSweepSqlTests.cs:AFinishedSubscriptionBackfill_IsNeverSwept_ItIsTheAlreadyDoneMarkerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_sweep_operational_retention(
  p_now               TIMESTAMPTZ,
  p_retention_seconds JSONB,
  p_batch             INTEGER
) RETURNS TABLE (category TEXT, table_name TEXT, rows_expired INTEGER, rows_remaining BIGINT, oldest_age_seconds DOUBLE PRECISION)
LANGUAGE plpgsql
AS $$
DECLARE
  v_keep    BIGINT;
  v_through BIGINT;
  v_removed INTEGER;
  v_max_gen BIGINT;
  v_links   INTEGER;
BEGIN
  -- ── the rewrite log (RewriteLog) ─────────────────────────────────────────────────────────
  v_removed := 0;
  v_keep := (p_retention_seconds ->> 'RewriteLog')::bigint;
  IF v_keep IS NOT NULL THEN
    -- The newest generation old enough to go; everything at or below it goes, oldest first, so the log stays
    -- contiguous and its start is exact.
    SELECT MAX(r.generation) INTO v_through
    FROM __SCHEMA__.wh_integrity_origin_rewrites r
    WHERE r.rewritten_at < p_now - make_interval(secs => v_keep);
    IF v_through IS NOT NULL THEN
      WITH doomed AS (
        SELECT r.generation FROM __SCHEMA__.wh_integrity_origin_rewrites r
        WHERE r.generation <= v_through
        ORDER BY r.generation
        LIMIT GREATEST(p_batch, 1)
      ), gone AS (
        DELETE FROM __SCHEMA__.wh_integrity_origin_rewrites r
        USING doomed d
        WHERE r.generation = d.generation
        RETURNING r.generation
      )
      SELECT COUNT(*)::int, MAX(g.generation) INTO v_removed, v_max_gen FROM gone g;
      IF v_max_gen IS NOT NULL THEN
        UPDATE __SCHEMA__.wh_settings s
        SET setting_value = GREATEST(COALESCE(NULLIF(s.setting_value, '')::bigint, 0), v_max_gen)::text
        WHERE s.setting_key = 'integrity_origin_rewrite_log_from';
      END IF;
    END IF;
  END IF;
  RETURN QUERY
    SELECT 'RewriteLog'::text, 'wh_integrity_origin_rewrites'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(r.rewritten_at)))::double precision
    FROM __SCHEMA__.wh_integrity_origin_rewrites r;

  -- ── finished restore requests (Queues) ──────────────────────────────────────────────────
  v_removed := 0;
  v_keep := (p_retention_seconds ->> 'Queues')::bigint;
  IF v_keep IS NOT NULL THEN
    WITH doomed AS (
      SELECT q.stream_id FROM __SCHEMA__.wh_archive_restore_requests q
      WHERE q.state = 'Restored' AND q.updated_at < p_now - make_interval(secs => v_keep)
      ORDER BY q.updated_at
      LIMIT GREATEST(p_batch, 1)
      FOR UPDATE SKIP LOCKED
    ), gone AS (
      DELETE FROM __SCHEMA__.wh_archive_restore_requests q
      USING doomed d
      WHERE q.stream_id = d.stream_id AND q.state = 'Restored'
      RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_removed FROM gone;
  END IF;
  RETURN QUERY
    SELECT 'Queues'::text, 'wh_archive_restore_requests'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(q.updated_at)))::double precision
    FROM __SCHEMA__.wh_archive_restore_requests q
    WHERE q.state = 'Restored';

  -- 456 (#2359): records nothing else removed, all Reports. Each goes only once nothing still relies on it.
  v_keep := (p_retention_seconds ->> 'Reports')::bigint;

  -- ── advisory findings no scan touched for the retention (Reports) ────────────────────────
  -- A finding still detected is touched on every scan, so its row stays and a repeat inside the cooldown stays quiet.
  v_removed := 0;
  IF v_keep IS NOT NULL THEN
    WITH doomed AS (
      SELECT a.finding_key FROM __SCHEMA__.wh_advisory_ledger a
      WHERE a.last_touched < p_now - make_interval(secs => v_keep)
      ORDER BY a.last_touched
      LIMIT GREATEST(p_batch, 1)
      FOR UPDATE SKIP LOCKED
    ), gone AS (
      DELETE FROM __SCHEMA__.wh_advisory_ledger a USING doomed d
      WHERE a.finding_key = d.finding_key AND a.last_touched < p_now - make_interval(secs => v_keep)
      RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_removed FROM gone;
  END IF;
  RETURN QUERY
    SELECT 'Reports'::text, 'wh_advisory_ledger'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(a.last_touched)))::double precision
    FROM __SCHEMA__.wh_advisory_ledger a;

  -- ── dead letter clusters with no dead letter left (Reports) ──────────────────────────────
  -- aggregate_dead_letters only upserts, so a cluster whose dead letters were all purged kept its row for good. While
  -- one dead letter of the cluster is left, the next aggregation would write the row back, so it stays.
  v_removed := 0;
  IF v_keep IS NOT NULL THEN
    WITH doomed AS (
      SELECT s.error_fingerprint, s.source_table, s.message_type FROM __SCHEMA__.wh_dead_letter_summary s
      WHERE s.last_seen_at < p_now - make_interval(secs => v_keep)
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_dead_letters dl
                        WHERE dl.error_fingerprint = s.error_fingerprint
                          AND dl.source_table = s.source_table AND dl.message_type = s.message_type)
      ORDER BY s.last_seen_at
      LIMIT GREATEST(p_batch, 1)
      FOR UPDATE SKIP LOCKED
    ), gone AS (
      DELETE FROM __SCHEMA__.wh_dead_letter_summary s USING doomed d
      WHERE s.error_fingerprint = d.error_fingerprint AND s.source_table = d.source_table
        AND s.message_type = d.message_type
      RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_removed FROM gone;
  END IF;
  RETURN QUERY
    SELECT 'Reports'::text, 'wh_dead_letter_summary'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(s.last_seen_at)))::double precision
    FROM __SCHEMA__.wh_dead_letter_summary s;

  -- ── finished probe campaigns whose cohort is gone (Reports) ──────────────────────────────
  -- A running campaign (verdict 0) is work. A finished one is evidence while its cohort has dead letters: a failed
  -- campaign spends the cohort's generation budget, a passed one lets its rows be re-driven. Once no dead letter of
  -- the fingerprint is left, neither can matter.
  v_removed := 0;
  IF v_keep IS NOT NULL THEN
    WITH doomed AS (
      SELECT c.fingerprint, c.generation FROM __SCHEMA__.wh_dlq_probe_campaigns c
      WHERE c.verdict <> 0 AND c.verdict_at < p_now - make_interval(secs => v_keep)
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_dead_letters dl WHERE dl.error_fingerprint = c.fingerprint)
      ORDER BY c.verdict_at
      LIMIT GREATEST(p_batch, 1)
      FOR UPDATE SKIP LOCKED
    ), gone AS (
      DELETE FROM __SCHEMA__.wh_dlq_probe_campaigns c USING doomed d
      WHERE c.fingerprint = d.fingerprint AND c.generation = d.generation AND c.verdict <> 0
      RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_removed FROM gone;
  END IF;
  RETURN QUERY
    SELECT 'Reports'::text, 'wh_dlq_probe_campaigns'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(c.verdict_at)))::double precision
    FROM __SCHEMA__.wh_dlq_probe_campaigns c
    WHERE c.verdict <> 0;

  -- ── stacks nothing names, with their links, then unused frames (Reports) ──────────────────
  -- A stack goes only when neither a dead letter nor the daily history (its own 90-day window) names it; its links go
  -- with it. A frame goes once it is past the retention and no link names it. If a removed stack recurs it is recorded
  -- afresh and counted as new, which after the retention with nothing naming it is what it is.
  v_removed := 0;
  v_links   := 0;
  IF v_keep IS NOT NULL THEN
    WITH doomed AS (
      SELECT st.stack_id FROM __SCHEMA__.wh_stacks st
      WHERE COALESCE(st.last_seen, st.first_seen) < p_now - make_interval(secs => v_keep)
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_dead_letters dl WHERE dl.stack_id = st.stack_id)
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stack_daily sd WHERE sd.stack_id = st.stack_id)
      ORDER BY COALESCE(st.last_seen, st.first_seen)
      LIMIT GREATEST(p_batch, 1)
      FOR UPDATE SKIP LOCKED
    ), gone_links AS (
      DELETE FROM __SCHEMA__.wh_stack_links l USING doomed d WHERE l.stack_id = d.stack_id
      RETURNING 1
    ), gone AS (
      DELETE FROM __SCHEMA__.wh_stacks st USING doomed d WHERE st.stack_id = d.stack_id
      RETURNING 1
    )
    SELECT (SELECT COUNT(*) FROM gone)::int, (SELECT COUNT(*) FROM gone_links)::int INTO v_removed, v_links;
  END IF;
  RETURN QUERY
    SELECT 'Reports'::text, 'wh_stacks'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(COALESCE(st.last_seen, st.first_seen))))::double precision
    FROM __SCHEMA__.wh_stacks st;
  RETURN QUERY
    SELECT 'Reports'::text, 'wh_stack_links'::text, v_links, COUNT(*)::bigint, NULL::double precision
    FROM __SCHEMA__.wh_stack_links l;

  v_removed := 0;
  IF v_keep IS NOT NULL THEN
    WITH doomed AS (
      SELECT f.frame_id FROM __SCHEMA__.wh_stack_frames f
      WHERE f.first_seen < p_now - make_interval(secs => v_keep)
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stack_links l WHERE l.frame_id = f.frame_id)
      ORDER BY f.first_seen
      LIMIT GREATEST(p_batch, 1)
      FOR UPDATE SKIP LOCKED
    ), gone AS (
      DELETE FROM __SCHEMA__.wh_stack_frames f USING doomed d
      WHERE f.frame_id = d.frame_id
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stack_links l WHERE l.frame_id = f.frame_id)
      RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_removed FROM gone;
  END IF;
  RETURN QUERY
    SELECT 'Reports'::text, 'wh_stack_frames'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(f.first_seen)))::double precision
    FROM __SCHEMA__.wh_stack_frames f;

  -- ── 457 (#2360): signal cursors of instances that are gone (Deliveries) ──────────────────
  -- Every pod start adds a cursor and nothing removed one. A cursor goes once its instance is no longer registered
  -- and it has not moved for the retention. A live pod's cursor is never removed: the signal log is kept down to the
  -- lowest live cursor, and a removed live cursor would let its pod miss signals.
  v_removed := 0;
  v_keep := (p_retention_seconds ->> 'Deliveries')::bigint;
  IF v_keep IS NOT NULL THEN
    WITH doomed AS (
      SELECT c.instance_id FROM __SCHEMA__.wh_signal_cursors c
      WHERE c.updated_at < p_now - make_interval(secs => v_keep)
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances i WHERE i.instance_id = c.instance_id)
      ORDER BY c.updated_at
      LIMIT GREATEST(p_batch, 1)
      FOR UPDATE SKIP LOCKED
    ), gone AS (
      DELETE FROM __SCHEMA__.wh_signal_cursors c USING doomed d
      WHERE c.instance_id = d.instance_id
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances i WHERE i.instance_id = c.instance_id)
      RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_removed FROM gone;
  END IF;
  RETURN QUERY
    SELECT 'Deliveries'::text, 'wh_signal_cursors'::text, v_removed, COUNT(*)::bigint,
           EXTRACT(EPOCH FROM (p_now - MIN(c.updated_at)))::double precision
    FROM __SCHEMA__.wh_signal_cursors c
    WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances i WHERE i.instance_id = c.instance_id);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_sweep_operational_retention(TIMESTAMPTZ, JSONB, INTEGER) IS
'Issue #2354: one batch of the operational retention sweep over the Rolling tables it covers; removes closed rows past their category''s resting period and reports what is left per table. 456 (#2359): also advisory findings, dead letter clusters, probe campaigns and stacks that nothing relies on any more. 457 (#2360): signal cursors of instances that are gone.';


DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_pair_halts;
