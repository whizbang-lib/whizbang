-- Migration: 049_NotifyScheduledRetryDue.sql
-- Date: 2026-06-03 (v0.502 slice B.4 — NOTIFY-first scheduled retries)
-- Description: Low-cadence helper that finds rows whose retry-after timestamp has elapsed
--              and emits one pg_notify per owning live instance so each ClaimWorker runs a
--              catch-up claim. Replaces the role that the 250ms ClaimWorker baseline poll
--              played for scheduled_for discovery — keeps the polling cadence tight enough
--              for retry latency without paying the 250ms tax on the rest of the system.
-- Dependencies: 001-048 (requires wh_outbox, wh_inbox, notify_instance_owners function)

SELECT __SCHEMA__.drop_all_overloads('notify_scheduled_retry_due');

CREATE OR REPLACE FUNCTION __SCHEMA__.notify_scheduled_retry_due()
RETURNS TABLE(category TEXT, stream_count INTEGER) AS $$
DECLARE
  v_outbox_streams UUID[];
  v_inbox_streams UUID[];
  v_now TIMESTAMPTZ := NOW();
BEGIN
  -- Outbox rows whose scheduled_for time has elapsed and which haven't been processed.
  -- Filter to NOT NULL stream_id because notify_instance_owners can't deliver to a row
  -- with no owning stream. (Stream-less retries are handled by the regular claim poll.)
  --
  -- Multi-schema fix: tables MUST be __SCHEMA__-qualified. Unqualified FROM clauses get
  -- resolved via search_path which defaults to public — when the function is invoked
  -- from an inventory/bff schema (the InMemory + ECommerce hosts pattern) it tried to
  -- read public.wh_outbox / public.wh_inbox, which don't exist; every 10 s cycle threw
  -- 42P01 and the surrounding noise stalled perspective discovery.
  SELECT ARRAY_AGG(DISTINCT stream_id) INTO v_outbox_streams
  FROM __SCHEMA__.wh_outbox
  WHERE processed_at IS NULL
    AND scheduled_for IS NOT NULL
    AND scheduled_for <= v_now
    AND stream_id IS NOT NULL;

  -- Inbox rows — same shape.
  SELECT ARRAY_AGG(DISTINCT stream_id) INTO v_inbox_streams
  FROM __SCHEMA__.wh_inbox
  WHERE processed_at IS NULL
    AND scheduled_for IS NOT NULL
    AND scheduled_for <= v_now
    AND stream_id IS NOT NULL;

  -- Note on wh_perspective_events: the current schema doesn't carry a scheduled_for column.
  -- Retries on perspective events ride the claim_orphaned_perspective_events lease-expiry
  -- path, which is NOTIFY-eligible via the regular work signals. If scheduled_for is added
  -- to wh_perspective_events in the future, add a third SELECT/notify call here.

  IF v_outbox_streams IS NOT NULL AND array_length(v_outbox_streams, 1) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('outbox', v_outbox_streams);
    category := 'outbox';
    stream_count := array_length(v_outbox_streams, 1);
    RETURN NEXT;
  END IF;

  IF v_inbox_streams IS NOT NULL AND array_length(v_inbox_streams, 1) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('inbox', v_inbox_streams);
    category := 'inbox';
    stream_count := array_length(v_inbox_streams, 1);
    RETURN NEXT;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_scheduled_retry_due IS
'Emits orphan-style wake NOTIFYs for streams whose scheduled_for retry timestamp has elapsed. Called periodically by ScheduledRetryWorker on a low-cadence (default 10 s) cycle so retry latency stays bounded without paying the 250 ms ClaimWorker polling tax. Returns one row per category that emitted NOTIFYs, with the count of distinct streams woken.';
