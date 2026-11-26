-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 601_SchedulesWithNoLiveOwnerAreClaimed.sql (#2575): wh_claim_due_schedules goes back to 579's
-- definition, whose legacy branch claims only schedules on streams pinned to the caller, and the rule goes.

SELECT __SCHEMA__.drop_all_overloads('wh_claim_due_schedules');

-- <docs>fundamentals/temporal/temporal-engine</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleClaimSqlTests.cs:DueOwned_SpawnsOccurrence_AdvancesNextFire_LogsRunAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleClaimSqlTests.cs:NotOwned_NotClaimedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Schedules_FireOnThePartitionsOwner_NotTheStoredOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
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
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
BEGIN
  FOR v_sched IN
    SELECT sc.*
    FROM __SCHEMA__.wh_schedules sc
    -- 566 (#2555): in map mode a schedule fires on the owner of its stream's partition (wh_i_own), the schedule's own
    -- number (compute_partition for a row without one). The stored owner is the legacy mode's test, and a stream with no
    -- ledger row fires nowhere in it, as before.
    LEFT JOIN __SCHEMA__.wh_active_streams s ON s.stream_id = sc.stream_id
    WHERE CASE WHEN v_map
               THEN __SCHEMA__.wh_i_own(COALESCE(sc.partition_number, __SCHEMA__.compute_partition(sc.stream_id, p_partition_count)), p_instance_id) IS NOT NULL
               ELSE s.assigned_instance_id = p_instance_id END
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
      -- No occurrence spawned: record the misfire so the skip stays auditable.
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

    -- Advance the schedule. Completed schedules keep their (past) next_fire_at; status 2 excludes
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

DROP FUNCTION IF EXISTS __SCHEMA__.wh_schedule_owner_admits(UUID, UUID, UUID[]);
