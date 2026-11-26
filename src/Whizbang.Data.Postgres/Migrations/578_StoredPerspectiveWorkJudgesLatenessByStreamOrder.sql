-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 578_StoredPerspectiveWorkJudgesLatenessByStreamOrder.sql
-- Date: 2026-11-24
-- Description: store_perspective_events judges whether an event is behind its cursor by stream order, never by id
--              (#2551), as the event-store chains do since 573 and 574.
--
--              An event id is time-ordered and never a stream position. store_perspective_events flagged a cursor for
--              rewind when the stored event's id sorted before the cursor's last applied event, and chose the earliest
--              trigger by id. An event emitted earlier whose id sorts later was then applied out of order with no
--              replay, and one emitted later whose id sorts earlier forced a needless replay. It now compares the
--              events' stream versions, through two primary-key probes of wh_event_store per stored event that is new.
--
-- Reversal: reversible (the down file restores 567's definition)
-- Dependencies: 567 (store_perspective_events)
-- Objects: store_perspective_events

-- ---------------------------------------------------------------------------------------------
-- store_perspective_events: copied from its last word (567), with the 578 delta: lateness by stream version.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('store_perspective_events');

-- store_perspective_events: last word 022, redefined as the perspective work store with the event-store chain's
-- rules (partition, Stored status, priority, the lease routing, the rewind of a late event and the doorbell), and with
-- dead-letter recovery as one more arrival (#1722). Signature unchanged.
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveEventsPartitionSqlTests.cs:StorePerspectiveEvents_PopulatesPartitionNumberAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveWorkRow_IsStoredInTheShapeTheEventStoreChainStoresItInAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveEvent_BehindItsCursor_FlagsTheCursorForRewindAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#reads</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveEvent_BehindItsCursorInStreamOrder_ThoughItsIdSortsAfter_FlagsTheRewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredWorkReentrySqlTests.cs:ARecoveredPerspectiveEvent_AheadOfItsCursorInStreamOrder_ThoughItsIdSortsBefore_IsNotARewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredPerspectiveDeadLetterApplyTests.cs:ARecoveredPerspectiveWorkRow_IsApplied_AndReadsRecoveredOnceAppliedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:StorePerspectiveEvents_InMapMode_LeasesToTheCallerOnlyOnAPartitionItOwnsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.store_perspective_events(
  p_events JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200),
  was_newly_created BOOLEAN
) AS $$
DECLARE
  v_event RECORD;
  v_was_new BOOLEAN;
  v_work_id UUID;
  v_lease_instance UUID;
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
  v_claimed INTEGER;
  -- The doorbell, as store_outbox_messages rings it: streams probed once per call before this call inserts for them,
  -- the probed-empty ones, and those this call gave new work.
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_streams UUID[] := ARRAY[]::UUID[];
  v_notify_streams UUID[] := ARRAY[]::UUID[];
BEGIN
  IF jsonb_array_length(p_events) = 0 THEN RETURN; END IF;

  FOR v_event IN
    SELECT
      (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS ev_stream_id,
      (elem->>'PerspectiveName')::VARCHAR(200) AS ev_perspective_name,
      (elem->>'EventId')::UUID AS ev_event_id,
      -- 241: the work id to store the row under; a new one when absent. Recovery keeps the dead letter's, so a row
      -- dead-lettered again is recognized as the same work.
      (elem->>'EventWorkId')::UUID AS ev_work_id,
      -- The chain schedules work with its source row's priority; zero (undeclared) and an absent field read as standard.
      COALESCE(NULLIF((elem->>'Priority')::INTEGER, 0), 150) AS ev_priority,
      -- 241 (#1722): set only by dead-letter recovery, naming the dead-letter row the work re-enters from.
      (elem->>'RecoveredFromDeadLetterId')::UUID AS ev_recovered_from
    FROM jsonb_array_elements(p_events) AS elem
    ORDER BY (elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID, (elem->>'EventId')::UUID
  LOOP
    -- 241 (#1722): a recovery claims its dead letter first (Recovering), in this transaction, so two recoveries of one
    -- row cannot both re-enter it, and a dead letter whose work is still queued is left alone.
    IF v_event.ev_recovered_from IS NOT NULL THEN
      UPDATE __SCHEMA__.wh_dead_letters dl
      SET recovery_status = 1,                  -- Recovering, until the event is applied
          recovery_attempts = dl.recovery_attempts + 1,
          last_recovery_at = p_now
      WHERE dl.dead_letter_id = v_event.ev_recovered_from
        AND dl.source_id = v_event.ev_work_id
        AND dl.recovery_status NOT IN (1, 2, 3, 4)  -- not already Recovering, HoldForReview, Recovered, PermanentlyFailed
        AND dl.recovered_at IS NULL
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_perspective_events queued
          WHERE queued.stream_id = v_event.ev_stream_id
            AND queued.perspective_name = v_event.ev_perspective_name
            AND queued.event_id = v_event.ev_event_id);
      GET DIAGNOSTICS v_claimed = ROW_COUNT;
      IF v_claimed = 0 THEN
        CONTINUE;  -- claimed by another recovery, already settled, or the work is still queued
      END IF;
    END IF;

    -- Emptiness probe, once per stream per call, before this call's first insert for it (the loop is stream-ordered).
    -- 140: a locked pending row is being completed, so it reads as empty and rings. 161: ordered by the index key so
    -- the probe never scans a stream whose work is all done.
    IF NOT (v_event.ev_stream_id = ANY(v_probed_streams)) THEN
      v_probed_streams := array_append(v_probed_streams, v_event.ev_stream_id);
      PERFORM 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = v_event.ev_stream_id
          AND pe.processed_at IS NULL
        ORDER BY pe.stream_id, pe.event_id
        LIMIT 1
        FOR SHARE OF pe SKIP LOCKED;
      IF NOT FOUND THEN
        v_empty_streams := array_append(v_empty_streams, v_event.ev_stream_id);
      END IF;
    END IF;

    -- The chain's lease routing (Slice 26.14): a leasing caller leases to the stream's live pinned owner, falling back
    -- to itself; a caller passing no lease expiry leaves the row for the claim.
    v_lease_instance := NULL;
    -- 567 (#2555): in map mode the row is leased to the caller only when it owns the stream's partition; otherwise it is
    -- left unleased for the partition's owner to claim. Never to a stored owner.
    IF p_lease_expiry IS NOT NULL AND v_map THEN
      IF __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(v_event.ev_stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN
        v_lease_instance := p_instance_id;
      END IF;
    ELSIF p_lease_expiry IS NOT NULL THEN
      SELECT ast.assigned_instance_id INTO v_lease_instance
      FROM __SCHEMA__.wh_active_streams ast
      WHERE ast.stream_id = v_event.ev_stream_id
        AND ast.assigned_instance_id IS NOT NULL
        AND EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_service_instances si
          WHERE si.instance_id = ast.assigned_instance_id);
      v_lease_instance := COALESCE(v_lease_instance, p_instance_id);
    END IF;

    v_work_id := COALESCE(v_event.ev_work_id, gen_random_uuid());
    INSERT INTO __SCHEMA__.wh_perspective_events (
      event_work_id, stream_id, perspective_name, event_id,
      partition_number, status, attempts, created_at, instance_id, lease_expiry, priority,
      recovered_from_dead_letter_id
    ) VALUES (
      v_work_id,
      v_event.ev_stream_id,
      v_event.ev_perspective_name,
      v_event.ev_event_id,
      __SCHEMA__.compute_partition(v_event.ev_stream_id, p_partition_count),
      1,                  -- Stored flag
      0,                  -- Initial attempts
      p_now,
      v_lease_instance,
      CASE WHEN v_map AND v_lease_instance IS NULL THEN NULL ELSE p_lease_expiry END,
      v_event.ev_priority,
      v_event.ev_recovered_from  -- 241: NULL for every arrival but a recovery
    )
    -- Constraint-less, so a work id that is already taken is a conflict as well as the (stream, perspective, event) key.
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    IF NOT v_was_new THEN
      -- 241: a recovery that lost the race to a concurrent store of the same work hands its dead letter back, so the
      -- dead letter is never left Recovering behind a row that does not name it.
      IF v_event.ev_recovered_from IS NOT NULL THEN
        UPDATE __SCHEMA__.wh_dead_letters dl
        SET recovery_status = 0
        WHERE dl.dead_letter_id = v_event.ev_recovered_from AND dl.recovery_status = 1;
        CONTINUE;
      END IF;
      -- The work is already queued: answer with its id.
      SELECT pe.event_work_id INTO v_work_id
      FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = v_event.ev_stream_id
        AND pe.perspective_name = v_event.ev_perspective_name
        AND pe.event_id = v_event.ev_event_id;
    ELSE
      -- A late event (behind its cursor's last applied event) is applied by replaying the stream in order: the cursor
      -- is flagged RewindRequired with the earliest such event as its trigger, exactly as the chain flags it (100).
      -- 578 (#2551): "behind" and "earliest" are stream order, never the id: the event's stream version against the
      -- version of the cursor's last applied event, and against the version of an existing trigger. An event the store
      -- holds no version for has no place a replay could put it, and is not flagged. Two primary-key probes.
      UPDATE __SCHEMA__.wh_perspective_cursors pc
      SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
          rewind_trigger_event_id = CASE
            WHEN pc.rewind_trigger_event_id IS NULL THEN v_event.ev_event_id
            WHEN late.late_version < COALESCE(
                   (SELECT t.version FROM __SCHEMA__.wh_event_store t WHERE t.event_id = pc.rewind_trigger_event_id),
                   9223372036854775807)
              THEN v_event.ev_event_id
            ELSE pc.rewind_trigger_event_id
          END,
          rewind_flagged_at = p_now,
          rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
      FROM (SELECT se.version AS late_version FROM __SCHEMA__.wh_event_store se WHERE se.event_id = v_event.ev_event_id) AS late
      WHERE pc.stream_id = v_event.ev_stream_id
        AND pc.perspective_name = v_event.ev_perspective_name
        AND pc.last_event_id IS NOT NULL
        AND late.late_version < (SELECT ce.version FROM __SCHEMA__.wh_event_store ce WHERE ce.event_id = pc.last_event_id);

      IF v_event.ev_stream_id = ANY(v_empty_streams)
         AND NOT (v_event.ev_stream_id = ANY(v_notify_streams)) THEN
        v_notify_streams := array_append(v_notify_streams, v_event.ev_stream_id);
      END IF;
    END IF;

    RETURN QUERY SELECT v_work_id, v_event.ev_stream_id, v_event.ev_perspective_name, v_was_new;
  END LOOP;

  IF cardinality(v_notify_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_notify_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_perspective_events(JSONB, UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Stores perspective work rows (022) with the event-store chain''s rules (241): partition from the stream, Stored, the '
  'given priority, leased to the stream''s live owner (or the caller) when a lease expiry is given, the cursor flagged '
  'RewindRequired for an event behind it, and the perspective doorbell rung for a stream that had no pending work. '
  'Unique on (stream, perspective, event). 241 (#1722): work recovered from a dead letter (RecoveredFromDeadLetterId) '
  're-enters here: it claims its dead letter and its row names it for process_perspective_event_completions to settle. 567 (#2555): with the service''s switch on, no stored owner is pinned or leased to; leased by partition (wh_i_own). 578 (#2551): a late event is behind its cursor in stream order (its version), never by id.';
