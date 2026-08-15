-- Migration: 102_ReapEnrolledPerspectiveRows
-- Purpose:  Sweep perspective rows by the EFFECTIVE-EXPIRY LADDER, driven by enrolment rather than
--           by "does this table happen to have an expires_at column".
--
-- Replaces the behaviour of 082's Task 9, which could only act on a STAMPED expires_at and
-- enumerated every wh_per_* table carrying the column — i.e. every perspective table, since the
-- column is part of the standard DDL. Deriving the window instead means a perspective that adopts
-- retention immediately governs rows written before the declaration existed, with no backfill.
--
-- Standalone function, NOT folded into perform_maintenance: 082's Task 9 is left in place (it is a
-- no-op once no row carries a stamped-but-unreaped expiry within an enrolled perspective, and it
-- remains correct for anything that still stamps). Re-creating the 300-line perform_maintenance
-- verbatim to change one task is exactly the transcription risk this codebase has been bitten by.
--
-- Dependencies: 082 (row reap), 101 (retention enrolment on wh_perspective_registry)

CREATE OR REPLACE FUNCTION __SCHEMA__.reap_enrolled_perspective_rows()
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start        TIMESTAMPTZ := clock_timestamp();
  v_rows         INTEGER := 0;
  v_deleted      INTEGER := 0;
  v_debug_mode   BOOLEAN := FALSE;
  v_reg          RECORD;
BEGIN
  SELECT COALESCE((SELECT setting_value::BOOLEAN FROM wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN QUERY SELECT
      'reap_enrolled_perspective_rows'::TEXT, 0,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
      'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  -- Only ENROLLED perspectives are swept. A perspective that declared nothing is not scanned at
  -- all, however stale its rows look.
  FOR v_reg IN
    SELECT table_name, row_ttl_seconds, row_max_age_seconds
    FROM __SCHEMA__.wh_perspective_registry
    WHERE row_retention_enrolled
  LOOP
    -- Skip a registry row whose table has since been dropped, rather than aborting the sweep.
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = current_schema() AND table_name = v_reg.table_name);

    -- The ladder, as three disjuncts:
    --
    --   1. An explicit expires_at REPLACES the sliding rule (a deliberately pinned row).
    --   2. Otherwise the sliding rule derives from updated_at (business time). Guarded on
    --      expires_at IS NULL so an override wins, and on the window being present at all.
    --   3. The absolute cap derives from created_at and carries NO expires_at IS NULL guard.
    --      That asymmetry is deliberate and load-bearing: it is what makes a declared ceiling
    --      unbreachable by a per-row write. A data write must not defeat a retention limit
    --      declared in code. It reads like an oversight beside its neighbours; it is not.
    --
    -- Arithmetic sits on the NOW() side throughout, so each comparison stays sargable against the
    -- created_at / updated_at indexes rather than forcing a sequential scan per cycle.
    EXECUTE format(
      'DELETE FROM %I.%I WHERE
           (expires_at IS NOT NULL AND expires_at < NOW())
        OR (expires_at IS NULL AND $1 IS NOT NULL
            AND updated_at < NOW() - make_interval(secs => $1))
        OR ($2 IS NOT NULL
            AND created_at < NOW() - make_interval(secs => $2))',
      current_schema(), v_reg.table_name)
    USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds;

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    v_rows := v_rows + v_deleted;
  END LOOP;

  RETURN QUERY SELECT
    'reap_enrolled_perspective_rows'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_enrolled_perspective_rows() IS
  'Sweeps enrolled perspectives by the effective-expiry ladder: explicit expiry, else sliding window '
  'from updated_at, with an absolute cap from created_at that binds regardless of any override.';
