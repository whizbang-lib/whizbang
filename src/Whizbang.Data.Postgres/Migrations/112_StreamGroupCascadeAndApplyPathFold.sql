-- Migration: 112_StreamGroupCascadeAndApplyPathFold
-- Purpose:  Two consumers of the row-destruction seam land their SQL substrate here.
--           (1) STREAM GROUPS: the row sweeps now JOURNAL what they evict, so the maintenance
--               cascade can compute the group closure (Announce/Follow/Bridge) over exactly the
--               rows that actually died and evict the same streams from sibling perspectives in
--               the same cycle — plus the hold-aware cascade delete the closure executes.
--           (2) APPLY-PATH FOLD: the persisted half of the apply-stack flow view — a signature
--               table sized by DISTINCT SHAPES plus a fold function that collapses a stream's
--               version-ordered event-type path (same gaps-and-islands RLE as the live query)
--               into the counts BEFORE the stream's pointers are destroyed. The stream dies; its
--               shape survives.
--
--           (3) EVICTION COMPANIONS (issue #1483): an eviction reclaims storage only; the read model
--               stays a function of the events. Every path that evicts a perspective row (both sweeps
--               here and in 113, the cascade, and the stamped-expiry reap in perform_maintenance)
--               also removes, for each perspective that folds the evicted model, its cursor for the
--               stream and every snapshot but the latest, in the same statement chain. The next event
--               then rebuilds the row from that snapshot plus the history after it.
--
-- Dependencies: 111 (hold table + sweeps), 101-104 (registry/enrolment)

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- (1a) The eviction journal: each sweep records the (table, row) pairs it destroyed. Drained by
-- the maintenance cascade in the same or next cycle (DELETE ... RETURNING = atomic claim, so N
-- replicas never double-cascade). Cascade deletes themselves are NOT journaled — transitivity is
-- the closure's job, governed by Bridge, never by re-seeding.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_row_eviction_journal (
  table_name TEXT        NOT NULL,
  row_id     UUID        NOT NULL,
  evicted_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (table_name, row_id)
);

COMMENT ON TABLE __SCHEMA__.wh_row_eviction_journal IS
  'Origin evictions awaiting group-cascade processing: the row sweeps write what they destroyed; '
  'the maintenance cascade drains atomically and expands through the stream-group closure.';

-- (3a) The model -> perspective mapping. Cursors and snapshots are keyed by the PERSPECTIVE's CLR type
-- name, the registry by the MODEL's; reconcile_perspective_registry (142) records here every perspective
-- the generator found folding the model. NULL until a startup reconcile has recorded them, and an
-- eviction from such a row then removes the row alone, never a guessed companion.
ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS perspective_names TEXT[];

COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.perspective_names IS
  'CLR type names of the perspectives that fold this model (the form wh_perspective_cursors and '
  'wh_perspective_snapshots key on). Written by reconcile_perspective_registry; read by the row-eviction '
  'companion cleanup (issue #1483).';

-- (3b) The companion cleanup every eviction path calls with the ids it actually deleted. For each
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

-- (1b) Expiry sweep re-created VERBATIM from 111 with one change: the DELETE journals its victims.
-- 1483: the victims' companions are cleaned in the same statement chain.
DROP FUNCTION IF EXISTS __SCHEMA__.reap_enrolled_perspective_rows(INTEGER);

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
    -- sees exactly what died — never an approximation of what should have.
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

-- Exactly one overload per framework function: this name is defined at more than one
-- arity across the migration set, and CREATE OR REPLACE at a different arity ADDS an
-- overload beside the old one rather than replacing it. The duplicate then makes every
-- unqualified reference ambiguous (42725) -- including this file's own COMMENT ON
-- FUNCTION -- which fails the whole startup pass and strands every later migration.
SELECT __SCHEMA__.drop_all_overloads('reap_perspective_row_caps');

-- (1c) Cap sweep re-created VERBATIM from 111 with the same journaling change.
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_perspective_row_caps()
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start      TIMESTAMPTZ := clock_timestamp();
  v_rows       INTEGER := 0;
  v_deleted    INTEGER := 0;
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
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
    WHERE row_retention_enrolled AND row_cap_per_scope IS NOT NULL
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
           ) ranked WHERE ranked.rn > $1)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
         RETURNING id)
       INSERT INTO %I.wh_row_eviction_journal (table_name, row_id)
       SELECT %L, id FROM del
       ON CONFLICT (table_name, row_id) DO NOTHING',
      current_schema(), v_reg.table_name, v_partition, current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name)
    USING v_reg.row_cap_per_scope;

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    v_rows := v_rows + v_deleted;
  END LOOP;

  RETURN QUERY SELECT
    'reap_perspective_row_caps'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

-- (1d) The cascade delete: hold-aware, so a guard's Defer on a cascaded row survives it; returns
-- the count actually destroyed. NOT journaled — see the journal comment.
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
-- (2a) The apply-path signature table: one row per DISTINCT collapsed shape. Size scales with
-- shapes, not streams or events — which is what makes persisting it cheap and folding it safe.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_apply_paths (
  path         TEXT[]      NOT NULL PRIMARY KEY,
  stream_count BIGINT      NOT NULL DEFAULT 0,
  first_seen   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_seen    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_apply_paths IS
  'Persisted apply-path signatures: the fold-before-discard half of the apply-stack flow view. '
  'A destroyed stream''s shape survives here; live streams are computed on demand and unioned in.';

-- (2b) The fold: collapse each given stream''s version-ordered event-type path (the same
-- gaps-and-islands RLE the live query uses — runs of 2+ become one ''+''-suffixed element) and
-- upsert it into the signature counts. Idempotence is the CALLER''s contract: fold a stream
-- exactly once, immediately before destroying its pointers.
CREATE OR REPLACE FUNCTION __SCHEMA__.fold_stream_apply_paths(p_stream_ids UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_folded INTEGER := 0;
BEGIN
  WITH runs AS (
    SELECT es.stream_id, es.version, es.event_type, es.created_at,
           CASE WHEN lag(es.event_type) OVER (PARTITION BY es.stream_id ORDER BY es.version)
                     IS DISTINCT FROM es.event_type THEN 1 ELSE 0 END AS run_break
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = ANY(p_stream_ids)
  ),
  grouped AS (
    SELECT stream_id, event_type, created_at,
           SUM(run_break) OVER (PARTITION BY stream_id ORDER BY version) AS run_no
    FROM runs
  ),
  collapsed AS (
    SELECT stream_id, run_no,
           CASE WHEN COUNT(*) > 1 THEN MIN(event_type) || '+' ELSE MIN(event_type) END AS element,
           MAX(created_at) AS last_at
    FROM grouped
    GROUP BY stream_id, run_no
  ),
  paths AS (
    SELECT stream_id,
           array_agg(element ORDER BY run_no) AS path,
           MAX(last_at) AS head_at
    FROM collapsed
    GROUP BY stream_id
  ),
  shapes AS (
    SELECT path, COUNT(*) AS n, MIN(head_at) AS first_at, MAX(head_at) AS last_at
    FROM paths
    GROUP BY path
  )
  INSERT INTO __SCHEMA__.wh_apply_paths AS ap (path, stream_count, first_seen, last_seen)
  SELECT s.path, s.n, s.first_at, s.last_at FROM shapes s
  ON CONFLICT (path) DO UPDATE SET
    stream_count = ap.stream_count + EXCLUDED.stream_count,
    first_seen = LEAST(ap.first_seen, EXCLUDED.first_seen),
    last_seen = GREATEST(ap.last_seen, EXCLUDED.last_seen);

  GET DIAGNOSTICS v_folded = ROW_COUNT;
  RETURN v_folded;
END;
$$ LANGUAGE plpgsql;
