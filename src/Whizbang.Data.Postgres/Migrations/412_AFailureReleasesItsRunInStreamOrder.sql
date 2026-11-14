-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 412_AFailureReleasesItsRunInStreamOrder.sql
-- Date: 2026-11-13
-- Description: A failed outbox row releases the rest of its run in stream order, whatever the plan.
--
--              process_outbox_failures (copied from its last word, 172) applies the failures in (stream_id,
--              created_at, message_id) order, and a retried row releases the later rows of its stream its holder
--              still leases (171). That release was one multi-row UPDATE, whose rows are locked in the order its plan
--              reads them: the stream's order under an index scan, the heap's under a bitmap or sequential scan, which
--              a busy table can get. Holding a later row of the run while waiting on an earlier one, it closed a cycle
--              with a completion of the same rows, which locks in stream order (172): a deadlock seen about once in
--              600 rounds of the concurrent outbox test. The run's rows are now locked in an ordered subquery
--              (FOR UPDATE, ORDER BY created_at, message_id) before they are released, as every other writer that
--              waits on wh_outbox rows takes them. Nothing else changes.
--
--              Number 412: 409 is the highest on develop, and 410 and 411 are this change's own.
--
-- Reversal: reversible (172's process_outbox_failures is restored; the function keeps no state)
-- Dependencies: 172 (process_outbox_failures' last word, copied with the delta above), 171 (the run release)
-- Objects: process_outbox_failures
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('process_outbox_failures');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ProcessOutboxFailures_TakesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ProcessOutboxFailures_MidRun_ReleasesTheRestOfTheRunToBeRetriedInOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxInboxFailureReasonSqlTests.cs:OutboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ProcessOutboxFailures_DoesNotDoubleCountAttemptsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ProcessOutboxFailures_TakesTheRunItReleasesInStreamOrderAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_outbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
  v_schedule_id TEXT;
  v_delivery INTEGER;
  -- 171: where the failed row sat, so the rest of its run can be released behind it.
  v_holder UUID;
  v_stream UUID;
  v_created TIMESTAMPTZ;
BEGIN
  IF jsonb_array_length(p_failures) = 0 THEN RETURN; END IF;

  -- 172: the failures are visited in (stream_id, created_at, message_id) order, not the order the
  -- flush delivered them in. Each one locks its row and then the rest of its run, which lie after it
  -- in that same order, so a flush takes its rows in one ascending pass, the order every other
  -- waiting writer of wh_outbox uses (complete_outbox_published, renew_leases). The read that sorts
  -- them takes no lock. A failure whose row is gone sorts last and updates nothing, as before.
  FOR v_failure IN
    SELECT f.msg_id, f.status_flags, f.error_message, f.failure_reason
    FROM (
      SELECT
        (e.elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
        (e.elem->>'CompletedStatus')::INTEGER as status_flags,
        e.elem->>'Error' as error_message,
        COALESCE(e.elem->>'Reason', e.elem->>'FailureReason')::INTEGER as failure_reason,
        e.ord
      FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS e(elem, ord)
    ) f
    LEFT JOIN __SCHEMA__.wh_outbox fo ON fo.message_id = f.msg_id
    ORDER BY fo.stream_id NULLS LAST, fo.created_at, f.msg_id, f.ord
  LOOP
    SELECT o.metadata->>'scheduleId', COALESCE((o.metadata->>'deliveryGuarantee')::INTEGER, 0),
           o.instance_id, o.stream_id, o.created_at
    INTO v_schedule_id, v_delivery, v_holder, v_stream, v_created
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = v_failure.msg_id;

    IF v_schedule_id IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, error_message)
      VALUES (v_schedule_id::UUID, v_failure.msg_id, p_now, 1, v_failure.error_message);
    END IF;

    IF v_schedule_id IS NOT NULL AND v_delivery = 1 THEN
      -- AT-MOST-ONCE (delivery_guarantee = 1): never redeliver. Parked terminally; the failure is
      -- durably recorded in wh_schedule_runs above, which is what makes this safe.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),
          scheduled_for = 'infinity'::TIMESTAMPTZ,             -- terminal: no retry, ever
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;
    ELSE
      -- Default (at-least-once, and every non-schedule message): retry with backoff. Attempts are
      -- counted by claim_orphaned_outbox alone, so the backoff reads o.attempts as it stands.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
          -- Exponential backoff: 30s * 2^attempts, capped at 5 minutes
          scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(o.attempts, 10)), 10)),
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;

      -- 171: release the rest of the run. The rows of the same stream after the failed one that its
      -- holder still leases were claimed with it and must not publish ahead of its retry. Before
      -- this they stayed leased, the next drain of the stream fetched them, and they went out
      -- before the row that failed. Released, they are unowned and the acquisition's ordering rule
      -- (an earlier row deferred into the future blocks the later ones) holds them behind the
      -- retry. The attempt their claim charged is refunded: none of them was attempted. One range
      -- scan on idx_outbox_stream_run from the failed row's position; the holder test is an index
      -- filter on an INCLUDEd column.
      IF v_holder IS NOT NULL AND v_stream IS NOT NULL AND v_stream <> __EMPTY_UUID__::uuid THEN
        -- 412: the run's rows are locked in (created_at, message_id) order before they are released. The bare
        -- multi-row UPDATE locked them in whatever order its plan read them, which on a plan that reads the heap is not
        -- the stream's, so it could hold a later row of the run while waiting on an earlier one that a completion of
        -- the same rows, locking in stream order, already held: a deadlock.
        UPDATE __SCHEMA__.wh_outbox o
        SET instance_id = NULL,
            lease_expiry = NULL,
            attempts = GREATEST(o.attempts - 1, 0)
        FROM (
          SELECT r.message_id
          FROM __SCHEMA__.wh_outbox r
          WHERE r.stream_id = v_stream
            AND (r.created_at, r.message_id) > (v_created, v_failure.msg_id)
            AND r.instance_id = v_holder
            AND r.processed_at IS NULL
            AND r.published_at IS NULL
            AND r.coalesce_group IS NULL
          ORDER BY r.created_at, r.message_id
          FOR UPDATE OF r
        ) run
        WHERE o.message_id = run.message_id
          AND o.instance_id = v_holder
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
          AND o.coalesce_group IS NULL;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.process_outbox_failures(JSONB, TIMESTAMPTZ) IS
  '412: the run a retried row releases is locked in (created_at, message_id) order, whatever the plan reads. '
  'Processes outbox message failures: sets the Failed flag, records the error and reason, and '
  'schedules the retry with exponential backoff (capped at 5 minutes), releasing the lease for '
  'reclaim. A failing schedule occurrence also lands a wh_schedule_runs Failed row, and an '
  'at-most-once schedule (delivery_guarantee=1) is parked terminally instead of retried. Reads the '
  'element the runtime writes (MessageId, Reason) as well as the older name (FailureReason). 171: a '
  'retried row releases the rest of its run (later rows of its stream its holder still leases, attempt '
  'refunded), so the stream is retried in order. 172: the failures are applied in (stream_id, created_at, message_id) '
  'order, so the rows are locked in the order every waiting writer of wh_outbox uses.';
