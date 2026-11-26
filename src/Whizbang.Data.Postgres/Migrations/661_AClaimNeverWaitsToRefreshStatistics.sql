-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 661_AClaimNeverWaitsToRefreshStatistics.sql
-- Date: 2026-11-26
-- Description: The claim's statistics refresh never waits on another session's lock, and analyzes a table at most
--              once a minute, backing it off after an analyze, a skip or a failure (#2611).
--
--              Measured on a deployed service: an application transaction stayed idle for over three hours, and the
--              maintenance REINDEX INDEX CONCURRENTLY of a queue index waited on it for all that time while holding
--              ShareUpdateExclusiveLock on wh_inbox_state. That lock blocks ANALYZE. The table's statistics were
--              stale, so every claim queued on the refresh's blocking
--              pg_advisory_xact_lock behind the one claimant waiting in ANALYZE, waited out the five second lock
--              timeout in turn, and gave up. The service's claims averaged 8.2 s, against 20 to 40 ms elsewhere.
--
--              wh_refresh_stale_queue_statistics (copied from its last word, 463) now, per stale table:
--                the back-off     skips a table the refresh analyzed, skipped or failed on within the last 60
--                                 seconds, read from wh_queue_statistics_backoff (one row per queue table, read by key,
--                                 never locked by a reader). After the lock was gone, the same service showed why an
--                                 analyze must back off too: its inbox state table, a few dozen live rows churned by
--                                 thousands of changes a minute, read as stale again right after every analyze, so the
--                                 refresh analyzed it about 90 times a minute and was most of every claim's time. Sixty
--                                 seconds is autovacuum's own default naptime: a table analyzed within it is as fresh as
--                                 autovacuum keeps any table;
--                the turn         takes the table's advisory lock with pg_try_advisory_xact_lock: a claimant that
--                                 finds it taken leaves the table to its holder and moves on, without waiting and
--                                 without writing anything;
--                the analyze      runs ANALYZE (SKIP_LOCKED), which skips the table instead of waiting when another
--                                 session holds a conflicting lock, and reads pg_locks to know whether it ran (a
--                                 completed ANALYZE holds ShareUpdateExclusiveLock until the claim commits). A one
--                                 second lock timeout remains as a backstop for any other lock it meets;
--                autovacuum       keeps 207's remedy for a worker stuck in its vacuum (#1349): when the only sessions
--                                 holding a conflicting lock are autovacuum workers (_wh_only_autovacuum_blocks_analyze),
--                                 the turn's holder runs the plain ANALYZE under a lock timeout of deadlock_timeout plus
--                                 one second. PostgreSQL cancels an autovacuum that is not preventing wraparound once
--                                 another session has waited on it for deadlock_timeout, so the worker is ended. One
--                                 claimant per table pays that wait, once per back-off at most;
--                the record       an analyze (analyzed), a skip (lock_not_available), a lock timeout (lock_timeout) or
--                                 an ANALYZE that raised (failed) writes the table's back-off row, retry 60 seconds
--                                 later; all but an analyze also raise one
--                                 WARNING whizbang.queue_statistics_refresh_skipped with DETAIL table=reason and a HINT
--                                 saying what happened. Only the turn's holder writes, so the row is never contended,
--                                 and the warning is raised once per back-off. The coordinators count it
--                                 (whizbang.work_coordinator.queue_statistics.refresh_skipped) and log it.
--
--              Cost: the healthy path is unchanged, one catalog read per table and nothing else. The stale path adds,
--              per stale table, a relname lookup and a primary-key probe of the five-row back-off table; the turn is
--              no longer a wait, and ANALYZE runs at most once a minute per table. Each attempt adds one upsert, and a
--              skip a pg_locks read and a pg_stat_activity read, once per back-off.
--
--              Number 661: develop ends at 660, and 630 to 659 belong to a change in flight.
--
-- Reversal: reversible (the down file restores 463's definition and drops the back-off table and the helper)
-- Dependencies: 463 (wh_refresh_stale_queue_statistics, its last word), 207 (_wh_queue_statistics_stale)
-- Objects: wh_queue_statistics_backoff, _wh_only_autovacuum_blocks_analyze, wh_refresh_stale_queue_statistics
-- Constants: none.

-- ---------------------------------------------------------------------------------------------
-- wh_queue_statistics_backoff: when each queue table's refresh may be tried again.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_queue_statistics_backoff (
  table_name    TEXT        NOT NULL PRIMARY KEY,
  retry_after   TIMESTAMPTZ NOT NULL,
  reason        TEXT        NOT NULL,
  backed_off_at TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_queue_statistics_backoff IS
'Issue #2611: one row per queue table, the claim statistics refresh''s last attempt at it. Claims leave the table alone until retry_after. reason: analyzed (it ran; a table that churns reads as stale again at once), lock_not_available (another session held a lock that conflicts with ANALYZE), lock_timeout (a lock wait passed the refresh''s bound), failed (ANALYZE raised an error). Written only by the claimant holding the table''s turn, read by key.';

-- ---------------------------------------------------------------------------------------------
-- _wh_only_autovacuum_blocks_analyze: whether every session holding a lock that conflicts with ANALYZE on the table is
-- an autovacuum worker.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_only_autovacuum_blocks_analyze');

-- <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:OnlyAutovacuumBlocksAnalyze_IsTrueOnlyWhenEveryConflictingHolderIsAnAutovacuumWorkerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_only_autovacuum_blocks_analyze(p_table REGCLASS)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  -- The locks ANALYZE's ShareUpdateExclusiveLock conflicts with, held by another session of this database.
  WITH holders AS (
    SELECT DISTINCT l.pid
    FROM pg_locks l
    WHERE l.locktype = 'relation'
      AND l.database = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database())
      AND l.relation = p_table
      AND l.granted
      AND l.pid <> pg_backend_pid()
      AND l.mode IN ('ShareUpdateExclusiveLock', 'ShareLock', 'ShareRowExclusiveLock', 'ExclusiveLock', 'AccessExclusiveLock')
  )
  -- An autovacuum worker reports its type to a role allowed to read it. A role that may not still sees each session's
  -- database and user, and an autovacuum worker is the one backend connected to a database with no user.
  SELECT EXISTS (SELECT 1 FROM holders)
     AND NOT EXISTS (
       SELECT 1
       FROM holders h
       LEFT JOIN pg_stat_activity a ON a.pid = h.pid
       WHERE NOT COALESCE(
         a.backend_type = 'autovacuum worker'
           OR (a.backend_type IS NULL AND a.usesysid IS NULL AND a.datid IS NOT NULL),
         FALSE));
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_only_autovacuum_blocks_analyze(REGCLASS) IS
  'Whether some session holds a lock on the table that conflicts with ANALYZE and every such session is an autovacuum worker (661, #2611): the one holder the statistics refresh waits for, briefly, because PostgreSQL cancels it after deadlock_timeout.';

-- ---------------------------------------------------------------------------------------------
-- wh_refresh_stale_queue_statistics: copied from its last word (463), with the turn, the analyze and the back-off above.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_refresh_stale_queue_statistics');

-- <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:ClaimWork_StatisticsFromBeforeTheLoad_AcquiringIsStillPricedByTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_FreshStatistics_AnalyzesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhileAPeerHoldsTheTablesTurn_MovesOnWithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhileAReindexWaitsOnAnIdleTransaction_NeverWaits_AndBacksOffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhileAnotherSessionHoldsTheTable_GivesUpWithinTheLockTimeoutAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhenTheAnalyzeFails_BacksOff_AndSaysWhatFailedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_OnASmallTableThatChurns_AnalyzesItAtMostOncePerIntervalAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhenOnlyAutovacuumHoldsTheTable_WaitsForItBoundedByTheDeadlockTimeoutAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_AsADataOnlyRole_StillAnalyzesTheStaleTableAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:ClaimWork_WhileTheRefreshCannotAnalyze_NeverWaits_CountsTheSkip_AndWarnsOncePerBackoffAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_refresh_stale_queue_statistics()
RETURNS INTEGER
LANGUAGE plpgsql
-- The rights of the role that migrated the schema, which owns the tables (see 207's header). Pinned search_path: every
-- object below is schema-qualified, and nothing a caller puts on its path can be resolved in its place.
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  -- The tables claim_work acquires from or joins, each of which is empty between loads.
  v_tables CONSTANT REGCLASS[] := ARRAY[
    '__SCHEMA__.wh_outbox'::REGCLASS,
    '__SCHEMA__.wh_inbox'::REGCLASS,
    '__SCHEMA__.wh_inbox_state'::REGCLASS,
    '__SCHEMA__.wh_perspective_events'::REGCLASS,
    '__SCHEMA__.wh_active_streams'::REGCLASS
  ];
  -- The advisory lock's first key: this function. The second is the table's oid, unique per schema.
  c_lock_class CONSTANT INTEGER := hashtext('wh_refresh_stale_queue_statistics');
  -- 661: how long a table is left alone after its refresh analyzed it, skipped it or failed.
  c_backoff CONSTANT INTERVAL := INTERVAL '60 seconds';
  -- 661: the backstop for any lock ANALYZE (SKIP_LOCKED) still waits on.
  c_backstop CONSTANT TEXT := '1s';
  v_table REGCLASS;
  v_name TEXT;
  v_refreshed INTEGER := 0;
  v_reason TEXT;
  v_error TEXT;
  -- The caller's lock timeout, put back after each analyze.
  v_caller_lock_timeout TEXT := current_setting('lock_timeout');
BEGIN
  -- The healthy path: one catalog read per table, nothing stale, nothing locked.
  IF NOT EXISTS (SELECT 1 FROM unnest(v_tables) t WHERE __SCHEMA__._wh_queue_statistics_stale(t)) THEN
    RETURN 0;
  END IF;

  FOREACH v_table IN ARRAY v_tables LOOP
    CONTINUE WHEN NOT __SCHEMA__._wh_queue_statistics_stale(v_table);
    v_name := (SELECT c.relname FROM pg_class c WHERE c.oid = v_table);
    -- 661: a table analyzed, skipped or failed within the interval is left alone until its retry, by every claimant.
    CONTINUE WHEN EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_queue_statistics_backoff b WHERE b.table_name = v_name AND b.retry_after > clock_timestamp());
    -- 661: one claimant analyzes a table at a time, and the rest move on rather than wait for it.
    CONTINUE WHEN NOT pg_try_advisory_xact_lock(c_lock_class, v_table::OID::INTEGER);
    -- The statistics reads above are cached for the transaction; a peer may have analyzed or backed off since.
    PERFORM pg_stat_clear_snapshot();
    CONTINUE WHEN NOT __SCHEMA__._wh_queue_statistics_stale(v_table);
    CONTINUE WHEN EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_queue_statistics_backoff b WHERE b.table_name = v_name AND b.retry_after > clock_timestamp());
    IF NOT pg_has_role((SELECT c.relowner FROM pg_class c WHERE c.oid = v_table), 'MEMBER') THEN
      -- ANALYZE would skip it with a warning of its own; this one says what to do about it.
      RAISE WARNING 'whizbang: the planner statistics of % are stale and % cannot analyze it; the tables must be owned by the role that migrates the schema (#1349)',
        v_table, current_user;
      CONTINUE;
    END IF;

    v_reason := NULL;
    BEGIN
      -- Local to this block: an exception rolls the setting back with the block, and the success path restores it.
      PERFORM set_config('lock_timeout', c_backstop, TRUE);
      EXECUTE format('ANALYZE (SKIP_LOCKED) %s', v_table);
      -- A completed ANALYZE holds the table's ShareUpdateExclusiveLock until the claim commits; a skipped one took none.
      IF NOT EXISTS (
          SELECT 1 FROM pg_locks l
          WHERE l.locktype = 'relation' AND l.relation = v_table AND l.pid = pg_backend_pid()
            AND l.mode = 'ShareUpdateExclusiveLock' AND l.granted) THEN
        IF __SCHEMA__._wh_only_autovacuum_blocks_analyze(v_table) THEN
          -- 207 (#1349): an autovacuum worker stuck in its vacuum is cancelled once this has waited deadlock_timeout.
          PERFORM set_config('lock_timeout',
            ((EXTRACT(EPOCH FROM current_setting('deadlock_timeout')::INTERVAL) * 1000)::BIGINT + 1000)::TEXT || 'ms', TRUE);
          EXECUTE format('ANALYZE %s', v_table);
        ELSE
          v_reason := 'lock_not_available';
        END IF;
      END IF;
      PERFORM set_config('lock_timeout', v_caller_lock_timeout, TRUE);
    EXCEPTION
      WHEN lock_not_available THEN
        v_reason := 'lock_timeout';
        v_error := SQLERRM;
      WHEN OTHERS THEN
        v_reason := 'failed';
        v_error := SQLERRM;
    END;

    -- 661: an analyze backs the table off too. A small table that churns reads as stale again right after every analyze,
    -- so without this it was analyzed on nearly every claim; with it, at most once per interval across every claimant.
    INSERT INTO __SCHEMA__.wh_queue_statistics_backoff AS b (table_name, retry_after, reason, backed_off_at)
    VALUES (v_name, clock_timestamp() + c_backoff, COALESCE(v_reason, 'analyzed'), clock_timestamp())
    ON CONFLICT (table_name) DO UPDATE
      SET retry_after = EXCLUDED.retry_after, reason = EXCLUDED.reason, backed_off_at = EXCLUDED.backed_off_at;
    IF v_reason IS NULL THEN
      v_refreshed := v_refreshed + 1;
      RAISE LOG 'whizbang: analyzed % because its statistics were stale (#1349)', v_table;
      CONTINUE;
    END IF;
    RAISE WARNING USING
      MESSAGE = 'whizbang.queue_statistics_refresh_skipped',
      DETAIL = v_name || '=' || v_reason,
      HINT = format('The planner statistics of %s are stale and were not refreshed: %s. Claims go on with the statistics they have; the refresh is tried again after %s (#2611).',
        v_name,
        CASE v_reason
          WHEN 'lock_not_available' THEN 'another session holds a lock that conflicts with ANALYZE (a concurrent index build, or a transaction that locked the table)'
          WHEN 'lock_timeout' THEN 'a lock wait passed its bound (' || v_error || ')'
          ELSE 'ANALYZE failed (' || v_error || ')'
        END,
        EXTRACT(EPOCH FROM c_backoff)::INTEGER || ' seconds');
  END LOOP;
  RETURN v_refreshed;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_refresh_stale_queue_statistics() IS
  'Analyzes the queue tables whose statistics are stale (207, #1349), as the role that migrated the schema (SECURITY DEFINER), and never waits on another claimant or a conflicting lock (661, #2611): one claimant per table under a transaction-scoped advisory lock taken with pg_try_advisory_xact_lock, ANALYZE (SKIP_LOCKED), a wait of deadlock_timeout plus one second only when autovacuum alone holds the table, and a 60 second back-off after an analyze, a skip or a failure, a skip or failure raised once as the whizbang.queue_statistics_refresh_skipped warning. Returns how many tables it analyzed. claim_work calls it before acquiring.';
