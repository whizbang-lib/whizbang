-- Migration: 137_AdaptiveNotifyDebounce
-- Date: 2026-09-06
-- Description: The doorbell debounce (130-133) becomes ADAPTIVE per (instance, payload_kind).
--   A lone/sporadic doorbell fires in real time (a tiny FLOOR window); only a sustained rapid
--   RUN of doorbells toward the same target escalates suppression to the CEILING window
--   (notify_debounce_seconds). This aligns notify latency with the delivery strategy WITHOUT
--   static per-subscription config: the controller measures the workload. An interactive chat
--   command (sporadic) is delivered on its doorbell; a bulk-import fan-out (flood) is debounced
--   and drained on the linger poll, exactly as before — and a service that is interactive today
--   but high-volume tomorrow transitions on its own, no reconfiguration.
--
--   ROOT CAUSE it fixes: the fixed-window debounce (130) armed suppression on ANY recent
--   found-work (a fresh last_work_at watermark). A single interactive message landing within
--   the window of unrelated prior work had its only prompt doorbell suppressed and was stranded
--   on the drainer's adaptive poll cap — the #677 class (parts 2/3 fixed the stamping honesty;
--   this is the same stranding reached by a different door: recent-activity != flood). Observed
--   at 9-16 s on an interactive command path. Requiring a FLOOD (not mere recent activity) to
--   suppress removes the stranding for interactive traffic while keeping the #665 churn win.
--
--   INVARIANTS preserved (all of #677):
--     * Suppression still requires a LIVE target (a corpse's watermark must fire so the
--       deterministic re-target path engages, 130).
--     * Suppression still requires a fresh found-work watermark (last_work_at, armed ONLY by
--       claim_work on drainable work, 131/133) — the same condition that arms the C# drain linger.
--     * A fire NEVER arms suppression: rate-tracking rows the controller creates on a fire carry
--       last_work_at = NULL ("claim_work never found work here"), which fails the freshness
--       predicate and so cannot self-suppress (131's #677 part-1, type-enforced).
--     * The slide-on-suppress is retained (a suppressed store IS work the linger poll finds).
--     * ceiling <= 0 (notify_debounce_seconds) remains the global OFF switch: no suppression.
--
--   SIGNATURE UNCHANGED (deliberate): _notify_debounced keeps its 130 signature
--   (p_instance_id, p_payload, p_window) — a pure CREATE OR REPLACE, no DROP, no new overload.
--   p_window is reinterpreted as the CEILING seconds (notify_instance_owners already passes
--   notify_debounce_seconds there, so that caller is UNCHANGED); the floor/rapid-gap/churn knobs
--   are read inside from wh_settings. This avoids an arg-list change, whose signature-qualified
--   DROP silently no-ops under the schema-generator's inlined runner (pooled EF connections strip
--   search_path) and left both overloads, making a later no-arg reference ambiguous (42725).
--
--   OBSERVABILITY (a controller nobody can see is one nobody can debug): each
--   (instance, payload_kind) row now carries rapid_run, effective_window_ms, fired_count and
--   suppressed_count — the current regime and the fire/suppress volume — read by the metrics
--   reader and emitted as OpenTelemetry gauges/counters (see the C# notify-metrics reader).
--
-- Dependencies: 130 (wh_notify_state, notify_instance_owners, _notify_debounced),
--   131 (arms-on-found-work), 132/133 (claim_work honest arming), 028 (wh_settings)
-- Objects: wh_notify_state (+cols), _notify_debounced (CREATE OR REPLACE, same signature),
--   wh_settings seed. notify_instance_owners is intentionally NOT touched.

-- ============================================================================
-- Schema: rate-tracking + observability state on the per-target watermark row
-- ============================================================================
ALTER TABLE __SCHEMA__.wh_notify_state
  ADD COLUMN IF NOT EXISTS last_attempt_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS rapid_run INTEGER NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS effective_window_ms INTEGER NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS fired_count BIGINT NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS suppressed_count BIGINT NOT NULL DEFAULT 0;

-- last_work_at was NOT NULL (130): every row was born from claim_work stamping found work. The
-- adaptive controller now also creates rows on a FIRE to carry last_attempt_at/rapid_run; those
-- rows must NOT arm suppression, so their last_work_at is NULL ("claim_work never found work
-- here"). NULL fails the freshness predicate, so a fire-born row cannot self-suppress — the
-- #677 part-1 invariant, now enforced by the type system rather than a predicted-awake stamp.
ALTER TABLE __SCHEMA__.wh_notify_state ALTER COLUMN last_work_at DROP NOT NULL;

COMMENT ON TABLE __SCHEMA__.wh_notify_state IS
'Adaptive doorbell-debounce state (137): one row per (instance, payload kind). last_work_at is the found-work watermark (armed ONLY by claim_work on drainable work, 131/133; NULL = never armed, so it can never suppress). last_attempt_at + rapid_run are the controller''s rate signal: consecutive doorbells closer than notify_rapid_gap_ms advance rapid_run, a wider gap resets it; once rapid_run reaches notify_churn_run the effective window escalates from notify_debounce_floor_ms to the ceiling notify_debounce_seconds. effective_window_ms/fired_count/suppressed_count are the OpenTelemetry surface. Rows for departed instances age out by most-recent activity.';

-- Adaptive settings (the ceiling reuses the existing notify_debounce_seconds from 130).
INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description) VALUES
  ('notify_debounce_floor_ms', '50', 'integer',
   'Adaptive notify (137): the FLOOR suppression window in ms, applied when a target is NOT in a doorbell flood. ~50 ms collapses only near-simultaneous double-doorbells; a lone/sporadic doorbell effectively always fires. The interactive delivery-latency floor. Ignored when the ceiling (notify_debounce_seconds) is <= 0.'),
  ('notify_rapid_gap_ms', '100', 'integer',
   'Adaptive notify (137): two consecutive doorbells toward the same (instance, payload_kind) closer than this many ms count as RAPID and advance rapid_run; a wider gap resets it to 0. The volume axis of the controller.'),
  ('notify_churn_run', '5', 'integer',
   'Adaptive notify (137): rapid_run must reach this many consecutive rapid doorbells before suppression escalates from the floor to the ceiling (notify_debounce_seconds). Below it, doorbells fire — the first few of any burst always ring so latency stays low until a flood is certain.')
ON CONFLICT (setting_key) DO NOTHING;

-- ============================================================================
-- _notify_debounced — adaptive suppress-or-fire for one target instance.
-- SAME 130 signature (p_instance_id, p_payload, p_window): a pure CREATE OR REPLACE. p_window is
-- the CEILING seconds (notify_instance_owners passes notify_debounce_seconds, unchanged); the
-- floor/rapid-gap/churn knobs are read here from wh_settings. No DROP, no new overload.
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._notify_debounced(
  p_instance_id UUID,
  p_payload TEXT,
  p_window INTEGER
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_live BOOLEAN;
  v_last_attempt TIMESTAMPTZ;
  v_last_work TIMESTAMPTZ;
  v_rapid_run INTEGER;
  v_floor_ms INTEGER;
  v_rapid_gap_ms INTEGER;
  v_churn_run INTEGER;
  v_gap_ms DOUBLE PRECISION;
  v_effective_ms INTEGER;
  v_suppress BOOLEAN := FALSE;
BEGIN
  -- Adaptive knobs (the ceiling arrives as p_window; read the floor/rapid-gap/churn once).
  SELECT COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_debounce_floor_ms'), 50),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_rapid_gap_ms'), 100),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_churn_run'), 5)
  INTO v_floor_ms, v_rapid_gap_ms, v_churn_run;

  -- Only a LIVE target may ever be suppressed: a corpse's fresh watermark must not strand work
  -- — its doorbell must fire so the deterministic re-target path engages (130).
  SELECT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances si
    WHERE si.instance_id = p_instance_id
      AND si.last_heartbeat_at > v_now - INTERVAL '30 seconds')
  INTO v_live;

  -- This target's current state (row may not exist yet — first doorbell after idle).
  SELECT last_attempt_at, last_work_at, rapid_run
    INTO v_last_attempt, v_last_work, v_rapid_run
  FROM __SCHEMA__.wh_notify_state
  WHERE instance_id = p_instance_id AND payload_kind = p_payload;

  -- VOLUME axis: advance the rapid run while doorbells arrive closer than the rapid gap; reset
  -- on the first calm gap. No prior attempt = calm (a lone doorbell after idle).
  v_gap_ms := CASE WHEN v_last_attempt IS NULL THEN NULL
                   ELSE EXTRACT(EPOCH FROM (v_now - v_last_attempt)) * 1000 END;
  IF v_gap_ms IS NOT NULL AND v_gap_ms < v_rapid_gap_ms THEN
    v_rapid_run := COALESCE(v_rapid_run, 0) + 1;
  ELSE
    v_rapid_run := 0;
  END IF;

  -- TIME axis: floor window normally; escalate to the ceiling (p_window seconds) once the run
  -- trips churn. p_window <= 0 is the global OFF switch — suppression disabled (floor ignored).
  IF p_window <= 0 THEN
    v_effective_ms := 0;
  ELSIF v_rapid_run >= v_churn_run THEN
    v_effective_ms := p_window * 1000;
  ELSE
    v_effective_ms := GREATEST(v_floor_ms, 0);
  END IF;

  -- Suppress iff: live AND the drainer is genuinely draining this kind (found-work watermark
  -- fresh within the EFFECTIVE window). A NULL last_work_at (fire-born row, claim_work never
  -- armed) can never satisfy this — "a fire never arms suppression" (131), by construction.
  IF v_live AND v_effective_ms > 0 AND v_last_work IS NOT NULL
     AND v_last_work > v_now - (v_effective_ms * INTERVAL '1 millisecond') THEN
    v_suppress := TRUE;
  END IF;

  IF v_suppress THEN
    -- Slide the found-work watermark (a suppressed store IS work the linger poll will find) and
    -- record the attempt + rate state + the suppressed count for OTel.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_work_at = v_now,
        last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        suppressed_count = suppressed_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_payload;
  ELSE
    PERFORM pg_notify('wh_work_i_' || p_instance_id::text, p_payload);
    -- Record the attempt + rate state + fired count WITHOUT arming suppression: on INSERT
    -- last_work_at stays NULL (only claim_work arms it); on CONFLICT it is left untouched.
    INSERT INTO __SCHEMA__.wh_notify_state
      (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run,
       effective_window_ms, fired_count)
    VALUES (p_instance_id, p_payload, NULL, v_now, v_rapid_run, v_effective_ms, 1)
    ON CONFLICT (instance_id, payload_kind) DO UPDATE
      SET last_attempt_at = EXCLUDED.last_attempt_at,
          rapid_run = EXCLUDED.rapid_run,
          effective_window_ms = EXCLUDED.effective_window_ms,
          fired_count = __SCHEMA__.wh_notify_state.fired_count + 1;
    -- Opportunistic hygiene on the rare fire path: rows for long-departed instances, aged by
    -- most-recent activity (either watermark) so fire-born NULL-last_work_at rows also expire.
    DELETE FROM __SCHEMA__.wh_notify_state
    WHERE GREATEST(COALESCE(last_work_at, 'epoch'::timestamptz),
                   COALESCE(last_attempt_at, 'epoch'::timestamptz)) < v_now - INTERVAL '7 days';
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._notify_debounced(UUID, TEXT, INTEGER) IS
'Adaptive debounced doorbell for one target (137): p_window is the CEILING seconds. Floor window when calm (a lone doorbell fires in real time), ceiling window once rapid_run reaches notify_churn_run. Suppression requires a live target AND a fresh found-work watermark (last_work_at, armed only by claim_work) — a fire never arms it (last_work_at NULL). p_window <= 0 fires always (off switch). Records rapid_run/effective_window_ms/fired_count/suppressed_count for observability. floor/rapid-gap/churn read from wh_settings.';
