-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 661_AClaimNeverWaitsToRefreshStatistics.sql (#2611): 463's definition of the statistics refresh comes
-- back verbatim, and the back-off table and the autovacuum helper go. The back-off rows only said when a refresh may be
-- tried again, so dropping them loses nothing a later claim needs.

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

SELECT __SCHEMA__.drop_all_overloads('_wh_only_autovacuum_blocks_analyze');
DROP TABLE IF EXISTS __SCHEMA__.wh_queue_statistics_backoff;
