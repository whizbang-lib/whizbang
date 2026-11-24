-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 557_UnpositionedReceivedHistoryNeverHoldsACut.sql (#2529): _wh_cut_awaits_seal goes back to 460's
-- definition, where a received event with no origin position holds the cut.

-- <docs>resilience/stream-integrity#a-cut-waits-for-the-seal</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Truncate_OfHistoryAboveTheSeal_WaitsForIt_WhenTheGateIsOn_AndProceedsWhenItIsOffAsync</tests>
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
  '460 (#2363): whether a cut of the streams must wait: integrity_cut_waits_for_seal on and an event the digest counts at or above its lane''s sealed frontier, a received one at or above this service''s seal for its origin, or (integrity_cut_waits_for_consumers) a local one at or above the lowest seal a consumer reported in 7 days.';
