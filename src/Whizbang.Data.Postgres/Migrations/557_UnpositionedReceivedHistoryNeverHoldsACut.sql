-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 557_UnpositionedReceivedHistoryNeverHoldsACut.sql
-- Date: 2026-11-23
-- Description: The cut gate never holds a cut for a received event with no position on its origin's lane, so archiving
--              and prefix truncation no longer answer awaiting_seal forever on such streams (#2529).
--
--              _wh_cut_awaits_seal (460, #2363) treated a received event whose origin_commit_sequence is NULL as above
--              the seal. Nothing ever fills that column for an event already stored, so a stream holding one could
--              never be archived or truncated: every pass answered awaiting_seal and the next asked again.
--
--              The rule: a received event with no origin position does not hold a cut. The seal gate's guarantee is
--              that a cut removes only history whose part of the digest is already fixed, so cutting it changes no
--              answer another service compares. A received event with no position is never folded into a sealed
--              epoch: the digest counts it only in its origin lane's open window (compute_type_digests_epoch, the
--              NULL-sequence branch), no frontier or verified seal can ever cover it, and the gate would wait for a
--              seal that never comes. A cut of it is carried (_wh_carry_cut) into wh_digest_epoch_removed at epoch -1,
--              which the open window always adds back and no refold rewrites, so the lane's answer is exactly what it
--              was before the cut. The seal has nothing to protect for it.
--
--              Unchanged: a received event with a position still waits for its lane's sealed frontier and for this
--              service's verified seal of its origin; a local event still waits for its frontier, and the stamper's
--              sequence when it has none yet (the local answer leaves an unstamped row out until it is stamped, so
--              cutting it early would change that answer); the consumers' seals still hold local history when
--              integrity_cut_waits_for_consumers is on.
--
--              Cost: none added; the same read of the streams' events, with one predicate fewer for a received row.
--
--              Number 557: the next free number after 556.
--
-- Reversal: reversible (the down file restores 460's definition; streams it let through stay cut, which is correct)
-- Dependencies: 460 (_wh_cut_awaits_seal, _wh_carry_cut, compute_type_digests_epoch's open window and removed rows)
-- Objects: _wh_cut_awaits_seal
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Whether a cut of the streams (at or below p_through_version; NULL: whole streams) must wait for the seal. Only when
-- integrity_cut_waits_for_seal is on. An event the digest counts must sit below its lane's sealed frontier; a received
-- one also below this service's verified seal for its origin, when it audits that origin; a local one, with
-- integrity_cut_waits_for_consumers on, also below the lowest seal a consumer reported in the last 7 days. 557: a
-- received event with no origin position never waits: it is never sealed, and its cut is carried into the open window.
-- <docs>resilience/stream-integrity#a-cut-waits-for-the-seal</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:Truncate_OfReceivedHistoryWithNoOriginSequence_IsNotHeld_EvenWithNothingSealedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:ArchiveWhole_OfReceivedHistoryWithNoOriginSequence_IsNotHeld_AndTheOriginsAnswerStaysAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:Truncate_OfReceivedHistoryWithAPositionAboveTheSeal_IsStillHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:Truncate_OfLocalHistoryTheStamperHasNotSequenced_IsStillHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Truncate_OfHistoryAboveTheSeal_WaitsForIt_WhenTheGateIsOn_AndProceedsWhenItIsOffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Truncate_OfReceivedHistory_WaitsForTheConsumersSeal_WhenItAuditsThatOriginAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Truncate_OfLocalHistory_WaitsForEveryConsumersReportedSeal_OnlyWhenThatSettingIsOnAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_cut_awaits_seal(p_stream_ids UUID[], p_through_version BIGINT)
RETURNS BOOLEAN AS $$
DECLARE
  v_gate BOOLEAN;
  v_consumers BOOLEAN;
  v_lowest_consumer BIGINT;
BEGIN
  SELECT (s.setting_value = 'true') INTO v_gate
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_cut_waits_for_seal';
  IF NOT COALESCE(v_gate, FALSE) THEN
    RETURN FALSE;
  END IF;

  SELECT (s.setting_value = 'true') INTO v_consumers
  FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_cut_waits_for_consumers';
  IF COALESCE(v_consumers, FALSE) THEN
    SELECT MIN(c.sealed_through) INTO v_lowest_consumer
    FROM __SCHEMA__.wh_integrity_consumer_seals c
    WHERE c.reported_at >= NOW() - INTERVAL '7 days';
  END IF;

  RETURN EXISTS (
    SELECT 1
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_digest_epoch_frontiers f
      ON f.origin_service_id = COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)
    LEFT JOIN __SCHEMA__.wh_integrity_seals s ON s.origin_service_id = es.origin_service_id
    WHERE es.stream_id = ANY(p_stream_ids)
      AND (p_through_version IS NULL OR es.version <= p_through_version)
      AND __DIGEST_ELIGIBLE__(es)
      -- 557 (#2529): a received event with no origin position is never sealed; its cut is carried into the open window.
      AND NOT (es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL)
      AND (
        f.origin_service_id IS NULL
        OR CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END IS NULL
        OR CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END
             >= (f.closed_through_epoch + 1) * f.epoch_width
        OR (es.origin_service_id IS NOT NULL AND s.sealed_through IS NOT NULL
            AND es.origin_commit_sequence >= s.sealed_through)
        OR (es.origin_service_id IS NULL AND v_lowest_consumer IS NOT NULL
            AND es.commit_sequence >= v_lowest_consumer)));
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_cut_awaits_seal(UUID[], BIGINT) IS
  '460 (#2363): whether a cut of the streams must wait: integrity_cut_waits_for_seal on and an event the digest counts at or above its lane''s sealed frontier, a received one at or above this service''s seal for its origin, or (integrity_cut_waits_for_consumers) a local one at or above the lowest seal a consumer reported in 7 days. 557 (#2529): a received event with no origin position never waits: the digest counts it only in the open window, no seal ever covers it, and a cut carries it into the open window''s removed row, so the answer does not change; waiting for it held the stream forever. A local event with no sequence yet still waits for the stamper.';
