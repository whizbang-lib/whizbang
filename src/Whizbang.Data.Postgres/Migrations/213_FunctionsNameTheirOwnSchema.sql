-- Migration: 213_FunctionsNameTheirOwnSchema.sql
-- Date: 2026-10-19
-- Description: The run-time functions that found perspective and framework tables through current_schema() now
--              name their own schema, so on a service whose EF Core context owns a named schema they work there
--              instead of in public (README rule 14).
--
--              current_schema() is the CALLER's search_path. These functions run on pooled runtime connections
--              (the maintenance worker, the retention sweeps, the table-rewrite scan), where the path is the
--              default, so on a named-schema EF Core service they looked for their tables in public: the reaps
--              removed nothing, the retention preview counted nothing, and the rewrite scan offered nothing.
--              Each now writes its schema as replace('__SCHEMA__', '"', ''), which is the bare schema under both
--              drivers' substitutions and compares equal to information_schema.table_schema.
--
--              Every function below is its latest definition copied whole (rule 5) with only that change:
--              wh_tables_needing_rewrite (089), count_perspective_retention_backlog (104),
--              collect_perspective_row_reap_targets (113), reap_enrolled_perspective_rows,
--              reap_perspective_row_caps, cascade_delete_perspective_rows and perform_maintenance (211). The
--              shipped files are unchanged; a re-run of any of them re-runs this file after it (rule 11).
--
--              The migrations' own guards (046 and others compare table_schema = current_schema()) need no
--              redefinition: the EF Core runner sets search_path to the migrated schema for each migration,
--              and the Dapper driver's schema is current_schema() already.
-- Dependencies: 089, 104, 113, 211 (the definitions copied here)
-- Objects: wh_tables_needing_rewrite, count_perspective_retention_backlog, collect_perspective_row_reap_targets, reap_enrolled_perspective_rows, reap_perspective_row_caps, cascade_delete_perspective_rows, perform_maintenance
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>operations/infrastructure/migrations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:RewriteCandidatesAreMeasuredInTheContextsSchemaAsync</tests>
-- What the maintenance worker should rewrite RIGHT NOW.
--
-- Every candidate is re-measured here rather than trusted from the request list. That is what
-- makes the mechanism safe to leave switched on:
--   * a migration whose content hash changed replays and re-requests a table that was already
--     rewritten; re-measuring returns nothing, so no pointless multi-minute lock;
--   * a fresh deployment applies the drop against an empty table and never had residue at all;
--   * a table that churns back into bloat is offered again without anyone re-requesting it.
--
-- Combines both sources: explicitly requested (a migration recorded it) and detected (ratio over
-- the configured threshold). Expected width comes from pg_stats.avg_width, which autoanalyze
-- already maintains, so this is a catalog read rather than a table scan.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_tables_needing_rewrite()
RETURNS TABLE (table_name TEXT, bloat_ratio NUMERIC, requested BOOLEAN)
LANGUAGE plpgsql
AS $$
DECLARE
  v_threshold NUMERIC;
  v_min_rows  BIGINT;
  v_requested TEXT[];
BEGIN
  SELECT COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_bloat_threshold'), '3.0')::NUMERIC
    INTO v_threshold;
  SELECT COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'table_rewrite_min_rows'), '1000')::BIGINT
    INTO v_min_rows;
  SELECT string_to_array(NULLIF(COALESCE((SELECT setting_value FROM __SCHEMA__.wh_settings WHERE setting_key = 'pending_table_rewrites'), ''), ''), ',')
    INTO v_requested;

  RETURN QUERY
  -- pg_class.reltuples, NOT pg_stat_user_tables.n_live_tup: the stats collector is
  -- asynchronous, so n_live_tup can lag recent churn and both halves of the rewrite
  -- decision (this candidate ratio, and the post-rewrite re-measure that must beat it)
  -- then race the collector. reltuples is written by VACUUM/ANALYZE transactionally in
  -- the catalog, so both measures read the same synchronous source.
  SELECT c.relname::TEXT,
         round((pg_relation_size(c.oid)::NUMERIC / c.reltuples::NUMERIC) / GREATEST(w.expected, 1), 2),
         (v_requested IS NOT NULL AND c.relname = ANY(v_requested))
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN LATERAL (
    SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
    FROM pg_stats s
    WHERE s.schemaname = n.nspname AND s.tablename = c.relname
  ) w ON TRUE
  WHERE n.nspname = replace('__SCHEMA__', '"', '')
    AND c.relkind = 'r'
    AND c.relname LIKE 'wh\_%'
    AND c.reltuples >= v_min_rows
    AND ((pg_relation_size(c.oid)::NUMERIC / c.reltuples::NUMERIC) / GREATEST(w.expected, 1)) >= v_threshold
  ORDER BY 2 DESC;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_tables_needing_rewrite IS
'Tables whose heap is disproportionate to their live rows, re-measured on every call so an already-rewritten or never-bloated table is never offered. Union of migration-requested and threshold-detected.';

-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetentionAdoptionSafetyTests.cs:NewlyEnrolled_ReportsBacklogButRemovesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:RetentionBacklogCountsTheContextsOwnRowsAsync</tests>
-- Counts what enforcement WOULD remove for an enrolled perspective, without removing it. This is
-- the number an operator reads before acknowledging, and it is deliberately a separate function so
-- reporting can run on a perspective that is not yet enforcing.
CREATE OR REPLACE FUNCTION __SCHEMA__.count_perspective_retention_backlog(p_clr_type_name TEXT)
RETURNS BIGINT AS $$
DECLARE
  v_reg   RECORD;
  v_count BIGINT := 0;
BEGIN
  SELECT table_name, row_ttl_seconds, row_max_age_seconds
    INTO v_reg
    FROM __SCHEMA__.wh_perspective_registry
   WHERE clr_type_name = p_clr_type_name AND row_retention_enrolled;

  IF NOT FOUND THEN
    RETURN 0;
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = replace('__SCHEMA__', '"', '') AND table_name = v_reg.table_name) THEN
    RETURN 0;
  END IF;

  -- Same ladder as the sweep, counting instead of deleting. Kept in the same shape deliberately:
  -- a preview that does not match what enforcement does is worse than no preview.
  EXECUTE format(
    'SELECT COUNT(*) FROM %I.%I WHERE
         (expires_at IS NOT NULL AND expires_at < NOW())
      OR (expires_at IS NULL AND $1 IS NOT NULL
          AND updated_at < NOW() - make_interval(secs => $1))
      OR ($2 IS NOT NULL
          AND created_at < NOW() - make_interval(secs => $2))',
    replace('__SCHEMA__', '"', ''), v_reg.table_name)
  INTO v_count
  USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds;

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:ReapTargetsAreCollectedFromTheContextsSchemaAsync</tests>
-- (1b) Collect re-created from 111 with the cap side ack-gated: offer == destroy, always.
CREATE OR REPLACE FUNCTION __SCHEMA__.collect_perspective_row_reap_targets(
  p_clr_type_names TEXT[],
  p_per_table_limit INTEGER DEFAULT 500)
RETURNS TABLE(o_clr_type_name TEXT, o_table_name TEXT, o_row_id UUID, o_scope JSONB, o_data JSONB, o_reason TEXT) AS $$
DECLARE
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN;
  END IF;

  FOR v_reg IN
    SELECT r.clr_type_name, r.table_name, r.row_ttl_seconds, r.row_max_age_seconds,
           r.row_cap_per_scope, r.row_cap_scope_key, r.retention_enforcement_acknowledged
    FROM __SCHEMA__.wh_perspective_registry r
    WHERE r.row_retention_enrolled AND r.clr_type_name = ANY(p_clr_type_names)
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = replace('__SCHEMA__', '"', '') AND table_name = v_reg.table_name);

    -- BOTH sides gate on acknowledgement now (the cap sweep gained the gate in this migration).
    CONTINUE WHEN NOT v_reg.retention_enforcement_acknowledged;

    -- Expiry ladder.
    RETURN QUERY EXECUTE format(
      'SELECT %L::TEXT, %L::TEXT, id, scope, data, ''ttl''::TEXT FROM %I.%I WHERE
            ((expires_at IS NOT NULL AND expires_at < NOW())
         OR (expires_at IS NULL AND $1 IS NOT NULL
             AND updated_at < NOW() - make_interval(secs => $1))
         OR ($2 IS NOT NULL
             AND created_at < NOW() - make_interval(secs => $2)))
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
       LIMIT $3',
      v_reg.clr_type_name, v_reg.table_name, replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name)
    USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds, p_per_table_limit;

    -- Cap overflow.
    IF v_reg.row_cap_per_scope IS NOT NULL THEN
      v_partition := CASE
        WHEN v_reg.row_cap_scope_key IS NULL THEN '1'
        ELSE format('scope ->> %L', v_reg.row_cap_scope_key)
      END;

      RETURN QUERY EXECUTE format(
        'SELECT %L::TEXT, %L::TEXT, t.id, t.scope, t.data, ''cap''::TEXT FROM %I.%I t
         WHERE t.id IN (
           SELECT id FROM (
             SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
             FROM %I.%I
           ) ranked WHERE ranked.rn > $1)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = t.id AND h.hold_until > NOW())
         LIMIT $2',
        v_reg.clr_type_name, v_reg.table_name, replace('__SCHEMA__', '"', ''), v_reg.table_name,
        v_partition, replace('__SCHEMA__', '"', ''), v_reg.table_name,
        replace('__SCHEMA__', '"', ''), v_reg.table_name)
      USING v_reg.row_cap_per_scope, p_per_table_limit;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

-- Different parameter counts have existed under this name; clear them so the definition below is the only one.
SELECT __SCHEMA__.drop_all_overloads('reap_enrolled_perspective_rows');

-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EnrolledRowReaperSqlTests.cs:SlidingRule_ReapsIdleRowsWithNoStampedExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs:Sweep_JournalsItsVictims_InTheSameStatementAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:TtlReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_enrolled_perspective_rows(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start        TIMESTAMPTZ := clock_timestamp();
  v_rows         INTEGER := 0;
  v_deleted      INTEGER := 0;
  v_debug_mode   BOOLEAN := FALSE;
  v_reg          RECORD;
  v_pending      INTEGER := 0;
  v_victims      UUID[];
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN QUERY SELECT
      'reap_enrolled_perspective_rows'::TEXT, 0,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
      'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  FOR v_reg IN
    SELECT table_name, row_ttl_seconds, row_max_age_seconds
    FROM __SCHEMA__.wh_perspective_registry
    WHERE row_retention_enrolled AND retention_enforcement_acknowledged
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = replace('__SCHEMA__', '"', '') AND table_name = v_reg.table_name);

    -- The ladder (see 102/111). Victims are journaled in the same statement so the group cascade
    -- sees exactly what died, never an approximation of what should have.
    EXECUTE format(
      'WITH del AS (
         DELETE FROM %I.%I WHERE id IN (
           SELECT id FROM %I.%I WHERE
                ((expires_at IS NOT NULL AND expires_at < NOW())
             OR (expires_at IS NULL AND $1 IS NOT NULL
                 AND updated_at < NOW() - make_interval(secs => $1))
             OR ($2 IS NOT NULL
                 AND created_at < NOW() - make_interval(secs => $2)))
             AND NOT EXISTS (
               SELECT 1 FROM %I.wh_perspective_row_hold h
               WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
           LIMIT $3)
         RETURNING id),
       journaled AS (
         INSERT INTO %I.wh_row_eviction_journal (table_name, row_id)
         SELECT %L, id FROM del
         ON CONFLICT (table_name, row_id) DO NOTHING)
       SELECT ARRAY(SELECT id FROM del)',
      replace('__SCHEMA__', '"', ''), v_reg.table_name, replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name)
    INTO v_victims
    USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds, p_batch_size;

    -- 1483: counted from the rows deleted (the journal may already hold an id), and their cursors
    -- and all but their latest snapshots go with them.
    v_deleted := cardinality(v_victims);
    v_rows := v_rows + v_deleted;
    PERFORM __SCHEMA__._wh_evict_perspective_row_companions(v_reg.table_name, v_victims);
    IF v_deleted >= p_batch_size THEN
      v_pending := v_pending + 1;
    END IF;

    -- Self-cleaning: a hold whose row no longer exists holds nothing.
    EXECUTE format(
      'DELETE FROM %I.wh_perspective_row_hold h
       WHERE h.table_name = %L
         AND NOT EXISTS (SELECT 1 FROM %I.%I t WHERE t.id = h.row_id)',
      replace('__SCHEMA__', '"', ''), v_reg.table_name, replace('__SCHEMA__', '"', ''), v_reg.table_name);
  END LOOP;

  RETURN QUERY SELECT
    'reap_enrolled_perspective_rows'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_pending > 0
      THEN format('ok (draining: %s perspective(s) hit the batch bound)', v_pending)
      ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

-- Different parameter counts have existed under this name; clear them so the definition below is the only one.
SELECT __SCHEMA__.drop_all_overloads('reap_perspective_row_caps');

-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowCapSqlTests.cs:Cap_KeepsNewestPerScope_AndEvictsTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:CapReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_perspective_row_caps(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start      TIMESTAMPTZ := clock_timestamp();
  v_rows       INTEGER := 0;
  v_deleted    INTEGER := 0;
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
  v_pending    INTEGER := 0;
  v_victims    UUID[];
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN QUERY SELECT
      'reap_perspective_row_caps'::TEXT, 0,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
      'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  FOR v_reg IN
    SELECT table_name, row_cap_per_scope, row_cap_scope_key
    FROM __SCHEMA__.wh_perspective_registry
    WHERE row_retention_enrolled AND retention_enforcement_acknowledged AND row_cap_per_scope IS NOT NULL
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = replace('__SCHEMA__', '"', '') AND table_name = v_reg.table_name);

    v_partition := CASE
      WHEN v_reg.row_cap_scope_key IS NULL THEN '1'
      ELSE format('scope ->> %L', v_reg.row_cap_scope_key)
    END;

    EXECUTE format(
      'WITH del AS (
         DELETE FROM %I.%I WHERE id IN (
           SELECT id FROM (
             SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
             FROM %I.%I
           ) ranked WHERE ranked.rn > $1
           LIMIT $2)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
         RETURNING id),
       journaled AS (
         INSERT INTO %I.wh_row_eviction_journal (table_name, row_id)
         SELECT %L, id FROM del
         ON CONFLICT (table_name, row_id) DO NOTHING)
       SELECT ARRAY(SELECT id FROM del)',
      replace('__SCHEMA__', '"', ''), v_reg.table_name, v_partition, replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name,
      replace('__SCHEMA__', '"', ''), v_reg.table_name)
    INTO v_victims
    USING v_reg.row_cap_per_scope, p_batch_size;

    -- 1483: counted from the rows deleted, and their cursors and all but their latest
    -- snapshots go with them.
    v_deleted := cardinality(v_victims);
    v_rows := v_rows + v_deleted;
    PERFORM __SCHEMA__._wh_evict_perspective_row_companions(v_reg.table_name, v_victims);
    IF v_deleted >= p_batch_size THEN
      v_pending := v_pending + 1;
    END IF;
  END LOOP;

  RETURN QUERY SELECT
    'reap_perspective_row_caps'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_pending > 0
      THEN format('ok (draining: %s perspective(s) hit the batch bound)', v_pending)
      ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_perspective_row_caps(INTEGER) IS
  'Evicts rows ranked beyond a perspective''s per-scope cap, ordered by business time. Gated on '
  'retention_enforcement_acknowledged (a declared cap REPORTS before it removes) and batched so a '
  'first sweep over a large backlog drains across cycles. Holds honored; victims journaled; their '
  'cursors and all but their latest snapshots removed with them.';

-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs:CascadeDelete_HonorsHolds_AndRequeueIsIdempotentAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Cascade_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.cascade_delete_perspective_rows(
  p_table_name TEXT,
  p_row_ids UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_deleted INTEGER := 0;
  v_victims UUID[];
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = replace('__SCHEMA__', '"', '') AND table_name = p_table_name) THEN
    RETURN 0;
  END IF;

  EXECUTE format(
    'WITH del AS (
       DELETE FROM %I.%I WHERE id = ANY($1)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
       RETURNING id)
     SELECT ARRAY(SELECT id FROM del)',
    replace('__SCHEMA__', '"', ''), p_table_name, replace('__SCHEMA__', '"', ''), p_table_name)
  INTO v_victims
  USING p_row_ids;

  -- 1483: the cascaded rows' cursors and all but their latest snapshots go with them.
  v_deleted := cardinality(v_victims);
  PERFORM __SCHEMA__._wh_evict_perspective_row_companions(p_table_name, v_victims);
  RETURN v_deleted;
END;
$$ LANGUAGE plpgsql;

-- <docs>operations/infrastructure/maintenance</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/TtlReapDrivesFromRegistryTests.cs:TheRowExpiryReap_SelectsItsTablesFromTheRegistry_NotTheCatalogAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:StampedExpiryReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/CollectiveOrderingHeadSqlTests.cs:Maintenance_PrunesOnlyHeadsIdlePastTheRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PurgesStuckInboxMessages_OlderThanRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesRecentStuckInboxMessages_WithinRetentionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesLeasedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MaintenanceTests.cs:PerformMaintenance_PreservesClaimedInboxMessages_EvenIfOldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.cs:NoFunctionLocksTheWorkTablesOutOfCanonicalOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBodyReaperSqlTests.cs:Reap_SnapshotEarlierInTheStream_DoesNotCoverTheBodyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NamedSchemaMigrationReplayTests.cs:MaintenanceReapsTheContextsOwnExpiredRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.perform_maintenance()
RETURNS TABLE(
  task_name TEXT,
  rows_affected BIGINT,
  duration_ms DOUBLE PRECISION,
  status TEXT
) AS $$
DECLARE
  v_start TIMESTAMPTZ;
  v_rows BIGINT;
  v_dedup_retention_days INTEGER;
  v_stuck_inbox_retention_days INTEGER;
  v_debug_mode BOOLEAN;
  v_abandoned_stream_hours INTEGER;
  v_ephemeral_grace_seconds INTEGER;
  v_per_table TEXT;
  v_per_deleted BIGINT;
  v_per_victims UUID[];
  v_instance_eviction_retention_hours INTEGER;
  v_dead_letter_retention_days INTEGER;
  v_orphan_grace_hours INTEGER;
  v_ordering_head_retention_days INTEGER;
BEGIN
  -- Read debug_mode flag once for the cycle. When true, the complete_* functions
  -- retain rows for forensics with processed_at stamped, this maintenance pass
  -- MUST skip purging those rows or the debug-mode design breaks.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug_mode;

  -- Grace period before an owner-less active-stream row is purged (Task 6). Configurable via
  -- wh_settings; default 1 hour preserves the transient-NULL race window between
  -- cleanup_stale_instances nulling the owner and the next claim cycle re-assigning it.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'abandoned_stream_hours'),
    1
  ) INTO v_abandoned_stream_hours;

  -- Rewind grace window (seconds): an ephemeral body is retained this long AFTER consumption so an
  -- out-of-order straggler can still rewind through it (events arrive out of order in a short window).
  -- Configurable via wh_settings; default 300s. A per-type [Ephemeral(RewindGrace)] override lands later.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_rewind_grace_seconds'),
    300
  ) INTO v_ephemeral_grace_seconds;

  -- Retention for wh_instance_evictions tombstones (Task 10, migration 106/107). The tombstone only
  -- needs to outlive a paused instance's resumption window, not the fleet's lifetime. Default 24
  -- hours is generous against any realistic pause while still bounding the table.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'instance_eviction_retention_hours'),
    24
  ) INTO v_instance_eviction_retention_hours;

  -- ========================================
  -- Task 1: Purge completed outbox messages
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox WHERE processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_outbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 2: Purge completed inbox messages
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    -- 162: processed_at is work state. The DELETE stays on wh_inbox because that is the row being
    -- removed, and the state row goes with it through ON DELETE CASCADE; the predicate reads the
    -- state table. USING keeps this one statement so the GET DIAGNOSTICS below still measures it.
    DELETE FROM __SCHEMA__.wh_inbox i
    USING __SCHEMA__.wh_inbox_state ist
    WHERE ist.message_id = i.message_id AND ist.processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 5: Purge ancient stuck inbox messages
  -- ========================================
  -- POSITION: this sweep sits beside Task 2 because both DELETE from wh_inbox, and a transaction
  -- that locks a table, moves on, and comes back to it can deadlock against a sibling that took
  -- the two tables the other way round. Every pod runs this function on the same tick, so the
  -- sibling here is another copy of this very function: one holds a wh_perspective_events row and
  -- wants wh_active_streams, the other holds wh_active_streams and wants wh_perspective_events.
  -- Each table is therefore visited exactly once, in the canonical order wh_outbox, wh_inbox,
  -- wh_inbox_state, wh_perspective_events, wh_active_streams. The report is looked up by task
  -- name everywhere it is read, never by position, so moving a block is free.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stuck_inbox_retention_days'),
    7
  ) INTO v_stuck_inbox_retention_days;

  v_start := clock_timestamp();
  -- 162: same shape as the purge above. Every term of this predicate is on the state table,
  -- received_at as a write-once copy, so the sweep never reads the wide message row to decide.
  DELETE FROM __SCHEMA__.wh_inbox i
  USING __SCHEMA__.wh_inbox_state ist
  WHERE ist.message_id = i.message_id
    AND ist.processed_at IS NULL
    AND ist.lease_expiry IS NULL
    AND ist.instance_id IS NULL
    AND ist.received_at < NOW() - (v_stuck_inbox_retention_days || ' days')::INTERVAL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_stuck_inbox'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 3: Purge completed perspective events
  -- ========================================
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NOT NULL;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_completed_perspective_events'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 12: Reap orphaned perspective-event rows (issue #687)
  -- ========================================
  -- POSITION: beside Task 3 for the same reason Task 5 sits beside Task 2 -- both DELETE from
  -- wh_perspective_events, and this one used to run after the wh_active_streams purge, which put
  -- the two tables in both orders inside one transaction. Nothing here depends on the blocks it
  -- moved past: the grace hours are read from wh_settings by this block itself, the predicate
  -- reads wh_event_store which this function never writes, and Task 13 still runs after it and
  -- still sees v_orphan_grace_hours set.
  -- A wh_perspective_events row whose source event no longer exists in wh_event_store is
  -- UNPROJECTABLE forever: the drainer's inner join (get_stream_events) returns nothing, so the
  -- row is re-claimed every cycle with attempts climbing and no error, livelocking the pipeline
  -- (root cause of #679). These arise when an event is reaped/purged after its perspective work
  -- was created. Deleting is correct: the event is gone, so there is nothing to project and the
  -- projection cursor never advanced past the row. Age-bounded on created_at so a row whose event
  -- write has simply not committed yet (a legitimate in-flight window) is never reaped out from
  -- under itself. Not gated on debug_mode: this is unprojectable garbage, not forensic evidence,
  -- and leaving it keeps the pipeline wedged.
  v_start := clock_timestamp();
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'orphan_perspective_grace_hours'),
    1
  ) INTO v_orphan_grace_hours;
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  WHERE pe.processed_at IS NULL
    AND pe.created_at < NOW() - (v_orphan_grace_hours * INTERVAL '1 hour')
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = pe.event_id
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'reap_orphaned_perspective_events'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 4: Purge old deduplication entries
  -- ========================================
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'),
    30
  ) INTO v_dedup_retention_days;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dead_letter_retention_days'),
    7
  ) INTO v_dead_letter_retention_days;

  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_message_deduplication
  WHERE first_seen_at < NOW() - (v_dedup_retention_days || ' days')::INTERVAL;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_old_deduplication'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 6: Purge abandoned active-stream rows
  -- ========================================
  -- Two branches, both safe because UUIDv7 IDs never repeat (a missing
  -- wh_service_instances row means the instance is fully gone):
  --
  --   (a) Rows whose assigned_instance_id is non-NULL but points at a
  --       wh_service_instances row that no longer exists. After the
  --       heartbeat-recency liveness check in claim_orphaned_inbox /
  --       claim_orphaned_outbox (migrations 024/025) these are already
  --       non-blocking; the cleanup just bounds accumulation. No age guard.
  --
  --   (b) Rows whose assigned_instance_id IS NULL AND whose last_activity_at
  --       is older than the grace period. cleanup_stale_instances nulls the
  --       assigned_instance_id in the same tick where it deletes the dead
  --       wh_service_instances row, so without this branch every dead
  --       instance leaves its streams in the table forever (production forensic:
  --       tens of thousands of rows accumulated, 99% with NULL owner). The age
  --       guard preserves the legitimate transient-NULL race window between
  --       cleanup_stale_instances nulling the field and the next
  --       claim_orphaned_* cycle re-assigning via INSERT ON CONFLICT.
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_active_streams
  WHERE (
      assigned_instance_id IS NOT NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = __SCHEMA__.wh_active_streams.assigned_instance_id
      )
    )
    OR (
      assigned_instance_id IS NULL
      AND last_activity_at < NOW() - (v_abandoned_stream_hours * INTERVAL '1 hour')
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_abandoned_active_streams'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 7: Refresh wh_dead_letter_summary
  -- ========================================
  -- Slice 6 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis).
  -- Two-step pipeline inside aggregate_dead_letters:
  --   (1) Version-aware backfill, re-hashes raw wh_dead_letters rows with
  --       stale error_fingerprint_version; current-version rows are skipped.
  --   (2) GROUP BY upsert into wh_dead_letter_summary.
  -- The summary table is the operator/AI-facing rollup view: ~dozens of
  -- distinct fingerprint clusters instead of tens of thousands of raw rows.
  -- Cluster-count metric is the rows_affected for this task (post-aggregation).
  v_start := clock_timestamp();
  PERFORM __SCHEMA__.aggregate_dead_letters();
  SELECT COUNT(*) FROM __SCHEMA__.wh_dead_letter_summary INTO v_rows;
  RETURN QUERY SELECT
    'aggregate_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 8: Reap consumed ephemeral event bodies (E1 #13b2, + E2-4c TTL floor)
  -- ========================================
  -- wh_event_body holds ONLY ephemeral bodies (offloaded by the emit chain, migration 072). A body is
  -- reapable once every perspective that consumes its event has processed it, i.e. no unprocessed
  -- wh_perspective_events work item still references the event_id. The emit chain writes the body and
  -- its perspective work items in one transaction, so a body with consumers always has a matching
  -- gating work item (no premature-reap window); an ephemeral event with no consuming perspective has
  -- no work item and is reapable at once. The wh_event_store pointer is left in place, a
  -- pointer-present / body-NULL row is the deterministic rebuild-guard signal (#13d), not a lost event.
  -- Skipped under debug_mode so retained forensic bodies survive with the retained work items.
  -- Grace window: a consumed body is also kept until it is OLDER than v_ephemeral_grace_seconds, so an
  -- out-of-order straggler can still rewind through it (rewind uses the surviving bodies + a snapshot floor).
  -- TTL floor (E2-4c): an AfterTtl event carries its own absolute expiry in body metadata
  -- ('ephemeral_expires_at', stamped at dispatch). It EXTENDS retention, the consumed body is kept until it
  -- is ALSO past that expiry. An event with no key (Sourced / WhenConsumed) is unaffected: the gate is
  -- vacuously true, and it reaps as soon as consumed+aged.
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb
    USING __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_ephemeral_type_grace g ON g.event_type = es.event_type
    WHERE es.event_id = eb.event_id
      -- #13b4 safety gate: the reap is scoped to EPHEMERAL events explicitly. Pre-split this was
      -- guaranteed "by construction" (wh_event_body held only ephemeral bodies); once SOURCED bodies
      -- move into the body table (full split), this gate is what keeps the durable log un-reapable.
      AND (es.flags & 8) = 8
      -- E2-3 destruction hold: a PreDestruction hook may Cancel (hold far-future) or Defer(until) a body;
      -- while a hold is active the reap skips it, so the hook's decision is honored.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
        WHERE h.event_id = eb.event_id AND h.hold_until > NOW()
      )
      AND es.created_at < NOW() - (COALESCE(g.grace_seconds, v_ephemeral_grace_seconds) * INTERVAL '1 second')
      -- E2-4c TTL retention floor: an AfterTtl body carries an absolute 'ephemeral_expires_at' in its
      -- metadata; it is kept until past that instant. No key (Sourced / WhenConsumed) => vacuously true.
      AND (
        eb.metadata ->> 'ephemeral_expires_at' IS NULL
        OR (eb.metadata ->> 'ephemeral_expires_at')::timestamptz < NOW()
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.event_id = eb.event_id
          AND pe.processed_at IS NULL
      )
      -- Snapshot-coverage gate: the reap must never outrun the rewind floor. Reap only once EVERY consuming
      -- perspective has a snapshot at or past this event IN THE STREAM, i.e. there is no association whose
      -- perspective lacks a covering snapshot for the stream. The reap-driven step (MaintenanceWorker) drives
      -- those snapshots just before this runs, so coverage is normally satisfied; an event with no consuming
      -- perspective is vacuously covered.
      --
      -- 209: compared by version, not by commit sequence. Commit sequence is assignment order, and a row
      -- stamped after the fact carries a sequence far below rows written long before it, so a snapshot that
      -- sits EARLIER in the stream than the event could still compare "at or past" it and let the body go.
      -- The body is the only thing a rewind past that point can replay, so losing it leaves the perspective
      -- permanently short with nothing pending to repair it. Version is the stream's own counter, unique per
      -- stream by index, and cannot invert.
      --
      -- Both NULL cases hold the body rather than reap it, which is the recoverable direction: an event not
      -- yet placed in a stream, and a snapshot written before 209 that records no version, each compare NULL
      -- and so never count as covering.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_message_associations ma
        WHERE ma.normalized_message_type = es.event_type
          AND ma.association_type = __CATEGORY_PERSPECTIVE__
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
            WHERE s.stream_id = es.stream_id
              AND s.perspective_name = ma.target_name
              AND s.snapshot_version >= es.version
          )
      );
    GET DIAGNOSTICS v_rows = ROW_COUNT;

    -- Keep the hold table bounded: drop holds whose body is already gone (a Defer whose window lapsed and
    -- was then reaped, or any body reaped by another path). A permanent Cancel keeps body + hold together.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h
    WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = h.event_id);
  END IF;
  RETURN QUERY SELECT
    'reap_consumed_ephemeral_bodies'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 9: Reap expired TtlRow perspective rows (E2-4d)
  -- ========================================
  -- TransientStorage.TtlRow perspective rows carry an expires_at (stamped on upsert = now + ttl). Once past,
  -- a row is logically expired (already hidden from lens reads) and is physically deleted here.
  --
  -- 165: driven by the REGISTRY, not the catalog. This enumerated every wh_per_* table carrying an expires_at
  -- column, reasoning that a perspective declaring no lifetime never stamps it so its rows cannot match. True,
  -- and irrelevant to cost: expires_at comes from the shared table template, so EVERY perspective table has
  -- the column, and each took an unindexed DELETE that matched nothing. Measured on a deployed service: 120
  -- tables scanned for the 3 that declare a lifetime, 14,758 ms to delete zero rows -- 98 percent of the whole
  -- maintenance cycle, which itself was the heaviest statement on that database. Another service scanned 81
  -- tables for one declaring perspective. The registry already records the declaration, and
  -- reap_enrolled_perspective_rows (112), reap_perspective_row_caps (113) and
  -- collect_perspective_row_reap_targets (113) all read it; this task was the one place left on the old shape.
  --
  -- row_ttl_seconds rather than row_retention_enrolled: expires_at is stamped by the perspective's own write
  -- path from its TtlRow declaration, so it is live whether or not an operator has acknowledged the retention
  -- program. Gating on the acknowledgement would stop reaping rows that are still being stamped today.
  -- Skipped under debug_mode, like the body reaper.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    -- The schema is this function's own, from the substitution baked into its body, never current_schema(): that
    -- is the CALLER's search_path, which on the EF Core driver's (and a pooled) connection is public whatever
    -- schema the service owns. replace() strips the quotes the EF Core driver substitutes (Dapper substitutes
    -- the bare name), so the value compares equal to information_schema.table_schema on both drivers.
    FOR v_per_table IN
      SELECT r.table_name
      FROM __SCHEMA__.wh_perspective_registry r
      WHERE r.row_ttl_seconds IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM information_schema.tables t
          WHERE t.table_schema = replace('__SCHEMA__', '"', '')
            AND t.table_name = r.table_name)
    LOOP
      EXECUTE format(
        'WITH del AS (
           DELETE FROM %I.%I WHERE expires_at IS NOT NULL AND expires_at < NOW()
           RETURNING id)
         SELECT ARRAY(SELECT id FROM del)',
        replace('__SCHEMA__', '"', ''), v_per_table)
        INTO v_per_victims;
      v_per_deleted := cardinality(v_per_victims);
      v_rows := v_rows + v_per_deleted;
      -- Issue #1483: an eviction reclaims storage only. The expired rows' cursors and all but their
      -- latest snapshots go with them (the helper above), so the next event on the stream rebuilds the row.
      PERFORM __SCHEMA__._wh_evict_perspective_row_companions(v_per_table, v_per_victims);
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'reap_expired_perspective_rows'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 10: Purge expired instance-eviction tombstones (migration 106)
  -- ========================================
  -- The tombstone in wh_instance_evictions only needs to survive long enough for a genuinely
  -- paused instance to resume and be correctly refused. Once it is older than the retention
  -- window, either the instance is long dead for real, or, since instance ids are generated
  -- per PROCESS, not per deployment slot, anything still calling with that id is not the same
  -- process that was reaped. Keeping the row past that point only grows the table. Not gated on
  -- debug_mode: this is instance-identity bookkeeping, not forensic message data (same treatment
  -- as Task 6's abandoned-active-stream purge).
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_instance_evictions
  WHERE evicted_at < NOW() - (v_instance_eviction_retention_hours * INTERVAL '1 hour');
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'purge_instance_evictions'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 11: Purge settled dead letters
  -- ========================================
  -- Recovered(3) means the message was successfully re-driven, so the row is a receipt rather than
  -- work. Every other status is either unresolved or a deliberate human hold, and none of those may
  -- be discarded on age alone. Skipped under debug_mode, where the operator asked to keep evidence.
  v_start := clock_timestamp();
  IF v_debug_mode THEN
    v_rows := 0;
  ELSE
    -- Retention keys on when the row SETTLED (#682): a backlog older than the window would
    -- otherwise have its receipts deleted within one maintenance cycle of recovering ,
    -- recovered counts went BACKWARDS while a drain made real progress. recovered_at is
    -- NULL only on legacy rows settled before it was stamped; those fall back to the
    -- original failure time rather than living forever.
    -- A row referenced by an UNRESOLVED campaign's probe_ids is evidence, not clutter:
    -- deleting it resolves the campaign on an empty evidence set (see 127's evaluate).
    DELETE FROM __SCHEMA__.wh_dead_letters d
    WHERE d.recovery_status = 3
      AND COALESCE(d.recovered_at, d.dead_lettered_at) < NOW() - (v_dead_letter_retention_days || ' days')::INTERVAL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_dlq_probe_campaigns c
        WHERE c.verdict = 0 AND d.dead_letter_id = ANY(c.probe_ids)
      );
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_recovered_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- ========================================
  -- Task 13: Settle orphaned perspective-event DEAD LETTERS (issue #687)
  -- ========================================
  -- Task 12 reaps orphans still in wh_perspective_events. A row that was dead-lettered before
  -- its event vanished sits in wh_dead_letters instead, held forever: recovery would re-drive
  -- it into an empty join, and generation replay excludes held rows by design. When the row's
  -- ENTIRE source stream is absent from wh_event_store, every event of it is gone, so the
  -- perspective work is unrecoverable, settle it (Recovered + note, same disposition as the
  -- disabled-subsystem discard) so the ledger records the disposal and retention ages it out.
  -- The whole-stream predicate needs no per-event lookup and has no false positives: a stream
  -- with any surviving event is left for review (a genuine apply failure, not an orphan). Age-
  -- gated on dead_lettered_at by the same grace window so a stream still being written is safe.
  -- Operator holds (operator_disposition 2/3) are respected.
  v_start := clock_timestamp();
  UPDATE __SCHEMA__.wh_dead_letters dl
  SET recovery_status = 3,  -- Recovered: settled, eligible for the retention purge
      recovered_at    = NOW(),
      operator_notes  = COALESCE(operator_notes || E'\n', '')
        || 'auto-settled by maintenance: orphaned perspective event, source stream absent from event store'
  WHERE dl.source_table = 'wh_perspective_events'
    AND dl.recovered_at IS NULL
    AND dl.recovery_status NOT IN (3, 4)
    AND dl.operator_disposition NOT IN (2, 3)
    AND dl.stream_id IS NOT NULL
    AND dl.dead_lettered_at < NOW() - (v_orphan_grace_hours * INTERVAL '1 hour')
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = dl.stream_id
    );
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'settle_orphaned_perspective_dead_letters'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

  -- ========================================
  -- Task 14: Prune idle collective ordering heads (190)
  -- ========================================
  -- A head only matters while the next collective on its key could still be published and the one it
  -- names could still be in flight to a receiver. A key idle for the retention window (wh_settings
  -- 'collective_ordering_head_retention_days', default 7) is forgotten: its next collective goes out
  -- unlinked and applies at once, which is right, because its predecessor applied days ago. Pruning by
  -- idleness bounds the table by the keys in use within the window, not by every key ever used.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'collective_ordering_head_retention_days'),
    7
  ) INTO v_ordering_head_retention_days;
  v_start := clock_timestamp();
  DELETE FROM __SCHEMA__.wh_collective_ordering_heads h
  WHERE h.updated_at < NOW() - (v_ordering_head_retention_days * INTERVAL '1 day');
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN QUERY SELECT
    'prune_collective_ordering_heads'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;

END;
$$ LANGUAGE plpgsql;
