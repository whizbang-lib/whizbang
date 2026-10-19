-- Migration: 211_EvictionRemovesItsCompanions.sql
-- Date: 2026-10-19
-- Description: An eviction reclaims storage only; the read model stays a function of the events (#1483).
--
--              Every path that evicts a perspective row (the TTL sweep, the cap sweep, the stream-group
--              cascade, and the stamped-expiry reap in perform_maintenance task 9) now also removes, for each
--              perspective that folds the evicted model, its cursor for the stream and every snapshot but the
--              latest, in the same statement chain as the row. The next event then rebuilds the row from that
--              snapshot plus the history after it. Other perspectives' cursors and snapshots on the same
--              stream are never touched. Before this, the sweeps deleted the row and left its cursor and all
--              its snapshots behind.
--
--              The SQL had no model-to-perspective mapping: the registry keys on the MODEL's CLR type name,
--              cursors and snapshots on the PERSPECTIVE's. wh_perspective_registry.perspective_names carries
--              it, written by reconcile_perspective_registry from a "PerspectiveName" in each payload entry
--              (the generator emits one entry per perspective, in the CLR form). A payload that names no
--              perspective for a model leaves the recorded names alone, and a registry row with none recorded
--              evicts the row only, never a guessed companion.
--
--              The sweeps now count the rows they deleted rather than the journal rows they inserted (the
--              journal may already hold an id).
--
--              Every function below is its latest definition copied whole (rule 5) plus the delta:
--              reap_enrolled_perspective_rows (112), reap_perspective_row_caps (113),
--              cascade_delete_perspective_rows (112), reconcile_perspective_registry (142), perform_maintenance
--              (209). No later migration redefines any of them.
-- Dependencies: 112 (journal, cascade), 113 (cap sweep), 142 (reconcile), 209 (perform_maintenance)
-- Objects: wh_perspective_registry, _wh_evict_perspective_row_companions, reap_enrolled_perspective_rows, reap_perspective_row_caps, cascade_delete_perspective_rows, reconcile_perspective_registry, perform_maintenance
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 1: the model -> perspective mapping. NULL until a startup reconcile has recorded the names.
ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS perspective_names TEXT[];

COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.perspective_names IS
  'CLR type names of the perspectives that fold this model (the form wh_perspective_cursors and '
  'wh_perspective_snapshots key on). Written by reconcile_perspective_registry; read by the row-eviction '
  'companion cleanup (issue #1483).';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 2: the companion cleanup every eviction path calls with the ids it actually deleted. For each
-- perspective of the table's model: the cursor of each evicted stream goes (the next event must not
-- continue a row that no longer exists), and every snapshot but the latest goes (the latest is the floor
-- the rebuild restores from). "Latest" is the one a rewind would restore: highest stream version, with a
-- versionless snapshot (written before 209) last, because a rewind cannot place it in the stream. Other
-- perspectives' cursors and snapshots on the same streams are never touched. Returns rows removed.
-- <docs>fundamentals/perspectives/row-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Cascade_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Eviction_KeepsTheSnapshotARewindCanPlace_NotANewerVersionlessOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Eviction_ModelWithNoKnownPerspectives_RemovesOnlyTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Cascade_NoRowsDeleted_TouchesNoCompanionsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEvictionCompanionCleanupTests.cs:Cascade_OnADapperMigratedDatabase_RemovesCursor_KeepsLatestSnapshotAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_evict_perspective_row_companions(
  p_table_name TEXT,
  p_row_ids UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_names     TEXT[];
  v_cursors   INTEGER := 0;
  v_snapshots INTEGER := 0;
BEGIN
  IF COALESCE(cardinality(p_row_ids), 0) = 0 THEN
    RETURN 0;
  END IF;

  SELECT array_agg(DISTINCT n.perspective_name)
    INTO v_names
    FROM __SCHEMA__.wh_perspective_registry r
    CROSS JOIN LATERAL unnest(r.perspective_names) AS n(perspective_name)
   WHERE r.table_name = p_table_name;

  IF v_names IS NULL THEN
    RETURN 0;
  END IF;

  DELETE FROM __SCHEMA__.wh_perspective_cursors c
   WHERE c.stream_id = ANY(p_row_ids)
     AND c.perspective_name = ANY(v_names);
  GET DIAGNOSTICS v_cursors = ROW_COUNT;

  DELETE FROM __SCHEMA__.wh_perspective_snapshots s
   USING (
     SELECT sn.stream_id, sn.perspective_name, sn.snapshot_event_id,
            ROW_NUMBER() OVER (
              PARTITION BY sn.stream_id, sn.perspective_name
              ORDER BY sn.snapshot_version DESC NULLS LAST, sn.sequence_number DESC) AS rn
       FROM __SCHEMA__.wh_perspective_snapshots sn
      WHERE sn.stream_id = ANY(p_row_ids)
        AND sn.perspective_name = ANY(v_names)) ranked
   WHERE ranked.rn > 1
     AND s.stream_id = ranked.stream_id
     AND s.perspective_name = ranked.perspective_name
     AND s.snapshot_event_id = ranked.snapshot_event_id;
  GET DIAGNOSTICS v_snapshots = ROW_COUNT;

  RETURN v_cursors + v_snapshots;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_evict_perspective_row_companions(TEXT, UUID[]) IS
  'Issue #1483: for rows just evicted from a perspective table, removes each folding perspective''s '
  'cursor and all but its latest snapshot for those streams. Called by every eviction path.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 3: the expiry sweep, from 112 verbatim plus the delta: the victims come back as an array, the
-- journal insert moves into its own CTE, and the companions are cleaned for exactly those victims.

-- Exactly one overload per framework function: this name is defined at more than one
-- arity across the migration set, and CREATE OR REPLACE at a different arity ADDS an
-- overload beside the old one rather than replacing it. The duplicate then makes every
-- unqualified reference ambiguous (42725) -- including this file's own COMMENT ON
-- FUNCTION -- which fails the whole startup pass and strands every later migration.
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
      WHERE table_schema = current_schema() AND table_name = v_reg.table_name);

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
      current_schema(), v_reg.table_name, current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name)
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
      current_schema(), v_reg.table_name, current_schema(), v_reg.table_name);
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

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 4: the cap sweep, from 113 verbatim plus the same delta.

-- Exactly one overload per framework function (see Part 3).
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
      WHERE table_schema = current_schema() AND table_name = v_reg.table_name);

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
      current_schema(), v_reg.table_name, v_partition, current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name)
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

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 5: the cascade delete, from 112 verbatim plus the delta. Still not journaled (see 112).

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
    WHERE table_schema = current_schema() AND table_name = p_table_name) THEN
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
    current_schema(), p_table_name, current_schema(), p_table_name)
  INTO v_victims
  USING p_row_ids;

  -- 1483: the cascaded rows' cursors and all but their latest snapshots go with them.
  v_deleted := cardinality(v_victims);
  PERFORM __SCHEMA__._wh_evict_perspective_row_companions(p_table_name, v_victims);
  RETURN v_deleted;
END;
$$ LANGUAGE plpgsql;

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 6: reconcile, from 142 verbatim plus the delta: every perspective the payload names for the
-- model is recorded in perspective_names (a model folded by two perspectives arrives as two entries).

SELECT __SCHEMA__.drop_all_overloads('reconcile_perspective_registry');

-- <docs>fundamentals/perspectives/registry</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcilePerspectiveRegistryKeyAdoptionSqlTests.cs:Reconcile_ExistingDisplayStringKey_IsAdoptedNotDuplicatedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Reconcile_RecordsEveryPerspectiveOfTheModelAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Reconcile_PayloadWithoutPerspectiveNames_KeepsTheRecordedOnesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EvictionCompanionCleanupSqlTests.cs:Reconcile_ChangedPerspectiveNames_ReplaceTheRecordedOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reconcile_perspective_registry(
  p_perspectives JSONB,
  p_service_name VARCHAR DEFAULT NULL
)
RETURNS TABLE (
  action VARCHAR,
  clr_type_name VARCHAR,
  old_table_name VARCHAR,
  new_table_name VARCHAR,
  old_schema_hash VARCHAR,
  new_schema_hash VARCHAR
) AS $$
DECLARE
  v_perspective RECORD;
  v_existing RECORD;
  v_action VARCHAR;
  v_old_table_name VARCHAR;
  v_old_schema_hash VARCHAR;
  v_adopted BOOLEAN;
  v_perspective_names TEXT[];
BEGIN
  -- Process each perspective in the array
  FOR v_perspective IN
    SELECT
      assoc->>'ClrTypeName' AS clr_type_name,
      assoc->>'TableName' AS table_name,
      (assoc->'SchemaJson')::JSONB AS schema_json,
      assoc->>'SchemaHash' AS schema_hash,
      COALESCE(assoc->>'ServiceName', p_service_name) AS service_name
    FROM jsonb_array_elements(p_perspectives) AS assoc
  LOOP
    -- 1483: every perspective that folds this model, from the whole payload (a model folded by two
    -- perspectives arrives as two entries). NULL when the payload names none, which keeps what is recorded.
    SELECT array_agg(DISTINCT e->>'PerspectiveName' ORDER BY e->>'PerspectiveName')
      INTO v_perspective_names
      FROM jsonb_array_elements(p_perspectives) AS e
     WHERE e->>'ClrTypeName' = v_perspective.clr_type_name
       AND COALESCE(e->>'ServiceName', p_service_name) = v_perspective.service_name
       AND NULLIF(e->>'PerspectiveName', '') IS NOT NULL;

    -- Check if this CLR type already exists in the registry
    SELECT
      pr.table_name,
      pr.schema_hash
    INTO v_existing
    FROM __SCHEMA__.wh_perspective_registry pr
    WHERE pr.clr_type_name = v_perspective.clr_type_name
      AND pr.service_name = v_perspective.service_name;

    -- 142: adopt a row written under the previous key form (issue #697). The registry used to be
    -- keyed by a display-string rendering of a nested model (Outer.Model); the canonical key is
    -- the CLR form (Outer+Model), which is what the runtime looks up. A nesting dot cannot be told
    -- from a namespace dot by SQL alone, so the adoption keys on the one thing both forms share:
    -- the table this service registered for the model. The row keeps its enrollment columns.
    v_adopted := FALSE;
    IF NOT FOUND THEN
      SELECT
        pr.table_name,
        pr.schema_hash
      INTO v_existing
      FROM __SCHEMA__.wh_perspective_registry pr
      WHERE pr.table_name = v_perspective.table_name
        AND pr.service_name = v_perspective.service_name
        AND pr.clr_type_name <> v_perspective.clr_type_name;
      IF FOUND THEN
        UPDATE __SCHEMA__.wh_perspective_registry
        SET clr_type_name = v_perspective.clr_type_name,
            updated_at = NOW()
        WHERE __SCHEMA__.wh_perspective_registry.table_name = v_perspective.table_name
          AND __SCHEMA__.wh_perspective_registry.service_name = v_perspective.service_name;
        v_adopted := TRUE;
      END IF;
    END IF;

    IF FOUND THEN
      -- CLR type exists in registry
      v_old_table_name := v_existing.table_name;
      v_old_schema_hash := v_existing.schema_hash;

      -- Check if table name changed
      IF v_existing.table_name != v_perspective.table_name THEN
        -- Table name changed - execute ALTER TABLE RENAME
        BEGIN
          EXECUTE format(
            'ALTER TABLE IF EXISTS %I RENAME TO %I',
            v_existing.table_name,
            v_perspective.table_name
          );
          v_action := 'renamed';
        EXCEPTION WHEN OTHERS THEN
          -- If rename fails (e.g., table doesn't exist), just update registry
          v_action := 'updated';
        END;
      ELSIF v_existing.schema_hash != v_perspective.schema_hash THEN
        -- Schema changed but table name same - drift detected
        v_action := 'drift_detected';
      ELSIF v_adopted THEN
        -- 142: same table, same service, a key written in the previous form: renamed in place
        v_action := 'renamed_key';
      ELSE
        -- No changes, just update timestamp
        v_action := 'updated';
      END IF;

      -- Update the registry entry
      UPDATE __SCHEMA__.wh_perspective_registry
      SET
        table_name = v_perspective.table_name,
        schema_json = v_perspective.schema_json,
        schema_hash = v_perspective.schema_hash,
        perspective_names = COALESCE(v_perspective_names, __SCHEMA__.wh_perspective_registry.perspective_names),
        updated_at = NOW()
      WHERE __SCHEMA__.wh_perspective_registry.clr_type_name = v_perspective.clr_type_name
        AND __SCHEMA__.wh_perspective_registry.service_name = v_perspective.service_name;

    ELSE
      -- New CLR type - insert into registry
      v_action := 'inserted';
      v_old_table_name := NULL;
      v_old_schema_hash := NULL;

      INSERT INTO __SCHEMA__.wh_perspective_registry (
        clr_type_name,
        table_name,
        schema_json,
        schema_hash,
        service_name,
        perspective_names,
        created_at,
        updated_at
      ) VALUES (
        v_perspective.clr_type_name,
        v_perspective.table_name,
        v_perspective.schema_json,
        v_perspective.schema_hash,
        v_perspective.service_name,
        v_perspective_names,
        NOW(),
        NOW()
      );
    END IF;

    -- Return the action for this perspective
    -- Explicit casts ensure RECORD column types match RETURNS TABLE exactly
    -- (jsonb ->> returns TEXT, but RETURNS TABLE expects VARCHAR)
    RETURN QUERY SELECT
      v_action::VARCHAR,
      v_perspective.clr_type_name::VARCHAR,
      v_old_table_name::VARCHAR,
      v_perspective.table_name::VARCHAR,
      v_old_schema_hash::VARCHAR,
      v_perspective.schema_hash::VARCHAR;
  END LOOP;

  RETURN;
END;
$$ LANGUAGE plpgsql;

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Part 7: perform_maintenance, from 209 verbatim plus the delta in Task 9: the expired rows come back as
-- an array and their companions are cleaned. Nothing else in the function changes.

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
      -- while a hold is active the reap skips it, so the hook's decision is honoured.
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
    -- current_schema() (NOT the __SCHEMA__ placeholder): the EFCore schema-init replaces __SCHEMA__ with a
    -- QUOTED identifier ("public"), which is correct for `schema.table` refs but wrong inside a string literal
    -- compared to information_schema.table_schema (unquoted). current_schema() is the effective schema the
    -- maintenance connection runs in (same pattern as migration 046).
    FOR v_per_table IN
      SELECT r.table_name
      FROM __SCHEMA__.wh_perspective_registry r
      WHERE r.row_ttl_seconds IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM information_schema.tables t
          WHERE t.table_schema = current_schema()
            AND t.table_name = r.table_name)
    LOOP
      EXECUTE format(
        'WITH del AS (
           DELETE FROM %I.%I WHERE expires_at IS NOT NULL AND expires_at < NOW()
           RETURNING id)
         SELECT ARRAY(SELECT id FROM del)',
        current_schema(), v_per_table)
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
