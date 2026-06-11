-- Migration: 056_DeadLetterReadyNotify.sql
-- Date: 2026-06-10
-- Description: Fires a 'deadletter' NOTIFY on every active instance's channel when a
--              wh_dead_letters row is inserted. DeadLetterRecoveryWorker subscribes via
--              IWorkNotificationListener.OnSignal(DeadLetterReady) and wakes immediately,
--              instead of polling on its ScanIntervalMinutes (10 min default).
-- Dependencies: 050 (wh_dead_letters), 052 (wh_live_instances) — uses the live-instances
--               view so a stopped pod doesn't get a phantom NOTIFY.

CREATE OR REPLACE FUNCTION __SCHEMA__._notify_dead_letter_ready() RETURNS TRIGGER AS $$
BEGIN
  -- Fan out to every active instance. The per-instance channel pattern is established by
  -- migration 048's notify_instance_owners; we reuse the same channel-name convention so
  -- the existing PgWorkNotificationListener subscriptions pick this up without any new
  -- subscribe-side wiring.
  PERFORM pg_notify('wh_work_i_' || instance_id::text, 'deadletter')
  FROM __SCHEMA__.wh_service_instances
  WHERE last_heartbeat_at > NOW() - INTERVAL '5 minutes';
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._notify_dead_letter_ready IS
  'AFTER INSERT trigger fn on wh_dead_letters; broadcasts "deadletter" payload to every '
  'active instance''s wh_work_i_<id> channel so DeadLetterRecoveryWorker wakes within ms '
  'of the row insert (vs. up to 10 min on the polling-only path).';

DROP TRIGGER IF EXISTS trg_wh_dead_letters_notify ON __SCHEMA__.wh_dead_letters;
CREATE TRIGGER trg_wh_dead_letters_notify
  AFTER INSERT ON __SCHEMA__.wh_dead_letters
  FOR EACH ROW
  EXECUTE FUNCTION __SCHEMA__._notify_dead_letter_ready();
