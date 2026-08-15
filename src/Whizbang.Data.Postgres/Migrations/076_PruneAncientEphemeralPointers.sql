-- Migration: 076_PruneAncientEphemeralPointers.sql
-- Date: 2026-07-16
-- Description: Tier-2 deep-maintenance pointer prune (E1 #13b3). The tier-1 reaper (073) deletes consumed,
--              aged ephemeral BODIES (wh_event_body) but deliberately LEAVES the wh_event_store pointer as
--              the #13d rebuild-guard signal (pointer-present / body-NULL). Over a long horizon those
--              pointers accumulate; this optional, DISABLED-BY-DEFAULT, self-gated (~monthly) cycle prunes
--              the ANCIENT ones for storage economy — while KEEPING THE NEWEST pointer per stream (a
--              "tombstone") so BOTH the rebuild guard (GetEphemeralStreamIdsAsync WHERE flags&8=8) and the
--              perspective cursor's last_event_id target survive the prune. This is what the plan means by
--              "the guard keys off stream mode, so pruning pointers never weakens it": one surviving
--              ephemeral pointer keeps the stream flagged ephemeral forever.
--              A pointer is pruned only when: ephemeral (flags&8=8) AND its body is already reaped (no
--              wh_event_body row) AND it is past a horizon that can never be shorter than the dedup window
--              AND it has no pending perspective work AND it is not the newest pointer for its stream.
--              Skipped under debug_mode (forensic retention, like the reaper). Opt-in: the operator sets
--              ephemeral_deep_maintenance_enabled=true.
-- Dependencies: 073 (tier-1 reaper + wh_event_body), 046 (wh_event_store.version), 032 (wh_settings + dedup_retention_days)

-- Settings (all defaulted; the enable flag is FALSE so this migration is inert until an operator opts in).
INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description) VALUES
  ('ephemeral_deep_maintenance_enabled', 'false', 'boolean',
   'Opt-in killswitch for the tier-2 ephemeral pointer prune (#13b3). FALSE = never prune append-only ephemeral pointers.'),
  ('ephemeral_pointer_retention_days', '90', 'integer',
   'Ancient-ephemeral-pointer prune horizon (days). A reaped ephemeral pointer is prune-eligible only once older than this. Set >= your dedup + cross-service replay windows; the prune floors it at dedup_retention_days.'),
  ('ephemeral_pointer_prune_interval_days', '30', 'integer',
   'Self-gate cadence (days) for the tier-2 pointer prune. It actually prunes at most once per interval regardless of how often perform-maintenance ticks.'),
  ('ephemeral_pointer_prune_last_run', '1970-01-01T00:00:00Z', 'timestamptz',
   'Watermark for the tier-2 pointer-prune self-gate (atomic CAS). Do not edit manually.')
ON CONFLICT (setting_key) DO NOTHING;

-- Prune ancient, reaped ephemeral pointers. Standalone (NOT folded into perform_maintenance) so the heavy
-- monthly scan+delete is decoupled from the 10-minute tier-1 cadence; the caller invokes it every cycle and
-- the function self-gates on the interval + enable flag, so it is a cheap no-op when disabled or not due.
CREATE OR REPLACE FUNCTION __SCHEMA__.prune_ancient_ephemeral_pointers()
RETURNS TABLE(rows_pruned BIGINT, status TEXT) AS $$
DECLARE
  v_enabled BOOLEAN;
  v_debug BOOLEAN;
  v_retention_days INTEGER;
  v_dedup_days INTEGER;
  v_interval_days INTEGER;
  v_horizon_days INTEGER;
  v_claimed BIGINT;
  v_rows BIGINT;
BEGIN
  -- Opt-in gate. Pruning append-only pointers is a deliberate storage-economy choice; off by default.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_deep_maintenance_enabled'),
    FALSE) INTO v_enabled;
  IF NOT v_enabled THEN
    RETURN QUERY SELECT 0::BIGINT, 'disabled'::TEXT;
    RETURN;
  END IF;

  -- debug_mode retains forensic rows — the reaper (073) and completed-message purges honor it, so must this.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'),
    FALSE) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT 0::BIGINT, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_prune_interval_days'),
    30) INTO v_interval_days;

  -- Self-gate (multi-pod safe): atomically claim this tick by advancing the watermark ONLY if the interval
  -- has elapsed. If no row is updated, another pod already claimed it this interval (or it is not yet due) —
  -- return without pruning. The conditional UPDATE is the atomic CAS; no advisory lock needed.
  UPDATE __SCHEMA__.wh_settings
    SET setting_value = NOW()::TEXT
    WHERE setting_key = 'ephemeral_pointer_prune_last_run'
      AND setting_value::TIMESTAMPTZ < NOW() - (v_interval_days * INTERVAL '1 day');
  GET DIAGNOSTICS v_claimed = ROW_COUNT;
  IF v_claimed = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 'not due'::TEXT;
    RETURN;
  END IF;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_retention_days'),
    90) INTO v_retention_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'),
    30) INTO v_dedup_days;
  -- Safety floor: the horizon can NEVER be shorter than the dedup window — a still-deduped redelivery must
  -- never find its pointer already gone. Operators widen ephemeral_pointer_retention_days for longer
  -- cross-service replay windows; they can never accidentally shorten it below dedup.
  v_horizon_days := GREATEST(v_retention_days, v_dedup_days);

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE (es.flags & 8) = 8                                            -- ephemeral pointers only
    AND es.created_at < NOW() - (v_horizon_days * INTERVAL '1 day')   -- past the horizon
    AND NOT EXISTS (                                                  -- body already reaped by tier-1 (073)
      SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
    AND NOT EXISTS (                                                  -- no pending perspective work item
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
    AND es.version < (                                                -- KEEP the newest pointer per stream:
      SELECT MAX(es2.version) FROM __SCHEMA__.wh_event_store es2       -- the surviving "tombstone" keeps the
      WHERE es2.stream_id = es.stream_id);                            -- stream flagged ephemeral (guard) and
                                                                      -- is the cursor's last_event_id target.
  GET DIAGNOSTICS v_rows = ROW_COUNT;

  RETURN QUERY SELECT v_rows, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.prune_ancient_ephemeral_pointers IS
'Tier-2 deep maintenance (#13b3): prunes ANCIENT ephemeral wh_event_store pointers whose bodies are already reaped (tier-1, 073), past GREATEST(ephemeral_pointer_retention_days, dedup_retention_days), with no pending perspective work — while KEEPING the newest pointer per stream so the rebuild guard (flags&8) and the cursor last_event_id both survive. Opt-in (ephemeral_deep_maintenance_enabled), self-gated to ephemeral_pointer_prune_interval_days (atomic CAS on ephemeral_pointer_prune_last_run), skipped under debug_mode. Returns (rows_pruned, status): disabled | skipped (debug_mode=true) | not due | ok.';
