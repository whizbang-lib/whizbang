-- Migration: 111_PerspectiveRowDestructionSeam
-- Purpose:  Give the perspective-row retention sweeps a pre-destruction seam: a durable HOLD that
--           postpones a row's eviction, and a COLLECT function that exposes each sweep's DELETE
--           predicate as a SELECT (with the row payloads) so a guard can veto or defer before the
--           row dies.
--
-- Why:      A row that references an external resource (a blob, a file) must not be deleted until
--           that resource is verifiably cleaned up — the row must outlive the resource, never the
--           reverse. Stamping expires_at cannot express that hold: the cap sweep never reads
--           expires_at, and the absolute max-age disjunct deliberately pierces it. The ephemeral
--           reaper solved the same problem with wh_event_destruction_hold; this is the row-shaped
--           sibling, checked by BOTH sweeps and by the collect query so what a guard sees is
--           exactly what the sweeps would destroy.
--
-- Shape:    One hold table; both sweep functions re-created VERBATIM from their prior migrations
--           (104 expiry / 103 caps) with a single added NOT EXISTS against active holds plus
--           stale-hold cleanup; one collect function whose predicates are copied from the sweeps
--           byte-for-byte so preview, offer, and destruction can never disagree.
--
-- Dependencies: 101 (enrolment), 102/104 (expiry sweep), 103 (cap sweep)

-- The hold: a (table, row) pair whose eviction is postponed until hold_until. '-infinity' means no
-- active hold (a forced-delete marker after retry exhaustion); 'infinity' means keep forever (a
-- guard Cancel or the RetryThenKeep failure policy). failure_count carries the guard retry ladder.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_perspective_row_hold (
  table_name    TEXT        NOT NULL,
  row_id        UUID        NOT NULL,
  hold_until    TIMESTAMPTZ NOT NULL,
  failure_count INTEGER     NOT NULL DEFAULT 0,
  PRIMARY KEY (table_name, row_id)
);

COMMENT ON TABLE __SCHEMA__.wh_perspective_row_hold IS
  'Pre-destruction holds for perspective rows: a guard''s Defer/Cancel postpones eviction here. '
  'Checked by reap_enrolled_perspective_rows, reap_perspective_row_caps, and the collect function '
  'so an offered row and a destroyed row are decided by the same predicate.';

-- Re-created VERBATIM from 104 with two additions: the ladder excludes rows under an active hold,
-- and holds whose row is already gone are dropped per table (self-cleaning, the 079 pattern).
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

    -- The ladder (see 102): explicit expiry replaces the sliding rule; the cap disjunct carries NO
    -- expires_at IS NULL guard, which is what makes a declared ceiling unbreachable by a per-row
    -- write. Bounded by p_batch_size so a backlog drains across cycles; steady state finds a
    -- handful and the bound never binds. A row under an active hold is invisible to the ladder —
    -- a guard deferred it, and the hold lapsing re-offers it, never silently deletes it.
    EXECUTE format(
      'DELETE FROM %I.%I WHERE id IN (
         SELECT id FROM %I.%I WHERE
              ((expires_at IS NOT NULL AND expires_at < NOW())
           OR (expires_at IS NULL AND $1 IS NOT NULL
               AND updated_at < NOW() - make_interval(secs => $1))
           OR ($2 IS NOT NULL
               AND created_at < NOW() - make_interval(secs => $2)))
           AND NOT EXISTS (
             SELECT 1 FROM %I.wh_perspective_row_hold h
             WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
         LIMIT $3)',
      current_schema(), v_reg.table_name, current_schema(), v_reg.table_name,
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

-- Re-created VERBATIM from 103 with one addition: rows under an active hold are excluded from the
-- eviction, though they still occupy their rank — a deferred row beyond the cap is simply retried
-- next cycle, and coherence converges when its hold lapses.
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

    -- A NULL scope key ranks the whole table as one partition; otherwise partition on the scope
    -- JSON key. Ranking is by updated_at DESC — BUSINESS time — so "keep the newest N" means the
    -- most recently ACTIVE, and a rebuild reproduces the same ordering. Ranking on a wall-clock
    -- column would make a rebuild evict essentially arbitrary rows, since every row's timestamp
    -- would become the rebuild moment in write order.
    v_partition := CASE
      WHEN v_reg.row_cap_scope_key IS NULL THEN '1'
      ELSE format('scope ->> %L', v_reg.row_cap_scope_key)
    END;

    EXECUTE format(
      'DELETE FROM %I.%I WHERE id IN (
         SELECT id FROM (
           SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
           FROM %I.%I
         ) ranked WHERE ranked.rn > $1)
       AND NOT EXISTS (
         SELECT 1 FROM %I.wh_perspective_row_hold h
         WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())',
      current_schema(), v_reg.table_name, v_partition, current_schema(), v_reg.table_name,
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

COMMENT ON FUNCTION __SCHEMA__.reap_perspective_row_caps() IS
  'Evicts rows ranked beyond a perspective''s per-scope cap, ordered by business time. Separate from '
  'the expiry sweep and intended for a slower cadence: ranking needs a window function no index avoids. '
  'Rows under an active pre-destruction hold are excluded and retried when the hold lapses.';

-- COLLECT: each sweep's DELETE predicate as a SELECT, with the row payloads a guard needs. The
-- predicates are copied from the sweeps above byte-for-byte (including the acknowledgement gate on
-- the expiry side and its ABSENCE on the cap side, and the hold exclusion on both) so the offered
-- set and the destroyed set can never disagree. Returns nothing under debug_mode, because the
-- sweeps destroy nothing under debug_mode.
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
      WHERE table_schema = current_schema() AND table_name = v_reg.table_name);

    -- Expiry ladder (gated on acknowledgement, exactly like the sweep).
    IF v_reg.retention_enforcement_acknowledged THEN
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
        v_reg.clr_type_name, v_reg.table_name, current_schema(), v_reg.table_name,
        current_schema(), v_reg.table_name)
      USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds, p_per_table_limit;
    END IF;

    -- Cap overflow (NOT acknowledgement-gated, exactly like the sweep).
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
        v_reg.clr_type_name, v_reg.table_name, current_schema(), v_reg.table_name,
        v_partition, current_schema(), v_reg.table_name,
        current_schema(), v_reg.table_name)
      USING v_reg.row_cap_per_scope, p_per_table_limit;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.collect_perspective_row_reap_targets(TEXT[], INTEGER) IS
  'The pre-destruction seam''s COLLECT phase: the row sweeps'' DELETE predicates as a SELECT, with '
  'row payloads, so a registered guard is offered exactly the set the next sweep would destroy.';
