-- Migration: 130_NotifyDebounce
-- Date: 2026-09-04
-- Description: Doorbell debounce (issue #665). Under fan-out load, store_*_messages fired
--   one pg_notify per message via notify_instance_owners — measured at a double-digit
--   share of database CPU during a bulk ingest, nearly all redundant because the target
--   instance was already awake and draining. wh_notify_state holds a per-instance
--   last_work_at watermark: claim_work stamps it whenever the instance finds work (see
--   126, zero extra round trips), and _notify_debounced suppresses a notify while the
--   watermark is fresher than the notify_debounce_seconds setting (default 7), sliding
--   the watermark instead — the suppressed store IS work the drainer's linger poll will
--   find. The C# drain linger (default 8 s, Whizbang:Workers:Claim:NotifyDrainLingerSeconds)
--   outlives the window by design: the suppression self-expires before the drainer stops
--   polling, so no sleep handshake is needed and clock skew up to the 1 s margin is safe.
--   Suppression NEVER applies toward a non-live instance (its doorbell must fire so the
--   deterministic re-target machinery engages), and a non-positive setting disables the
--   debounce entirely. The polling backstop remains the correctness floor throughout.
-- Dependencies: 028 (wh_settings), 045 (notify_instance_owners), 126 (claim_work stamp)
-- Objects: wh_notify_state, _notify_debounced, notify_instance_owners

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_notify_state (
  instance_id  UUID NOT NULL,
  payload_kind VARCHAR(20) NOT NULL,
  last_work_at TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (instance_id, payload_kind)
);

COMMENT ON TABLE __SCHEMA__.wh_notify_state IS
'Doorbell-debounce watermarks (130): one row per (instance, payload kind) — an outbox doorbell must never swallow a perspective one, last_work_at stamped by claim_work when the instance finds work and slid by suppressed stores. While fresher than the notify_debounce_seconds setting, notifies toward that instance are suppressed — it is draining or lingering and will find the work by polling. Rows for departed instances age out harmlessly (suppression requires a live heartbeat) and are pruned opportunistically.';

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
VALUES ('notify_debounce_seconds', '7', 'integer',
        'Doorbell debounce window: notifies toward an instance whose wh_notify_state watermark is fresher than this many seconds are suppressed (the drainer is awake and polling). MUST stay below the C# drain linger (Whizbang:Workers:Claim:NotifyDrainLingerSeconds, default 8) so the suppression self-expires while the drainer still polls. Non-positive disables the debounce.')
ON CONFLICT (setting_key) DO NOTHING;

-- ============================================================================
-- _notify_debounced — suppress-or-fire for one target instance
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__._notify_debounced(
  p_instance_id UUID,
  p_payload TEXT,
  p_window INTEGER
) RETURNS VOID AS $$
DECLARE
  v_suppressed BOOLEAN := FALSE;
BEGIN
  IF p_window > 0 THEN
    -- Suppress + slide in ONE statement, keyed per (instance, payload kind) — an outbox
    -- doorbell must never swallow a perspective one; each kind's consumers earn their own
    -- suppression only from their own kind's freshness. And only toward a LIVE instance:
    -- a corpse's fresh watermark must not strand work — its doorbell fires so the
    -- re-targeting machinery engages.
    UPDATE __SCHEMA__.wh_notify_state ns
    SET last_work_at = NOW()
    WHERE ns.instance_id = p_instance_id
      AND ns.payload_kind = p_payload
      AND ns.last_work_at > NOW() - make_interval(secs => p_window)
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = p_instance_id
          AND si.last_heartbeat_at > NOW() - INTERVAL '30 seconds');
    v_suppressed := FOUND;
  END IF;

  IF NOT v_suppressed THEN
    PERFORM pg_notify('wh_work_i_' || p_instance_id::text, p_payload);
    IF p_window > 0 THEN
      -- Predicted-awake stamp: this doorbell wakes the drainer, so the burst behind the
      -- first store of an idle-to-busy edge is suppressed — one doorbell per edge per kind.
      INSERT INTO __SCHEMA__.wh_notify_state (instance_id, payload_kind, last_work_at)
      VALUES (p_instance_id, p_payload, NOW())
      ON CONFLICT (instance_id, payload_kind) DO UPDATE SET last_work_at = NOW();
      -- Opportunistic hygiene on the rare fire path: rows for long-departed instances.
      DELETE FROM __SCHEMA__.wh_notify_state WHERE last_work_at < NOW() - INTERVAL '7 days';
    END IF;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._notify_debounced IS
'Debounced doorbell for one target (130): while the target''s watermark is fresher than p_window seconds AND the target is live, the notify is suppressed and the watermark slides (the store is work the linger poll will find). Otherwise pg_notify fires and stamps a predicted-awake watermark. p_window <= 0 always fires — the off switch.';

-- ============================================================================
-- notify_instance_owners — VERBATIM targeting from 045; emission goes through
-- _notify_debounced with the setting read once per call.
-- ============================================================================
SELECT __SCHEMA__.drop_all_overloads('notify_instance_owners');

CREATE OR REPLACE FUNCTION __SCHEMA__.notify_instance_owners(
  p_payload TEXT,
  p_stream_ids UUID[]
) RETURNS VOID AS $$
DECLARE
  v_unclaimed_streams UUID[];
  v_active_count INTEGER;
  v_debounce INTEGER;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
     WHERE setting_key = 'notify_debounce_seconds'), 7)
  INTO v_debounce;

  -- Step 1 (unchanged targeting): per-owner notify for streams in wh_active_streams.
  PERFORM __SCHEMA__._notify_debounced(a.assigned_instance_id, p_payload, v_debounce)
  FROM (
    SELECT DISTINCT assigned_instance_id
    FROM __SCHEMA__.wh_active_streams
    WHERE stream_id = ANY(p_stream_ids)
      AND assigned_instance_id IS NOT NULL
  ) a;

  -- Step 2 (unchanged targeting): deterministic-target notify for unclaimed streams.
  SELECT ARRAY_AGG(s) INTO v_unclaimed_streams
  FROM unnest(p_stream_ids) AS s
  WHERE NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = s
      AND a.assigned_instance_id IS NOT NULL
  );

  IF v_unclaimed_streams IS NULL OR cardinality(v_unclaimed_streams) = 0 THEN
    RETURN;
  END IF;

  SELECT COUNT(*)::INTEGER INTO v_active_count
  FROM __SCHEMA__.wh_service_instances
  WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds';

  IF v_active_count = 0 THEN
    RETURN;
  END IF;

  IF p_payload = 'outbox' THEN
    PERFORM __SCHEMA__._notify_debounced(targets.target_instance_id, p_payload, v_debounce)
    FROM (
      WITH src AS (
        SELECT partition_number
        FROM __SCHEMA__.wh_outbox
        WHERE stream_id = ANY(v_unclaimed_streams)
      ),
      live AS (
        SELECT instance_id,
               (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM __SCHEMA__.wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT DISTINCT live.instance_id AS target_instance_id
      FROM src
      JOIN live ON live.rank = (src.partition_number % v_active_count)
      WHERE src.partition_number IS NOT NULL
    ) AS targets;
  ELSIF p_payload = 'inbox' THEN
    PERFORM __SCHEMA__._notify_debounced(targets.target_instance_id, p_payload, v_debounce)
    FROM (
      WITH src AS (
        SELECT partition_number
        FROM __SCHEMA__.wh_inbox
        WHERE stream_id = ANY(v_unclaimed_streams)
      ),
      live AS (
        SELECT instance_id,
               (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM __SCHEMA__.wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT DISTINCT live.instance_id AS target_instance_id
      FROM src
      JOIN live ON live.rank = (src.partition_number % v_active_count)
      WHERE src.partition_number IS NOT NULL
    ) AS targets;
  ELSIF p_payload = 'perspective' THEN
    PERFORM __SCHEMA__._notify_debounced(targets.target_instance_id, p_payload, v_debounce)
    FROM (
      WITH src AS (
        SELECT partition_number
        FROM __SCHEMA__.wh_perspective_events
        WHERE stream_id = ANY(v_unclaimed_streams)
      ),
      live AS (
        SELECT instance_id,
               (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM __SCHEMA__.wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT DISTINCT live.instance_id AS target_instance_id
      FROM src
      JOIN live ON live.rank = (src.partition_number % v_active_count)
      WHERE src.partition_number IS NOT NULL
    ) AS targets;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_instance_owners IS
'Slice 27 + v0.685 + 130 — instance-routed NOTIFY emission with doorbell debounce. Targeting is unchanged (per-owner for pinned streams, rank-deterministic for unclaimed); every emission goes through _notify_debounced, which suppresses toward a live instance whose wh_notify_state watermark is fresher than notify_debounce_seconds (default 7) and slides the watermark instead. The C# drain linger (default 8 s) outlives the window, so suppression self-expires while the drainer still polls.';
