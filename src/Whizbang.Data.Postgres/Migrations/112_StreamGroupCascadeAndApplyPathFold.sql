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

-- (1b) Expiry sweep re-created VERBATIM from 111 with one change: the DELETE journals its victims.
DROP FUNCTION IF EXISTS __SCHEMA__.reap_enrolled_perspective_rows(INTEGER);

CREATE OR REPLACE FUNCTION __SCHEMA__.reap_enrolled_perspective_rows(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start        TIMESTAMPTZ := clock_timestamp();
  v_rows         INTEGER := 0;
  v_deleted      INTEGER := 0;
  v_debug_mode   BOOLEAN := FALSE;
  v_reg          RECORD;
  v_pending      INTEGER := 0;
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
         RETURNING id)
       INSERT INTO %I.wh_row_eviction_journal (table_name, row_id)
       SELECT %L, id FROM del
       ON CONFLICT (table_name, row_id) DO NOTHING',
      current_schema(), v_reg.table_name, current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name)
    USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds, p_batch_size;

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    v_rows := v_rows + v_deleted;
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
CREATE OR REPLACE FUNCTION __SCHEMA__.cascade_delete_perspective_rows(
  p_table_name TEXT,
  p_row_ids UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_deleted INTEGER := 0;
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = current_schema() AND table_name = p_table_name) THEN
    RETURN 0;
  END IF;

  EXECUTE format(
    'DELETE FROM %I.%I WHERE id = ANY($1)
       AND NOT EXISTS (
         SELECT 1 FROM %I.wh_perspective_row_hold h
         WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())',
    current_schema(), p_table_name, current_schema(), p_table_name)
  USING p_row_ids;

  GET DIAGNOSTICS v_deleted = ROW_COUNT;
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
