-- Migration: 056_DeadLetterReadyNotify.sql
-- Date: 2026-06-10
-- Description: Fires a 'deadletter' NOTIFY on every active instance's channel when a
--              wh_dead_letters row is inserted. DeadLetterRecoveryWorker subscribes via
--              IWorkNotificationListener.OnSignal(DeadLetterReady) and wakes immediately,
--              instead of polling on its ScanIntervalMinutes (10 min default).
-- Dependencies: 050 (wh_dead_letters), 052 (wh_live_instances) — uses the live-instances
--               view so a stopped pod doesn't get a phantom NOTIFY.

-- Table refs (wh_dead_letters, wh_service_instances) stay BARE — matches the convention in
-- 050/053 where the table is global to the default schema, not per-bounded-context. Function
-- name is __SCHEMA__-qualified so each schema's migration produces a no-op or distinct fn;
-- the trigger DROP+CREATE points at the most-recently-migrated schema's copy — semantically
-- safe because every copy does the same fan-out.
CREATE OR REPLACE FUNCTION __SCHEMA__._notify_dead_letter_ready() RETURNS TRIGGER AS $$
BEGIN
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
