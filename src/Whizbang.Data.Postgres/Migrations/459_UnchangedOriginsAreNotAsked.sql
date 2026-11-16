-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 459_UnchangedOriginsAreNotAsked.sql
-- Date: 2026-11-16
-- Description: An audit does not ask an origin whose state has not moved since the last full match (#2349).
--
--              The routine audit asks each origin for the window above the consumer's seal on every cycle, and the
--              origin and the consumer each fold that window, though in a quiet fleet it is the same empty window every
--              time. The window above the seal can only change when the origin emits, when this service receives, or
--              when the origin rewrites its history. So at each full match the consumer remembers, beside the seal:
--              - match_sealed_through: the seal (set only while every event held from the origin lies below it;
--                otherwise NULL, so an unverified event held above the seal keeps the asks coming),
--              - match_origin_generation: the origin generation recorded beside the seal,
--              - match_checkpoint_sequence: the origin's last checkpoint position (wh_integrity_known_origins),
--              - match_received_max: the highest origin position this store holds from it.
--              wh_integrity_decide_origin_ask skips while all four are as remembered and the last ask
--              (wh_integrity_known_origins.last_asked_at) is younger than the bound; any other answer asks and stamps
--              the ask. A mismatch forgets the memo. The memo never creates a seal row: the seal guard (408) reads a
--              missing row as first contact.
--
--              Cost: one decision per origin per audit reads the seal row, the known-origin row and the top of one
--              range of idx_event_store_origin_lane (155); recording a match does the same once per compare. No index.
--
--              Number 459: after 458 in this bundle.
--
-- Reversal: reversible (the columns and the functions go; every audit asks again, as before)
-- Dependencies: 092 (wh_integrity_seals), 093 (origin_generation), 155 (idx_event_store_origin_lane),
--               339 (wh_integrity_known_origins), 407 (last_commit_sequence)
-- Objects: wh_integrity_seals.match_sealed_through, wh_integrity_seals.match_origin_generation,
--          wh_integrity_seals.match_checkpoint_sequence, wh_integrity_seals.match_received_max,
--          wh_integrity_known_origins.last_asked_at, _wh_integrity_received_max, wh_integrity_record_compare_outcome,
--          wh_integrity_decide_origin_ask

ALTER TABLE __SCHEMA__.wh_integrity_seals
  ADD COLUMN IF NOT EXISTS match_sealed_through BIGINT,
  ADD COLUMN IF NOT EXISTS match_origin_generation BIGINT,
  ADD COLUMN IF NOT EXISTS match_checkpoint_sequence BIGINT,
  ADD COLUMN IF NOT EXISTS match_received_max BIGINT;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_seals.match_sealed_through IS
'459 (#2349): the seal at the last full match of the window above it; NULL when none is remembered. The audit skips the ask while it and the other match_ columns are unchanged.';

ALTER TABLE __SCHEMA__.wh_integrity_known_origins
  ADD COLUMN IF NOT EXISTS last_asked_at TIMESTAMPTZ;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_known_origins.last_asked_at IS
'459 (#2349): when the audit last asked this origin for its window, on the audit''s clock. An unchanged origin is still asked once the last ask is MaxAskSkipMinutes old.';

SELECT __SCHEMA__.drop_all_overloads('_wh_integrity_received_max');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_record_compare_outcome');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_decide_origin_ask');

-- The highest origin position this store holds from an origin: the top of one range of idx_event_store_origin_lane.
-- <docs>resilience/stream-integrity#unchanged-origins-are-not-asked</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAskMemoSqlTests.cs:TheHighestPositionHeld_IsReadThroughTheOriginLaneIndex_UnderAGenericPlanAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_integrity_received_max(p_origin UUID) RETURNS BIGINT
LANGUAGE sql STABLE
AS $$
  SELECT MAX(es.origin_commit_sequence) FROM __SCHEMA__.wh_event_store es WHERE es.origin_service_id = p_origin;
$$;

-- How a type-level compare of the window above the seal ended. A full match remembers the four values, the seal only
-- while everything held from the origin lies below it; a mismatch forgets. Never creates a seal row.
-- <docs>resilience/stream-integrity#unchanged-origins-are-not-asked</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAskMemoSqlTests.cs:AMismatch_ForgetsTheMemo_SoTheNextAuditAsksAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAskMemoSqlTests.cs:AMatch_WhileThisStoreHoldsEventsAtOrAboveTheSeal_IsNotRememberedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAskMemoSqlTests.cs:AnOriginWithNoSeal_IsAlwaysAsked_AndGetsNoRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_record_compare_outcome(p_origin UUID, p_full_match BOOLEAN) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_received BIGINT;
BEGIN
  IF NOT p_full_match THEN
    UPDATE __SCHEMA__.wh_integrity_seals
    SET match_sealed_through = NULL
    WHERE origin_service_id = p_origin AND match_sealed_through IS NOT NULL;
    RETURN;
  END IF;
  v_received := __SCHEMA__._wh_integrity_received_max(p_origin);
  UPDATE __SCHEMA__.wh_integrity_seals s
  SET match_sealed_through = CASE WHEN COALESCE(v_received, -1) < s.sealed_through THEN s.sealed_through END,
      match_origin_generation = s.origin_generation,
      match_checkpoint_sequence = (SELECT k.last_commit_sequence FROM __SCHEMA__.wh_integrity_known_origins k
                                   WHERE k.origin_service_id = p_origin),
      match_received_max = v_received
  WHERE s.origin_service_id = p_origin;
END;
$$;

-- Whether the audit asks an origin now: 3 = skip (a full match is remembered, none of the four moved, and the last ask
-- is younger than p_max_skip); 0 = nothing remembered, 1 = something moved, 2 = the bound came round. Every answer but
-- 3 stamps p_now as the last ask.
-- <docs>resilience/stream-integrity#unchanged-origins-are-not-asked</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAskMemoSqlTests.cs:AfterAFullMatch_AnUnchangedOrigin_IsSkippedUntilTheBound_ThenAskedAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAskMemoSqlTests.cs:AnyOfTheFourMoving_AsksAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TwoServiceAskSkipTests.cs:AnIdlePair_IsAskedOncePerMaximumSkip_NotOncePerAuditAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_decide_origin_ask(
  p_origin UUID, p_now TIMESTAMPTZ, p_max_skip INTERVAL
) RETURNS SMALLINT
LANGUAGE plpgsql
AS $$
DECLARE
  v_seal __SCHEMA__.wh_integrity_seals%ROWTYPE;
  v_checkpoint BIGINT;
  v_last_asked TIMESTAMPTZ;
  v_decision SMALLINT;
BEGIN
  SELECT k.last_commit_sequence, k.last_asked_at INTO v_checkpoint, v_last_asked
  FROM __SCHEMA__.wh_integrity_known_origins k WHERE k.origin_service_id = p_origin;
  SELECT * INTO v_seal FROM __SCHEMA__.wh_integrity_seals s WHERE s.origin_service_id = p_origin;

  IF v_seal.match_sealed_through IS NULL THEN
    v_decision := 0;
  ELSIF v_seal.sealed_through <> v_seal.match_sealed_through
     OR v_seal.origin_generation IS DISTINCT FROM v_seal.match_origin_generation
     OR v_checkpoint IS DISTINCT FROM v_seal.match_checkpoint_sequence
     OR __SCHEMA__._wh_integrity_received_max(p_origin) IS DISTINCT FROM v_seal.match_received_max THEN
    v_decision := 1;
  ELSIF v_last_asked IS NULL OR v_last_asked <= p_now - p_max_skip THEN
    v_decision := 2;
  ELSE
    RETURN 3;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_known_origins SET last_asked_at = p_now WHERE origin_service_id = p_origin;
  RETURN v_decision;
END;
$$;
