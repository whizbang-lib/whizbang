-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 395_ARepairSendThatFailsIsWithdrawn.sql
-- Date: 2026-11-11
-- Description: The paced repair drain records a request as awaiting its answer before it sends it, and takes the record
--              back when the send fails (#2223).
--
--              The drain sent each repair request and only then recorded its buckets as awaiting the answer
--              (wh_integrity_mark_answer_pending). An origin that answers before the send returns (a fast origin, or
--              this host's continuation delayed under load) cleared the wait first, and the late record put it back:
--              the bucket counted against the outstanding-answer cap, and lost its answered state, until the answer
--              timeout. The drain now records first, the same rule the paced backfill follows (#2222), and a send that
--              fails takes the record back:
--
--              1. wh_integrity_withdraw_answer_pending: for the keyed buckets still awaiting the answer the drain's
--                 record set (the same instant and timeout) and claimed at that instant, clears the wait and returns the
--                 attempt the claim stamped (and the exhaustion that attempt marked), so a send that never left costs no
--                 attempt. last_repair_at stays, so the bucket is offered again after its backoff, not on the next tick
--                 against a broker that just failed. A bucket an answer reached meanwhile, or a later request, is left
--                 as it stands.
--
--              Number 395: 392 to 394 are taken by the changes this one ships with.
--
-- Reversal: reversible (a new function; the reverse drops it, and an older release never calls it)
-- Dependencies: 090 (wh_integrity_ledger), 320 (answer_pending_until, answered_at, exhausted_at)
-- Objects: wh_integrity_withdraw_answer_pending

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_withdraw_answer_pending');

-- Takes back the answer-pending record of a repair request whose send failed: the wait clears and the claim's attempt is
-- returned, only for a bucket still awaiting the answer that record set and claimed at p_now. Returns the rows withdrawn.
-- <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RepairDrainRecordsBeforeSendingTests.cs:Withdraw_ClearsTheWait_AndReturnsTheAttemptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RepairDrainRecordsBeforeSendingTests.cs:Withdraw_OfTheClaimThatSpentTheLastAttempt_LiftsTheExhaustionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RepairDrainRecordsBeforeSendingTests.cs:Withdraw_AfterTheAnswerLanded_LeavesTheAnswerStandingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RepairDrainRecordsBeforeSendingTests.cs:Withdraw_OfAnotherRecord_LeavesItStandingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_withdraw_answer_pending(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_now               TIMESTAMPTZ,
  p_timeout_secs      INTEGER
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_withdrawn INTEGER;
BEGIN
  WITH keys AS (
    SELECT DISTINCT COALESCE(k.tenant, '') AS tenant, k.etype, k.sid
    FROM unnest(p_tenant_scopes, p_event_types, p_stream_ids) AS k(tenant, etype, sid)
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET answer_pending_until = NULL,
      repair_attempts      = GREATEST(l.repair_attempts - 1, 0),
      exhausted_at         = NULL,
      last_touched         = p_now
  FROM keys k
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = k.tenant AND l.event_type = k.etype AND l.stream_id = k.sid
    AND l.answered_at IS NULL
    AND l.answer_pending_until = p_now + make_interval(secs => p_timeout_secs)
    AND l.last_repair_at = p_now;
  GET DIAGNOSTICS v_withdrawn = ROW_COUNT;
  RETURN v_withdrawn;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_withdraw_answer_pending(UUID, TEXT[], TEXT[], UUID[], TIMESTAMPTZ, INTEGER) IS
'395 (#2223): takes back the answer-pending record of a repair request whose send failed. For each keyed bucket still awaiting the answer recorded at p_now with p_timeout_secs, and claimed at p_now: the wait clears, the claim''s attempt is returned and its exhaustion lifted, and last_repair_at stays, so the bucket is offered again after its backoff. A bucket an answer reached, or a later request, is left alone. Returns the rows withdrawn.';
