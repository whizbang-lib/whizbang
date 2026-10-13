-- Migration: 207_ClaimRefreshesStaleQueueStatistics.sql
-- Date: 2026-10-12
-- Description: A claim analyzes a queue table whose statistics describe it as it was before a load (#1349).
--
--              The queue tables are empty between loads, and that is when autovacuum analyzes them. Autovacuum runs
--              one worker per table, and under a load that worker can be stuck in its vacuum: a btree vacuum needs a
--              cleanup lock on each leaf page, and the claim scans that keep pinning those pages never stop. The
--              worker never reaches its analyze. Measured on a deployed service: a perspective-event table analyzed
--              while empty (reltuples 0, one page) carried tens of thousands of pending rows an hour later, and the
--              vacuum had waited on BufferPin for that whole hour.
--
--              On those statistics every candidate index looks free. The planner captured each selected stream by
--              walking the whole pending set in event-id order and filtering on the stream, once per stream, so one
--              claim_work call read the backlog times the batch and ran past the caller's command timeout. The call
--              was canceled, nothing was claimed, the backlog stayed the same size, and every later call met it
--              again. Reproduced on a container: 180,000 pending rows, one claim, 19 ms on fresh statistics and more
--              than eight minutes on statistics taken from the table while it held one page and no rows
--              (ClaimWorkStaleStatisticsTests).
--
--              No SQL shape survives that: once the planner believes the table is empty, a plan that reads every
--              matching row before sorting looks as cheap as one that walks an index and stops. So the claim keeps
--              the statistics true instead. Before it acquires anything, claim_work asks
--              wh_refresh_stale_queue_statistics whether any queue table has changed far more since its last analyze
--              than autovacuum lets a healthy table drift: more than 1,000 rows and more than four times what the
--              planner believes it holds. Autovacuum analyzes at 50 rows plus a tenth of the table, so on a healthy
--              database it always gets there first and this never fires; the check is a catalog read per table.
--
--              A stale table is analyzed by one claimant at a time: a transaction-scoped advisory lock per table
--              (transaction-scoped because a session lock is unsafe behind a pooler), and a claimant that waited
--              re-reads the statistics after the holder committed and analyzes only what is still stale. ANALYZE
--              needs no cleanup lock. It does take the lock a running vacuum of the same table holds, and Postgres
--              cancels an autovacuum that is not preventing wraparound once another session has waited on it for
--              deadlock_timeout, so the stuck worker is ended rather than waited out. Everything runs under a five
--              second lock timeout: a claimant that cannot get there in time logs a warning, keeps its statistics as
--              they are, and claims; it is never worse off than before.
--
--              Only a table's owner may analyze it, and a consumer can run its services as a role with data rights
--              only, keeping the schema's owner on the -init connection (DataOnlyRoleE2ETests). The -init connection
--              is schema-only and never used once a service is up, so the refresh does not borrow it: it is SECURITY
--              DEFINER, created by the role that migrates the schema and so owns the tables, and runs ANALYZE with
--              that role's rights whatever role the claim runs as. What it can do with them is fixed: analyze the
--              tables listed below, and only when they are stale. search_path is pinned to pg_catalog and every
--              object it names is schema-qualified. EXECUTE is left with PUBLIC, as on every framework function:
--              claim_work calls it as the claiming role, and revoking it would fail every claim of a role that was
--              granted only the defaults. A stale table that even the owner cannot analyze (the tables were
--              reassigned to another role) is reported with a warning on every poll rather than skipped silently.
--
-- Dependencies: 204 (claim_work, which calls this before its acquisitions)
-- Objects: _wh_queue_statistics_stale, wh_refresh_stale_queue_statistics
-- Constants: none.

-- ---------------------------------------------------------------------------------------------
-- _wh_queue_statistics_stale: whether the planner's statistics for one table are past trusting.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_queue_statistics_stale');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:ClaimWork_StatisticsFromBeforeTheLoad_AcquiringIsStillPricedByTheBatchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_queue_statistics_stale(p_table REGCLASS)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  -- Rows changed since the last analyze, against what the planner believes the table holds. reltuples is -1 for a
  -- table never analyzed, which the planner sizes from its pages instead, so it counts as zero here.
  SELECT pg_stat_get_mod_since_analyze(c.oid) > GREATEST(1000, 4 * GREATEST(c.reltuples, 0)::BIGINT)
  FROM pg_class c
  WHERE c.oid = p_table;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_queue_statistics_stale(REGCLASS) IS
  'Whether a queue table has changed far more since its last analyze than autovacuum lets a healthy table drift (more '
  'than 1,000 rows and more than four times what the planner believes it holds) (207).';

-- ---------------------------------------------------------------------------------------------
-- wh_refresh_stale_queue_statistics: analyze the queue tables whose statistics are stale.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_refresh_stale_queue_statistics');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:ClaimWork_StatisticsFromBeforeTheLoad_AcquiringIsStillPricedByTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_FreshStatistics_AnalyzesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhileAPeerRefreshesTheTable_WaitsAndDoesNotAnalyzeAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_WhileAnotherSessionHoldsTheTable_GivesUpWithinTheLockTimeoutAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:RefreshStaleQueueStatistics_AsADataOnlyRole_StillAnalyzesTheStaleTableAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_refresh_stale_queue_statistics()
RETURNS INTEGER
LANGUAGE plpgsql
-- The rights of the role that migrated the schema, which owns the tables (see the header). Pinned search_path: every
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
    '__SCHEMA__.wh_receptor_processing'::REGCLASS,
    '__SCHEMA__.wh_active_streams'::REGCLASS
  ];
  -- The advisory lock's first key: this function. The second is the table's oid, unique per schema.
  c_lock_class CONSTANT INTEGER := hashtext('wh_refresh_stale_queue_statistics');
  v_table REGCLASS;
  v_refreshed INTEGER := 0;
  -- The caller's lock timeout, put back once the refresh is done.
  v_caller_lock_timeout TEXT := current_setting('lock_timeout');
BEGIN
  -- The healthy path: one catalog read per table, nothing stale, nothing locked.
  IF NOT EXISTS (SELECT 1 FROM unnest(v_tables) t WHERE __SCHEMA__._wh_queue_statistics_stale(t)) THEN
    RETURN 0;
  END IF;

  BEGIN
    -- Local to this block: an exception rolls the setting back with the block, and the success path restores it.
    PERFORM set_config('lock_timeout', '5s', TRUE);
    FOREACH v_table IN ARRAY v_tables LOOP
      IF __SCHEMA__._wh_queue_statistics_stale(v_table) THEN
        -- One claimant analyzes a table at a time; the rest wait for it, then find it fresh.
        PERFORM pg_advisory_xact_lock(c_lock_class, v_table::OID::INTEGER);
        -- The statistics reads above are cached for the transaction; a peer may have analyzed while this waited.
        PERFORM pg_stat_clear_snapshot();
        IF __SCHEMA__._wh_queue_statistics_stale(v_table) THEN
          IF NOT pg_has_role((SELECT c.relowner FROM pg_class c WHERE c.oid = v_table), 'MEMBER') THEN
            -- ANALYZE would skip it with a warning of its own; this one says what to do about it.
            RAISE WARNING 'whizbang: the planner statistics of % are stale and % cannot analyze it; the tables must be owned by the role that migrates the schema (#1349)',
              v_table, current_user;
            CONTINUE;
          END IF;
          EXECUTE format('ANALYZE %s', v_table);
          v_refreshed := v_refreshed + 1;
          RAISE LOG 'whizbang: analyzed % because its statistics were stale (#1349)', v_table;
        END IF;
      END IF;
    END LOOP;
    PERFORM set_config('lock_timeout', v_caller_lock_timeout, TRUE);
  EXCEPTION WHEN lock_not_available THEN
    RAISE WARNING 'whizbang: could not refresh the planner statistics of % within 5 s (%); claiming on the statistics it has',
      v_table, SQLERRM;
  END;
  RETURN v_refreshed;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_refresh_stale_queue_statistics() IS
  'Analyzes the queue tables whose statistics are stale (207, #1349), as the role that migrated the schema (SECURITY DEFINER), one claimant per table at a time under a '
  'transaction-scoped advisory lock and a five second lock timeout. Returns how many tables it analyzed. claim_work '
  'calls it before acquiring, so a backlog that arrived after the last analyze is planned as the backlog it is.';
