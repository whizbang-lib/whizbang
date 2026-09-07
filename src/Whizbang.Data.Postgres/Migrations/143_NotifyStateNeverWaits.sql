-- Migration: 143_NotifyStateNeverWaits
-- Date: 2026-09-07
-- Description: the doorbell debounce never waits on another writer's transaction (issue #713).
--
--   _notify_debounced (137, kind split in 141) decides suppress-or-fire for one target instance and
--   records the decision on that instance's wh_notify_state row: the watermark slide and the
--   suppressed counter on the suppress branch, the attempt stamp and the fired counter on the fire
--   branch. Both writes run inside the CALLER's transaction, so the row lock is held until the
--   caller commits. The callers are the hot paths: the inbox and outbox stores and the handler
--   commits all ring the owners of the streams they touched. A handler-commit batch toward a busy
--   instance holds its transaction for seconds under a bulk ingest, and every store toward the
--   same instance that arrived during it queued on that one row. The stores are the receive path,
--   so the transport's receivers stalled, the inbox lease pool filled with rows nobody completed,
--   and the consumer's progress froze and jumped. The lock graph showed the chain in the open:
--   a dozen store calls waiting on transactionid behind one handler commit, itself behind another,
--   and the only ungranted tuple lock in the database on wh_notify_state. The probe and claim
--   fixes (140) had removed the FOR SHARE deadlocks that were hiding this one.
--
--   Rule: a doorbell never waits on the state row. The row is taken with FOR UPDATE SKIP LOCKED.
--   Owned: today's suppress-or-fire logic and the counter writes run against it (we hold it, so
--   nothing waits on us but the next writer, who skips). Not owned because another transaction
--   holds it: that writer is ringing or sliding this target right now, so we ring without touching
--   the state and return. Not owned because the row does not exist: create it, one creator at a
--   time under a transaction-scoped advisory try-lock; a creator that loses the try-lock, or whose
--   insert finds the row already committed by someone else, rings and returns. A spurious doorbell
--   is absorbed by the drain's refetch-until-empty loop; a lost wakeup is not, and waiting is the
--   storm. Suppression is therefore decided only on the owned path, so the 137 contract holds
--   whenever the row is free and degrades to "ring" under contention instead of "wait".
--
--   Residual: an INSERT ... ON CONFLICT DO NOTHING waits when a concurrent transaction holds an
--   UNCOMMITTED insert of the same key. Debounce creators are serialized by the advisory lock so
--   they never meet that way; the only other creator is claim_work's watermark stamp (140), so the
--   wait is bounded by one claim tick and happens at most once per (instance, kind) row lifetime.
--   The opportunistic hygiene delete on the fire path takes its rows FOR UPDATE SKIP LOCKED for the
--   same reason: it must never wait on a live row.
--
--   Signature unchanged from 141 (same parameter names; CREATE OR REPLACE cannot rename them), so
--   there is still exactly one overload for the duplicate-overload sweep.
-- Dependencies: 137 (wh_notify_state rate columns), 140 (claim watermark stamp), 141 (_notify_debounced current text)
-- Objects: _notify_debounced

CREATE OR REPLACE FUNCTION __SCHEMA__._notify_debounced(
  p_instance_id UUID,
  p_kind TEXT,
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

  -- This target's current state, taken WITHOUT waiting (143). The row lock we take here lives
  -- until the caller's transaction ends, which is exactly why the next writer must skip it.
  SELECT last_attempt_at, last_work_at, rapid_run
    INTO v_last_attempt, v_last_work, v_rapid_run
  FROM __SCHEMA__.wh_notify_state
  WHERE instance_id = p_instance_id AND payload_kind = p_kind
  FOR UPDATE SKIP LOCKED;

  IF NOT FOUND THEN
    -- Held by another writer (it is ringing or sliding this target right now), or the row does
    -- not exist yet. Held: ring and leave the state to the holder. Absent: one creator at a time;
    -- a loser rings and returns rather than queue behind the winner's transaction.
    IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_notify_state
               WHERE instance_id = p_instance_id AND payload_kind = p_kind)
       OR NOT pg_try_advisory_xact_lock(hashtext(p_instance_id::text), hashtext(p_kind)) THEN
      PERFORM pg_notify('wh_work_i_' || p_instance_id::text, p_payload);
      RETURN;
    END IF;

    -- Born without a found-work watermark: only claim_work arms last_work_at (131/137).
    INSERT INTO __SCHEMA__.wh_notify_state
      (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run, effective_window_ms,
       fired_count, suppressed_count)
    VALUES (p_instance_id, p_kind, NULL, NULL, 0, 0, 0, 0)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    IF NOT FOUND THEN
      -- Committed by someone else between the probe and the insert: theirs, not ours.
      PERFORM pg_notify('wh_work_i_' || p_instance_id::text, p_payload);
      RETURN;
    END IF;
    -- The row is ours (uncommitted insert): state is empty, the same as a first doorbell after idle.
  END IF;

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
    -- record the attempt + rate state + the suppressed count for OTel. The row is ours.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_work_at = v_now,
        last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        suppressed_count = suppressed_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_kind;
  ELSE
    PERFORM pg_notify('wh_work_i_' || p_instance_id::text, p_payload);
    -- Record the attempt + rate state + fired count WITHOUT arming suppression: last_work_at is
    -- left untouched (only claim_work arms it). The row is ours.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        fired_count = fired_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_kind;
    -- Opportunistic hygiene on the rare fire path: rows for long-departed instances, aged by
    -- most-recent activity (either watermark) so fire-born NULL-last_work_at rows also expire.
    -- Never waits: a row someone holds is, by definition, not long-departed.
    DELETE FROM __SCHEMA__.wh_notify_state ns
    WHERE ns.ctid IN (
      SELECT stale.ctid FROM __SCHEMA__.wh_notify_state stale
      WHERE GREATEST(COALESCE(stale.last_work_at, 'epoch'::timestamptz),
                     COALESCE(stale.last_attempt_at, 'epoch'::timestamptz)) < v_now - INTERVAL '7 days'
      FOR UPDATE SKIP LOCKED);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._notify_debounced(UUID, TEXT, TEXT, INTEGER) IS
  'Suppress-or-fire for one target instance (130/131/137, kind split from payload in 141, never '
  'waits since 143): the wh_notify_state row is keyed by p_kind and taken FOR UPDATE SKIP LOCKED; '
  'a held or contended row means another writer is ringing or sliding this target, so the doorbell '
  'fires without touching the state. p_payload is what pg_notify carries when the doorbell fires. '
  'Suppression requires an owned row, a live target, and a found-work watermark for this kind that '
  'is fresh within the effective (adaptive) window.';
