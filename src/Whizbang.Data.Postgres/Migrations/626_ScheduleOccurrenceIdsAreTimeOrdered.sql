-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 626_ScheduleOccurrenceIdsAreTimeOrdered.sql
-- Date: 2026-11-25
-- Description: Every schedule occurrence id is a UUIDv7 (#2599).
--
--              An occurrence's id is its outbox message id, and the outbox refuses a message id that is not a UUIDv7.
--              wh_claim_due_schedules minted md5(schedule_id:occurrence_count)::uuid, whose version nibble is 7 one
--              time in sixteen, so fifteen of every sixteen fires were refused; wh_trigger_schedule_now minted
--              gen_random_uuid(), a v4, so every manual trigger was refused.
--
--              wh_schedule_occurrence_id(schedule, occurrence, fire_at) is the one cadence id: the 48-bit unix-ms
--              timestamp of the occurrence's scheduled fire time, version 7, the RFC 4122 variant, and the remaining
--              74 bits from md5(schedule_id:occurrence). The same inputs always give the same id, so two claimers of one
--              occurrence agree on it and the outbox dedups the second. Every claim path (the cadence fire, a coalesced
--              or replayed misfire, and the logged skip) takes its id from the one line that sets v_occurrence_id.
--              wh_trigger_schedule_now takes wh_new_uuid_v7(now): a fresh v7 on the database clock with random bits.
--              PostgreSQL 18's uuidv7() is not used: the supported versions start below 18.
--
--              Ids already stored (wh_schedule_runs, wh_outbox, wh_event_store) are history and are not rewritten.
--
--              Cost: one md5 and a string build per claimed occurrence, no table read; the claim's query is unchanged.
--
-- Reversal: reversible (the down file restores wh_claim_due_schedules' last word, 601, and wh_trigger_schedule_now's,
--           070, and drops the three id functions)
-- Dependencies: 601 (wh_claim_due_schedules), 070 (wh_trigger_schedule_now)
-- Objects: _wh_uuid_v7, wh_schedule_occurrence_id, wh_new_uuid_v7, wh_claim_due_schedules, wh_trigger_schedule_now

-- ---------------------------------------------------------------------------------------------
-- _wh_uuid_v7: a UUIDv7 from a timestamp and at least 19 hex digits of other bits.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_uuid_v7');

-- <docs>fundamentals/temporal/temporal-engine#occurrence-ids</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:TheCadenceId_ForEveryOccurrenceNumber_IsAUuidV7OfItsFireTimeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ATriggerNow_MintsAFreshUuidV7_StampedWithTheDatabaseClockAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_uuid_v7(p_at TIMESTAMPTZ, p_hex TEXT) RETURNS UUID
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $$
  -- 12 hex digits of unix milliseconds, the version digit 7, 3 digits of rand_a, the variant digit (10xx: 8, 9, a or
  -- b, its low two bits from p_hex) and 15 digits of rand_b.
  SELECT (lpad(to_hex(floor(EXTRACT(EPOCH FROM p_at) * 1000)::BIGINT), 12, '0')
          || '7' || substr(p_hex, 1, 3)
          || substr('89ab', (('x' || substr(p_hex, 4, 1))::BIT(4)::INTEGER & 3) + 1, 1)
          || substr(p_hex, 5, 15))::UUID
$$;
COMMENT ON FUNCTION __SCHEMA__._wh_uuid_v7(TIMESTAMPTZ, TEXT) IS
  'A UUIDv7 whose 48-bit timestamp is p_at in unix milliseconds and whose other bits come from the first 19 hex digits '
  'of p_hex (#2599).';

-- ---------------------------------------------------------------------------------------------
-- wh_schedule_occurrence_id: the deterministic cadence occurrence id.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_schedule_occurrence_id');

-- <docs>fundamentals/temporal/temporal-engine#occurrence-ids</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:TheCadenceId_IsTheSameForTheSameOccurrence_AndDiffersAcrossOccurrencesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:TheCadenceId_ForEveryOccurrenceNumber_IsAUuidV7OfItsFireTimeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:TheClaim_MintsTheCadenceIdThroughTheOneOccurrenceIdFunctionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_schedule_occurrence_id(
  p_schedule_id UUID,
  p_occurrence_number BIGINT,
  p_fire_at TIMESTAMPTZ
) RETURNS UUID
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $$
  SELECT __SCHEMA__._wh_uuid_v7(p_fire_at, md5(p_schedule_id::TEXT || ':' || p_occurrence_number::TEXT))
$$;
COMMENT ON FUNCTION __SCHEMA__.wh_schedule_occurrence_id(UUID, BIGINT, TIMESTAMPTZ) IS
  'The id of a schedule''s occurrence, which is its outbox message id: a UUIDv7 stamped with the scheduled fire time, '
  'the rest hashed from the schedule id and the occurrence number, so every claimer computes the same id (#2599).';

-- ---------------------------------------------------------------------------------------------
-- wh_new_uuid_v7: a fresh UUIDv7 at a given instant.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_new_uuid_v7');

-- <docs>fundamentals/temporal/temporal-engine#occurrence-ids</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ATriggerNow_MintsAFreshUuidV7_StampedWithTheDatabaseClockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ATriggerNowAsync_IsPublishedThroughTheOutboxUnderItsOccurrenceIdAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_new_uuid_v7(p_at TIMESTAMPTZ) RETURNS UUID
LANGUAGE sql VOLATILE PARALLEL SAFE
AS $$
  SELECT __SCHEMA__._wh_uuid_v7(p_at, md5(gen_random_uuid()::TEXT))
$$;
COMMENT ON FUNCTION __SCHEMA__.wh_new_uuid_v7(TIMESTAMPTZ) IS
  'A fresh UUIDv7 stamped with p_at and random other bits, for a PostgreSQL without uuidv7() (#2599).';

-- ---------------------------------------------------------------------------------------------
-- wh_claim_due_schedules: copied from its last word (601), with the 626 delta: the line that sets v_occurrence_id.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_claim_due_schedules');

-- <docs>fundamentals/temporal/temporal-engine#which-instance-fires-a-schedule</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleClaimSqlTests.cs:DueOwned_SpawnsOccurrence_AdvancesNextFire_LogsRunAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:DueSchedule_OnAStreamNothingHasWritten_FiresOnce_AndPinsItsStreamToTheClaimerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:DueSchedule_WhoseStreamOwnerIsNotLive_IsTakenOverByALiveInstanceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:DueSchedule_WhoseStreamIsPinnedToAnotherLiveInstance_IsLeftToThatInstanceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleOwnerlessClaimSqlTests.cs:OwnerlessSchedule_ClaimedConcurrently_FiresExactlyOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.Schedules.cs:InMapMode_AScheduleWithNoLedgerRow_FiresOnItsPartitionsOwnerOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TemporalScheduleClaimSqlTests.cs:NotOwned_NotClaimedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.cs:Schedules_FireOnThePartitionsOwner_NotTheStoredOwnerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ClaimPathsDecideThroughTheOwnershipTableGuardTests.cs:EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync</tests>
-- <docs>fundamentals/temporal/temporal-engine#occurrence-ids</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:AScheduledFire_MintsAUuidV7_StampedWithItsScheduledFireTimeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:TheClaim_MintsTheCadenceIdThroughTheOneOccurrenceIdFunctionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ACoalescedMisfire_MintsAUuidV7_StampedWithTheMissedFireTimeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ACatchUpReplay_MintsAUuidV7PerMissedOccurrence_EachStampedWithItsOwnFireTimeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ACatchUpReplayPastItsLookback_MintsAUuidV7Async</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ASkippedMisfire_LogsItsRunUnderAUuidV7Async</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ARunLoggedUnderTheOldId_KeepsItsId_WhenTheNextOccurrenceFiresAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:AScheduledFire_IsPublishedThroughTheOutboxUnderItsOccurrenceIdAsync</tests>
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
  -- 601 (#2575): the legacy mode's live instances, read once per claim (the alive-lock read is not per row). Map mode
  -- does not read them.
  v_live UUID[] := CASE WHEN v_map THEN NULL
                        ELSE ARRAY(SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l) END;
BEGIN
  FOR v_sched IN
    SELECT sc.*
    FROM __SCHEMA__.wh_schedules sc
    -- 566 (#2555): in map mode a schedule fires on the owner of its stream's partition (wh_i_own), the schedule's own
    -- number (compute_partition for a row without one). The stored owner is the legacy mode's test, and a stream with no
    -- ledger row fires nowhere in it, as before.
    -- 601 (#2575): in the legacy mode a stream no live instance holds (no ledger row, an unassigned row, or an owner the
    -- liveness rule calls dead) is any live claimer's (wh_schedule_owner_admits); the row lock below keeps the fire
    -- exactly-once, and the fire pins the stream. A stream another live instance holds stays that instance's.
    LEFT JOIN __SCHEMA__.wh_active_streams s ON s.stream_id = sc.stream_id
    WHERE CASE WHEN v_map
               THEN __SCHEMA__.wh_i_own(COALESCE(sc.partition_number, __SCHEMA__.compute_partition(sc.stream_id, p_partition_count)), p_instance_id) IS NOT NULL
               ELSE __SCHEMA__.wh_schedule_owner_admits(s.assigned_instance_id, p_instance_id, v_live) END
      AND sc.status = 0
      AND sc.next_fire_at <= v_now
    ORDER BY sc.next_fire_at
    LIMIT p_limit
    FOR UPDATE OF sc SKIP LOCKED
  LOOP
    -- Deterministic occurrence id (schedule + occurrence#) so a redelivered fire dedups at the outbox.
    v_occurrence_id := __SCHEMA__.wh_schedule_occurrence_id(v_sched.schedule_id, v_sched.occurrence_count, v_sched.next_fire_at);
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

COMMENT ON FUNCTION __SCHEMA__.wh_claim_due_schedules(UUID, TIMESTAMPTZ, INTEGER, INTEGER, TIMESTAMPTZ) IS
  'Temporal engine authoritative fire: leases due Active schedules this instance may fire (SKIP LOCKED), spawns each '
  'occurrence into the outbox, advances next_fire_at (wh_schedule_next_fire + until/max bounds; completion => status 2), '
  'and logs a wh_schedule_runs row, all atomically (exactly-once creation). 566: in map mode the owner of the schedule''s '
  'partition (wh_i_own). 601 (#2575): otherwise the stream''s stored owner, or any instance when no live instance holds '
  'the stream (wh_schedule_owner_admits). 626 (#2599): the occurrence id is wh_schedule_occurrence_id, a deterministic '
  'UUIDv7 of the scheduled fire time.';

-- ---------------------------------------------------------------------------------------------
-- wh_trigger_schedule_now: copied from its last word (070), with the 626 delta: the line that sets v_occurrence_id.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_trigger_schedule_now');

-- <docs>fundamentals/temporal/temporal-engine#occurrence-ids</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ATriggerNow_MintsAFreshUuidV7_StampedWithTheDatabaseClockAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ScheduleOccurrenceIdSqlTests.cs:ATriggerNowAsync_IsPublishedThroughTheOutboxUnderItsOccurrenceIdAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_trigger_schedule_now(
  p_schedule_id UUID,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_partition_count INTEGER
) RETURNS TABLE(o_triggered BOOLEAN, o_occurrence_id UUID)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_sched RECORD;
  v_occurrence_id UUID;
  v_now TIMESTAMPTZ := NOW();
BEGIN
  -- Only a non-terminal schedule can be triggered (Active or Paused: a paused schedule may still be
  -- run manually by an operator). Completed/Canceled are terminal.
  SELECT * INTO v_sched
  FROM __SCHEMA__.wh_schedules
  WHERE schedule_id = p_schedule_id
    AND status IN (0, 1)
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN QUERY SELECT FALSE, NULL::UUID;
    RETURN;
  END IF;

  -- Fresh random id: the cadence ids are hashed from (schedule, occurrence#), so a random id can never collide
  -- with the next scheduled occurrence (and each manual trigger is a distinct occurrence). 626 (#2599): a UUIDv7
  -- stamped with the database clock, because the occurrence id is the outbox message id.
  v_occurrence_id := __SCHEMA__.wh_new_uuid_v7(v_now);

  PERFORM __SCHEMA__._wh_spawn_occurrence(
    v_sched.schedule_id, v_occurrence_id, v_sched.occurrence_count,
    v_sched.event_type, v_sched.event_data, v_sched.scope, v_sched.stream_id,
    v_sched.delivery_guarantee, p_instance_id, p_lease_expiry, v_now, p_partition_count,
    3::SMALLINT,   -- 3 = TriggeredEarly
    v_sched.authority_principal_id, v_sched.authority_claims);

  -- Deliberately NOT advancing next_fire_at / last_fire_at / occurrence_count: the regular cadence
  -- continues exactly as it would have, and the manual fire does not consume a max_occurrences slot.
  RETURN QUERY SELECT TRUE, v_occurrence_id;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_trigger_schedule_now IS
  'Fires an extra occurrence for a non-terminal schedule immediately, without disturbing its cadence '
  '(next_fire_at / last_fire_at / occurrence_count untouched). Logged as run status 3 (TriggeredEarly). '
  '626 (#2599): the occurrence id is a fresh UUIDv7 stamped with the database clock (wh_new_uuid_v7).';

