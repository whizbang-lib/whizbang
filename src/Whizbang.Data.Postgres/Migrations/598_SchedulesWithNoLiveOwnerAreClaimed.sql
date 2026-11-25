-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 598_SchedulesWithNoLiveOwnerAreClaimed.sql
-- Date: 2026-11-25
-- Description: A due schedule whose stream no live instance holds is claimed (#2575).
--
--              wh_claim_due_schedules (068) took a due schedule only through an inner join to wh_active_streams on
--              the caller's id. A stream gets its row the first time something is written to it, and a schedule's
--              first occurrence is that write, so a schedule created on a fresh stream (the framework's own
--              integrity schedules use a control stream nothing else writes to) was seen by no instance: it never
--              fired, and nothing reported it. A stream pinned to an instance that has since died was stuck the
--              same way, since the row still named the dead owner.
--
--              The rule, wh_schedule_owner_admits: a schedule is the caller's when its stream is pinned to the
--              caller, or when no live instance holds it (no row, an unassigned row, or an owner the liveness rule,
--              wh_live_instance_ids, calls dead). A stream pinned to another live instance stays that instance's.
--              Ownership only spreads the work: the claim's FOR UPDATE SKIP LOCKED and the advance in the same
--              transaction keep each fire exactly-once whichever instance takes it, and the first fire pins the
--              stream to the claimer (store_outbox_messages), which owns it from then on. The same rule answers
--              the next-fire-time query and the due poll in the provider, which read wh_live_instance_ids the
--              same way.
--
--              Cost: one read of the live instances per claim (an array built once, not per row) and a LEFT JOIN
--              in place of the inner join; the due scan is still the partial index idx_wh_schedules_due.
--
--              Number 598: 590 to 597 are taken by changes in flight.
--
-- Reversal: reversible (the down file restores 068's claim and drops the rule)
-- Dependencies: 068 (wh_claim_due_schedules), 316 (wh_live_instance_ids)
-- Objects: wh_schedule_owner_admits, wh_claim_due_schedules

-- ---------------------------------------------------------------------------------------------
-- wh_schedule_owner_admits: whether a schedule on a stream assigned to p_assigned_instance_id is the caller's.
-- The caller passes the live instances it read once (wh_live_instance_ids), so a scan applies the rule per row
-- without re-reading the alive-locks.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_schedule_owner_admits');

-- <docs>fundamentals/temporal/temporal-engine#which-instance-fires-a-schedule</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:OwnerAdmits_FollowsTheRuleForEveryOwnerShapeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_schedule_owner_admits(
  p_assigned_instance_id UUID,
  p_instance_id UUID,
  p_live_instance_ids UUID[]
) RETURNS BOOLEAN
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT p_assigned_instance_id IS NULL
    OR p_assigned_instance_id = p_instance_id
    OR NOT (p_assigned_instance_id = ANY(COALESCE(p_live_instance_ids, ARRAY[]::UUID[])))
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_schedule_owner_admits(UUID, UUID, UUID[]) IS
  '598 (#2575): whether a schedule on a stream assigned to p_assigned_instance_id is p_instance_id''s to fire: the '
  'stream is unassigned (or has no row, which a LEFT JOIN reads as NULL), is the caller''s, or its owner is not among '
  'the live instances the caller read (wh_live_instance_ids).';

-- ---------------------------------------------------------------------------------------------
-- wh_claim_due_schedules: copied from 068, the inner join to the caller's streams replaced by the rule.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_claim_due_schedules');

-- <docs>fundamentals/temporal/temporal-engine#which-instance-fires-a-schedule</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:DueSchedule_OnAStreamNothingHasWritten_FiresOnce_AndPinsItsStreamToTheClaimerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:DueSchedule_WhoseStreamOwnerIsNotLive_IsTakenOverByALiveInstanceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:DueSchedule_WhoseStreamIsPinnedToAnotherLiveInstance_IsLeftToThatInstanceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:OwnerlessSchedule_ClaimedConcurrently_FiresExactlyOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleClaimSqlTests.cs:DueOwned_SpawnsOccurrence_AdvancesNextFire_LogsRunAsync</tests>

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_claim_due_schedules(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_limit INTEGER DEFAULT 100,
  p_now TIMESTAMPTZ DEFAULT NULL
) RETURNS TABLE(
  o_schedule_id UUID,
  o_occurrence_id UUID,
  o_fired_at TIMESTAMPTZ,
  o_next_fire_at TIMESTAMPTZ,
  o_completed BOOLEAN
)
SET timezone = 'UTC'   -- function-local: due comparison + spawn run in UTC regardless of caller session
AS $$
DECLARE
  v_sched RECORD;
  v_occurrence_id UUID;
  v_next TIMESTAMPTZ;
  v_completed BOOLEAN;
  v_cursor TIMESTAMPTZ;
  v_horizon TIMESTAMPTZ;
  v_skipped BOOLEAN;
  -- DB clock is authority for the due decision (avoids multi-instance skew). p_now is a test seam.
  v_now TIMESTAMPTZ := COALESCE(p_now, NOW());
  -- 598 (#2575): the live instances, read once per claim (the alive-lock read is not per row).
  v_live UUID[] := ARRAY(SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l);
BEGIN
  FOR v_sched IN
    SELECT sc.*
    FROM __SCHEMA__.wh_schedules sc
    -- 598 (#2575): LEFT JOIN, and the one ownership rule. A stream no live instance holds (no row, an unassigned
    -- row, an owner the liveness rule calls dead) is anyone's; the row lock below keeps the fire exactly-once.
    LEFT JOIN __SCHEMA__.wh_active_streams s ON s.stream_id = sc.stream_id
    WHERE __SCHEMA__.wh_schedule_owner_admits(s.assigned_instance_id, p_instance_id, v_live)
      AND sc.status = 0
      AND sc.next_fire_at <= v_now
    ORDER BY sc.next_fire_at
    LIMIT p_limit
    FOR UPDATE OF sc SKIP LOCKED
  LOOP
    -- Deterministic occurrence id (schedule + occurrence#) so a redelivered fire dedups at the outbox.
    v_occurrence_id := md5(v_sched.schedule_id::text || ':' || v_sched.occurrence_count::text)::uuid;
    v_cursor := v_sched.next_fire_at;
    v_skipped := FALSE;

    -- CatchUp burst bound: never replay occurrences older than the lookback horizon. Jump the cursor
    -- forward to the horizon first so a long outage can't unleash an unbounded replay storm.
    IF v_sched.misfire_policy = 1 AND v_sched.catch_up_lookback_ms IS NOT NULL THEN
      v_horizon := v_now - make_interval(secs => v_sched.catch_up_lookback_ms / 1000.0);
      IF v_cursor < v_horizon THEN
        v_cursor := COALESCE(__SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone,
          v_cursor, v_horizon), v_cursor);
      END IF;
    END IF;

    -- One-step next fire from the cursor.
    v_next := __SCHEMA__.wh_schedule_next_fire(
      v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor);

    -- MISFIRE: the very next fire is ALSO already due => more than one fire was missed (paused a long
    -- time / owner was down). Without this, an interval schedule would advance one step per claim and
    -- grind through every missed occurrence.
    IF v_next IS NOT NULL AND v_next <= v_now THEN
      IF v_sched.misfire_policy = 0 THEN
        -- Coalesce (default): fire ONCE now, then fast-forward past the whole missed window.
        v_next := __SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor, v_now);
      ELSIF v_sched.misfire_policy = 2 THEN
        -- Skip: do NOT fire for the missed window at all; just fast-forward back onto cadence.
        v_skipped := TRUE;
        v_next := __SCHEMA__._wh_advance_past(
          v_sched.recurrence_kind, v_sched.cron, v_sched.interval_ms, v_sched.timezone, v_cursor, v_now);
      END IF;
      -- CatchUp (1): keep the one-step next => replay each missed occurrence, one per claim
      -- (bounded by catch_up_lookback_ms above).
    END IF;

    -- Bounds. A skipped fire doesn't consume a max_occurrences slot.
    IF v_next IS NOT NULL AND v_sched.until_at IS NOT NULL AND v_next > v_sched.until_at THEN
      v_next := NULL;
    END IF;
    IF NOT v_skipped AND v_next IS NOT NULL AND v_sched.max_occurrences IS NOT NULL
       AND (v_sched.occurrence_count + 1) >= v_sched.max_occurrences THEN
      v_next := NULL;
    END IF;
    v_completed := (v_next IS NULL);

    IF v_skipped THEN
      -- No occurrence spawned — record the misfire so the skip stays auditable.
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, instance_id)
      VALUES (v_sched.schedule_id, v_occurrence_id, v_now, 2, p_instance_id);   -- 2 = Skipped (misfire)
    ELSE
      -- Spawn the occurrence transactionally through the shared emit (run status 0 = fired).
      PERFORM __SCHEMA__._wh_spawn_occurrence(
        v_sched.schedule_id, v_occurrence_id, v_sched.occurrence_count,
        v_sched.event_type, v_sched.event_data, v_sched.scope, v_sched.stream_id,
        v_sched.delivery_guarantee, p_instance_id, p_lease_expiry, v_now, p_partition_count,
        0::SMALLINT, v_sched.authority_principal_id, v_sched.authority_claims);
    END IF;

    -- Advance the schedule. Completed schedules keep their (past) next_fire_at — status 2 excludes
    -- them from the due-detect/notify queries so they never re-fire, satisfying the NOT NULL column.
    UPDATE __SCHEMA__.wh_schedules
    SET last_fire_at = CASE WHEN v_skipped THEN last_fire_at ELSE v_now END,
        next_fire_at = COALESCE(v_next, next_fire_at),
        occurrence_count = occurrence_count + CASE WHEN v_skipped THEN 0 ELSE 1 END,
        status = CASE WHEN v_completed THEN 2 ELSE status END,
        version = version + 1
    WHERE schedule_id = v_sched.schedule_id;

    o_schedule_id := v_sched.schedule_id;
    o_occurrence_id := v_occurrence_id;
    o_fired_at := v_now;
    o_next_fire_at := v_next;
    o_completed := v_completed;
    RETURN NEXT;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_claim_due_schedules IS
  'Temporal engine authoritative fire: leases due Active schedules the caller owns or no live instance holds '
  '(598, wh_schedule_owner_admits; SKIP LOCKED), spawns each '
  'occurrence into the outbox, advances next_fire_at (wh_schedule_next_fire + until/max bounds; '
  'completion => status 2), and logs a wh_schedule_runs row — all atomically (exactly-once creation).';
