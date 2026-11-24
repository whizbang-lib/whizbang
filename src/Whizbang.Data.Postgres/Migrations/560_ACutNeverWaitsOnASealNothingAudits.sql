-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 560_ACutNeverWaitsOnASealNothingAudits.sql
-- Date: 2026-11-23
-- Description: The cut gate counts this service's seal for an origin only while this service audits that origin, and
--              the seals nothing audits are listed by origin (#2544).
--
--              _wh_cut_awaits_seal (460, 557) held a cut while a stream held a positioned received event at or above
--              this service's verified seal for the event's origin (wh_integrity_seals.sealed_through). A seal advances
--              only through this service's audit of the origin, and the audit asks only the origins it has heard a
--              checkpoint from, at the request address the checkpoint announced (wh_integrity_known_origins with a
--              request_topic; the audit worker skips a known origin with none). A seal row for an origin whose
--              checkpoints never reach this service, or reach it without an address, therefore has no path to
--              advance, and every stream holding a positioned event from that origin was held on every sweep, forever.
--
--              The rule: the seal holds a cut only when its origin is audited here (known, with a request address).
--              For any other origin the seal is not consulted, as for an origin that has no seal row at all; the cut
--              still waits for the origin lane's sealed frontier, so it removes only history whose part of this
--              service's digest is already fixed, and what it removes is carried (_wh_carry_cut) as every cut is. Once
--              the origin's checkpoints arrive, it is audited and its seal holds cuts again.
--
--              wh_integrity_unaudited_seals lists the seal rows whose origin is not audited here, so the maintenance
--              cycle can warn naming each origin and count them: the missing checkpoint route becomes visible.
--
--              Cost: one primary-key probe of wh_integrity_known_origins (one row per origin) per event the gate reads.
--              The listing reads wh_integrity_seals and wh_integrity_known_origins, each one row per origin.
--
--              Number 560: 557 and 558 are on develop and 559 is taken by a change in flight.
--
-- Reversal: reversible (the down file restores 557's gate and drops the listing; streams it let through stay cut)
-- Dependencies: 339 (wh_integrity_known_origins), 460 and 557 (_wh_cut_awaits_seal), 490 (wh_integrity_seals readings)
-- Objects: _wh_cut_awaits_seal, wh_integrity_unaudited_seals
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Whether a cut of the streams (at or below p_through_version; NULL: whole streams) must wait for the seal. Only when
-- integrity_cut_waits_for_seal is on. An event the digest counts must sit below its lane's sealed frontier; a received
-- one also below this service's verified seal for its origin, when it audits that origin (560: known, with a request
-- address); a local one, with integrity_cut_waits_for_consumers on, also below the lowest seal a consumer reported in the
-- last 7 days. 557: a received event with no origin position never waits.
-- <docs>resilience/stream-integrity#a-cut-waits-for-the-seal</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_OfAPositionedEventFromAnOriginThisServiceNeverAudits_IsNotHeldByItsSealAtZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_OfAPositionedEventFromAnAuditedOrigin_IsStillHeldByItsSealAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_OfAPositionedEventFromAKnownOriginWithNoRequestAddress_IsNotHeldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Archive_FromAnUnauditedOrigin_StillWaitsForItsLanesSealedFrontierAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:Truncate_OfReceivedHistoryWithNoOriginSequence_IsNotHeld_EvenWithNothingSealedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnpositionedReceivedHistorySqlTests.cs:Truncate_OfLocalHistoryTheStamperHasNotSequenced_IsStillHeldAsync</tests>
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
    -- 560 (#2544): this service's seal for an origin counts only while this service audits that origin.
    LEFT JOIN __SCHEMA__.wh_integrity_known_origins k
      ON k.origin_service_id = es.origin_service_id AND NULLIF(k.request_topic, '') IS NOT NULL
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
        OR (es.origin_service_id IS NOT NULL AND s.sealed_through IS NOT NULL AND k.origin_service_id IS NOT NULL
            AND es.origin_commit_sequence >= s.sealed_through)
        OR (es.origin_service_id IS NULL AND v_lowest_consumer IS NOT NULL
            AND es.commit_sequence >= v_lowest_consumer)));
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_cut_awaits_seal(UUID[], BIGINT) IS
  '460 (#2363): whether a cut of the streams must wait: integrity_cut_waits_for_seal on and an event the digest counts at or above its lane''s sealed frontier, a received one at or above this service''s seal for its origin, or (integrity_cut_waits_for_consumers) a local one at or above the lowest seal a consumer reported in 7 days. 557 (#2529): a received event with no origin position never waits. 560 (#2544): the seal counts only for an origin this service audits (in wh_integrity_known_origins with a request address); a seal nothing audits has no path to advance, so it never holds a cut, and the lane''s sealed frontier still does.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The seal rows whose origin this service does not audit: no checkpoint from it was ever recorded, or none announced a
-- request address. Such a seal cannot advance and no longer holds a cut; the maintenance cycle names each origin.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_unaudited_seals');
-- <docs>resilience/stream-integrity#a-seal-nothing-audits</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:UnauditedSeals_NameEachOriginWithASealThatNothingAudits_AndNotTheAuditedOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_unaudited_seals()
RETURNS TABLE(origin_service_id UUID, sealed_through BIGINT, updated_at TIMESTAMPTZ, known BOOLEAN) AS $$
  SELECT s.origin_service_id, s.sealed_through, s.updated_at, k.origin_service_id IS NOT NULL
  FROM __SCHEMA__.wh_integrity_seals s
  LEFT JOIN __SCHEMA__.wh_integrity_known_origins k ON k.origin_service_id = s.origin_service_id
  WHERE NULLIF(k.request_topic, '') IS NULL
  ORDER BY s.origin_service_id;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_unaudited_seals() IS
  '560 (#2544): the seal rows whose origin this service does not audit (not in wh_integrity_known_origins, or known with no request address), with whether a checkpoint was ever recorded. Such a seal cannot advance and does not hold a cut.';
