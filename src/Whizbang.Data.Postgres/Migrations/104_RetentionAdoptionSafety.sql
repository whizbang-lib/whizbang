-- Migration: 104_RetentionAdoptionSafety
-- Purpose:  Make ADOPTING retention a decision rather than a side effect of a deploy, and stop the
--           first sweep from being one large delete against a shared database.
--
-- Why:      Because both mechanisms DERIVE rather than stamp, a declaration is retroactive the
--           moment it ships. Adding a 60-day window to a perspective holding three years of rows
--           means the next cycle finds the entire backlog expired; adding a per-scope cap of 50 to
--           a scope holding ten thousand rows means the first sweep evicts 9 950. Both are
--           CORRECT — deriving is what makes the declaration the truth — but correct and safe to
--           run unannounced are different things, and the risks are distinct: the surprise (nobody
--           knew the backlog was that large) and the load (one statement deleting a large
--           population on a database other workloads share).
--
-- Shape:    Two guards. Enforcement is gated until a newly-enrolled perspective is acknowledged,
--           and every sweep is chunked so a backlog drains across cycles instead of in one
--           statement. Steady state is unaffected: once drained, each cycle finds a handful.
--
-- Dependencies: 101 (enrolment), 102 (expiry sweep), 103 (cap sweep)

-- Set when a perspective is first observed as enrolled. Enforcement waits until this is TRUE, so a
-- deploy REPORTS what it would remove before anything is removed.
ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS retention_enforcement_acknowledged BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.retention_enforcement_acknowledged IS
  'Gate on first adoption: a newly-enrolled perspective reports what it would remove and removes '
  'nothing until this is set. Prevents a deploy silently draining a historical backlog.';

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
    WHERE table_schema = current_schema() AND table_name = v_reg.table_name) THEN
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
    current_schema(), v_reg.table_name)
  INTO v_count
  USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds;

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

-- Re-created from 102 with two changes: enforcement is gated on acknowledgement, and the delete is
-- CHUNKED so a large backlog drains over several cycles rather than in one statement.
--
-- The zero-argument signature from 102 is DROPPED first. A defaulted parameter does not replace a
-- no-arg overload, it sits beside it, and a call with no arguments then resolves to neither —
-- 42725 "function is not unique". Same trap migration 085 hit when adding a defaulted argument.
DROP FUNCTION IF EXISTS __SCHEMA__.reap_enrolled_perspective_rows();

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
    -- handful and the bound never binds.
    EXECUTE format(
      'DELETE FROM %I.%I WHERE id IN (
         SELECT id FROM %I.%I WHERE
              (expires_at IS NOT NULL AND expires_at < NOW())
           OR (expires_at IS NULL AND $1 IS NOT NULL
               AND updated_at < NOW() - make_interval(secs => $1))
           OR ($2 IS NOT NULL
               AND created_at < NOW() - make_interval(secs => $2))
         LIMIT $3)',
      current_schema(), v_reg.table_name, current_schema(), v_reg.table_name)
    USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds, p_batch_size;

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    v_rows := v_rows + v_deleted;
    IF v_deleted >= p_batch_size THEN
      v_pending := v_pending + 1;
    END IF;
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
