-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 456_RecordsNothingElseRemovesRollOff.sql (#2359): the operational retention sweep goes back to 451's
-- word, sweeping the rewrite log and finished restore requests only. Rows it removed do not come back.

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
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_sweep_operational_retention(TIMESTAMPTZ, JSONB, INTEGER) IS
'Issue #2354: one batch of the operational retention sweep over the Rolling tables it covers; removes closed rows past their category''s resting period and reports what is left per table.';
