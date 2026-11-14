-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 412_AFailureReleasesItsRunInStreamOrder.sql: process_outbox_failures as 172 defined it. The function
-- keeps no state, so nothing else is undone.

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('process_outbox_failures');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:ProcessOutboxFailures_TakesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ProcessOutboxFailures_MidRun_ReleasesTheRestOfTheRunToBeRetriedInOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxInboxFailureReasonSqlTests.cs:OutboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ProcessOutboxFailures_DoesNotDoubleCountAttemptsAsync</tests>
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
        UPDATE __SCHEMA__.wh_outbox o
        SET instance_id = NULL,
            lease_expiry = NULL,
            attempts = GREATEST(o.attempts - 1, 0)
        WHERE o.stream_id = v_stream
          AND (o.created_at, o.message_id) > (v_created, v_failure.msg_id)
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
  'Processes outbox message failures: sets the Failed flag, records the error and reason, and '
  'schedules the retry with exponential backoff (capped at 5 minutes), releasing the lease for '
  'reclaim. A failing schedule occurrence also lands a wh_schedule_runs Failed row, and an '
  'at-most-once schedule (delivery_guarantee=1) is parked terminally instead of retried. Reads the '
  'element the runtime writes (MessageId, Reason) as well as the older name (FailureReason). 171: a '
  'retried row releases the rest of its run (later rows of its stream its holder still leases, attempt '
  'refunded), so the stream is retried in order. 172: the failures are applied in (stream_id, created_at, message_id) '
  'order, so the rows are locked in the order every waiting writer of wh_outbox uses.';
