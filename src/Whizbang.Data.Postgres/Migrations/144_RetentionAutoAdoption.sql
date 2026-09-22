-- Migration: 144_RetentionAutoAdoption
-- Date: 2026-09-07
-- Description: adopt_enrolled_perspective_retention(), the automatic half of the retention
--              adoption gate (issue #712).
--
--   Migration 104 gates the enrolled reap behind wh_perspective_registry.retention_enforcement_acknowledged
--   so a deploy cannot silently drain a historical backlog: a newly enrolled perspective reports what
--   it would remove and removes nothing until acknowledged. The only thing that set the flag was a
--   coordinator call no consumer had a reason to know about, so a declared window never started
--   reaping: the reconciler enrolled the perspective at startup, the stamped-expiry sweep ran, and
--   reap_enrolled_perspective_rows skipped every unacknowledged row forever. The framework's own
--   backlog function reported thousands of rows past the window on a deployment; nothing acted.
--
--   The gate exists for two reasons named in 104: surprise and load. Load is already handled by
--   chunking (the reap's batch bound, "draining" until clear). Surprise needs a signal, not a human
--   step. This function is that signal: for every enrolled, unacknowledged perspective it reads the
--   backlog through count_perspective_retention_backlog, opens the gate, and returns one row per
--   perspective (registry key, backlog). The maintenance worker calls it immediately before the
--   enrolled reap when PerspectiveRowRetentionOptions.AutoAcknowledge is on (the default), logs each
--   row, and records the backlog on the maintenance meter, so a declaration is in force within one
--   maintenance interval of the deploy and its first day is visible without anyone acting.
--   Idempotent: an acknowledged perspective produces no row. Un-enrolling still clears the flag, so
--   re-adopting goes through this stage again. AutoAcknowledge=false restores 104's manual gate.
--
--   Rows are taken FOR UPDATE SKIP LOCKED: two instances adopting in the same window split the
--   work instead of both reporting the same perspective.
-- Dependencies: 101 (row_retention_enrolled), 104 (retention_enforcement_acknowledged, count_perspective_retention_backlog), 112 (reap_enrolled_perspective_rows honors the gate)
-- Objects: adopt_enrolled_perspective_retention

CREATE OR REPLACE FUNCTION __SCHEMA__.adopt_enrolled_perspective_retention()
RETURNS TABLE(perspective TEXT, backlog BIGINT) AS $$
DECLARE
  v_reg RECORD;
BEGIN
  FOR v_reg IN
    SELECT r.clr_type_name
      FROM __SCHEMA__.wh_perspective_registry r
     WHERE r.row_retention_enrolled AND NOT r.retention_enforcement_acknowledged
     ORDER BY r.clr_type_name
     FOR UPDATE OF r SKIP LOCKED
  LOOP
    perspective := v_reg.clr_type_name;
    -- The number the gate existed to surface, read before the gate opens.
    backlog := __SCHEMA__.count_perspective_retention_backlog(v_reg.clr_type_name);
    UPDATE __SCHEMA__.wh_perspective_registry r
       SET retention_enforcement_acknowledged = TRUE
     WHERE r.clr_type_name = v_reg.clr_type_name;
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.adopt_enrolled_perspective_retention() IS
  'Automatic adoption of declared row retention (144, issue #712): for every enrolled perspective '
  'still gated by 104''s retention_enforcement_acknowledged, reads the backlog the window would '
  'remove, opens the gate, and returns (perspective, backlog). Idempotent; the maintenance worker '
  'runs it immediately before reap_enrolled_perspective_rows when auto-acknowledge is on.';
