-- Migration: 109_InstanceState.sql
-- Date: 2026-08-16
-- Description: Each instance records its own lifecycle phase and library version on its instance
--              row. LifecyclePhase exists today but lives only in process memory, so no instance
--              can see any other's — and neither can anything else. Two independent requirements
--              land on that same missing fact: the standby handshake cannot wait for peers to
--              reach a state nobody can observe, and a load-balanced status surface has to report
--              on instances it is not — it can only report what they have written down. The
--              version matters most in exactly the situation that motivated the startup pipeline:
--              during a mixed-version rollout, "which instances are on which version" is the
--              first question anyone asks.
--
--              record_instance_state deliberately BYPASSES record_heartbeat's ten-second
--              freshness guard: the guard exists to stop liveness ticks from writing constantly,
--              and a transition is not a tick — it is the one write that must not be deferred,
--              since the whole handshake turns on peers seeing it promptly. It equally
--              deliberately does NOT touch last_heartbeat_at: state is not liveness, and a
--              standing-by zombie must still be reapable.
-- Dependencies: 010 (wh_service_instances)

ALTER TABLE __SCHEMA__.wh_service_instances ADD COLUMN IF NOT EXISTS lifecycle_phase TEXT;
ALTER TABLE __SCHEMA__.wh_service_instances ADD COLUMN IF NOT EXISTS library_version TEXT;

COMMENT ON COLUMN __SCHEMA__.wh_service_instances.lifecycle_phase IS
'The instance''s own report of its lifecycle phase (Starting/Connecting/Migrating/Running/StandingBy/...).
Written by record_instance_state on each transition; freshness is bounded by the row''s own heartbeat.';
COMMENT ON COLUMN __SCHEMA__.wh_service_instances.library_version IS
'The Whizbang library version the instance''s binary runs. During a mixed-version rollout this is
the first question anyone asks, and it is unanswerable from an instance that only knows its own.';

-- ============================================================================
-- record_instance_state — a transition is not a tick.
-- Returns TRUE when the row existed and was updated; FALSE when it did not
-- (early startup transitions happen before the instance has heartbeated its
-- row into existence — expected, not an error). NULL version keeps whatever
-- is already on record: a phase transition without a version in hand must
-- not erase the answer to the rollout's first question.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.record_instance_state(
  p_instance_id UUID,
  p_lifecycle_phase TEXT,
  p_library_version TEXT DEFAULT NULL
) RETURNS BOOLEAN AS $$
BEGIN
  UPDATE __SCHEMA__.wh_service_instances
  SET lifecycle_phase = p_lifecycle_phase,
      library_version = COALESCE(p_library_version, library_version)
  WHERE instance_id = p_instance_id;

  RETURN FOUND;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.record_instance_state IS
'Records an instance''s lifecycle phase (and, when supplied, library version) on its own row.
Bypasses the heartbeat freshness guard on purpose (a transition is not a tick) and never touches
last_heartbeat_at (state is not liveness).';
