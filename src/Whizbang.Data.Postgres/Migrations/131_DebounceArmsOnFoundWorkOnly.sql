-- Migration: 131_DebounceArmsOnFoundWorkOnly
-- Date: 2026-09-05
-- Description: The doorbell debounce may arm ONLY on found work (issue #677). 130 stamped a
--   "predicted-awake" watermark on every FIRE, on the theory that the doorbell it just sent
--   wakes the drainer, so the burst behind it could be suppressed. But the C# side arms its
--   drain linger — the polling that makes suppression safe — only when a claim actually
--   FINDS work. The prediction fails exactly in the fenced-commit sequence the make-up
--   doorbell (118) exists for: the commit-time ring wakes a claim that finds NOTHING (the
--   row is fence-held, pre-visibility), the linger never arms, and the fire's own stamp
--   then suppresses the fenced-retry's make-up ring — the only prompt wake for the
--   now-visible row. Visibility quantizes to the adaptive poll cap (observed: 10.4 s
--   against a 1.5 s test budget).
--
--   Fix: drop the predicted-awake stamp. The watermark is now written by exactly one party
--   — claim_work, when the instance finds work (126) — which is the same condition that
--   arms the C# linger. Suppression and linger can no longer disagree. The #665 storm this
--   debounce was built for is still covered: a bulk ingest's drainer is finding work on
--   every claim, so its watermark stays fresh through the found-work stamp, and suppressed
--   stores still slide it. The only doorbells this change re-fires are those toward an
--   instance woken but not yet (or no longer) finding work — precisely the ones that must
--   not be swallowed.
--
--   Suppression semantics, kind-keying, the live-instance guard, the slide-on-suppress,
--   and the off switch (non-positive setting) are UNCHANGED from 130.
-- Dependencies: 126 (claim_work found-work stamp), 130 (wh_notify_state, notify_instance_owners)
-- Objects: _notify_debounced

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
    -- 131: NO predicted-awake stamp here. Arming suppression is claim_work's job alone
    -- (found work, 126) — the same condition that arms the C# drain linger. A fire that
    -- stamped its own watermark could suppress the follow-up ring toward an instance whose
    -- woken claim found nothing (fenced/pre-visibility), stranding the work on the
    -- adaptive/backstop cadence with no linger polling to cover it (issue #677).
    IF p_window > 0 THEN
      -- Opportunistic hygiene on the rare fire path: rows for long-departed instances.
      DELETE FROM __SCHEMA__.wh_notify_state WHERE last_work_at < NOW() - INTERVAL '7 days';
    END IF;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._notify_debounced IS
'Debounced doorbell for one target (131 supersedes 130): while the target''s watermark is fresher than p_window seconds AND the target is live, the notify is suppressed and the watermark slides (the store is work the linger poll will find). Otherwise pg_notify fires. The watermark is armed ONLY by claim_work finding work (126) — never by the fire itself — so SQL suppression and the C# drain linger arm on the same condition and a woken-but-empty claim can''t swallow the follow-up ring (issue #677). p_window <= 0 always fires — the off switch.';
