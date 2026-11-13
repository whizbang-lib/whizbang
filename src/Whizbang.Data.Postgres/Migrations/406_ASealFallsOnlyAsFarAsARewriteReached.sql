-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 406_ASealFallsOnlyAsFarAsARewriteReached.sql
-- Date: 2026-11-11
-- Description: An origin generation bump invalidates only what it changed (#2262).
--
--              A consumer's seal for an origin (wh_integrity_seals.sealed_through) fell to 0 on every change of the
--              origin's generation (093's guard), and the generation is one counter per database that thirteen
--              functions bump: after rewriting ANY lane (received lanes included, which nothing this origin's
--              manifests answer covers), once per batch of routine maintenance (the superseding reaper on every
--              cycle that removes something, batched reclassification, every reconciled redelivery batch), and on
--              every archive and restore whether or not anything fold-relevant moved. A busy origin bumped tens of
--              thousands of times a day, and every consumer re-verified history it had already proven, each time.
--
--                the note           every rewrite path already refolds what it touched before it bumps:
--                                   _wh_refold_epochs_covering per lane and range, refold_digest_epochs and
--                                   _wh_fold_digest_epoch directly. Those now note, in a transaction-local setting,
--                                   the lowest position of the LOCAL lane (the origin's own, the lane its manifests
--                                   answer) the rewrite touched: the range's minimum, or a refolded sealed epoch's
--                                   first position. Closing a new epoch (an epoch above the frontier) is no rewrite
--                                   and notes nothing. No caller changes, so the thirteen functions are not copied.
--                the bump           _wh_bump_origin_generation moves the generation only when a note exists, records
--                                   (generation, lowest position) in wh_integrity_origin_rewrites, consumes the note,
--                                   and keeps the last integrity_origin_rewrite_log_rows rows. A rewrite of a
--                                   received lane alone, or an archive or restore that moved nothing fold-relevant,
--                                   no longer moves it.
--                the answer         wh_integrity_history_rewrite(since) is the lowest position rewritten after a
--                                   generation, and whether the log reaches back that far (it starts at the generation
--                                   this migration found, and loses the oldest rows past the configured count).
--                the guard          wh_integrity_seal_guard takes the floor the answer carried (093's
--                                   integrity_seal_generation_guard stays, same arity, as its floorless call): a newer
--                                   generation whose floor counts from the recorded generation or an earlier one
--                                   keeps a seal at or below the floor (and proceeds), and lowers a seal above it to
--                                   the floor (and skips the round in flight, asked from the old seal). Anything else
--                                   (no floor: an older origin, or a consumer past the log; a floor from a later
--                                   generation; a generation that went back) resets to 0 as before.
--
--              Cost: the note is a set_config per refold call and one frontier lookup per epoch fold of the local
--              lane; the bump adds one insert and one primary-key range delete; the answer reads the log by primary
--              key. None of it is on the emit path.
--
--              Number 402: 397 is the highest on develop, and the integrity bundle's changes take 398 to 403 in order.
--
-- Reversal: lossy (the rewrite log, its settings and the new guard are dropped, and the five functions return to their 092 and 093
--           definitions; seals are kept, and the next generation change resets them, as before this migration)
-- Dependencies: 092 (_wh_fold_digest_epoch, wh_digest_epoch_frontiers, wh_integrity_seals), 093 (the generation, its
--               bump, _wh_refold_epochs_covering and the guard)
-- Objects: wh_integrity_origin_rewrites, _wh_note_origin_rewrite, _wh_fold_digest_epoch, _wh_refold_epochs_covering,
--          _wh_bump_origin_generation, wh_integrity_history_rewrite, wh_integrity_seal_guard,
--          integrity_seal_generation_guard
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_origin_rewrites (
  generation     BIGINT      PRIMARY KEY,
  rewritten_from BIGINT      NOT NULL,
  rewritten_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_origin_rewrites IS
  'Issue #2262: one row per origin generation bump: the lowest position of this origin''s own lane the rewrite touched. '
  'Answers how far history was rewritten since a consumer''s recorded generation. Bounded by '
  'integrity_origin_rewrite_log_rows.';

-- The generation the log starts at: a bump from before this migration recorded no floor.
INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
SELECT 'integrity_origin_rewrite_log_from',
       COALESCE((SELECT s.setting_value FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_generation'), '0'),
       'integer',
       'Issue #2262: the origin generation the rewrite log starts at; a consumer whose recorded generation is older is answered as unknown.'
ON CONFLICT (setting_key) DO NOTHING;

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
VALUES ('integrity_origin_rewrite_log_rows', '10000', 'integer',
        'Issue #2262: how many origin generation bumps the rewrite log keeps; a consumer further behind is answered as unknown and resets its seal.')
ON CONFLICT (setting_key) DO NOTHING;

-- ===========================================================================================
-- The note: the lowest local position a rewrite in this transaction touched
-- ===========================================================================================

-- The setting is transaction-local (set_config's third argument), so a rewrite that rolls back, or a subtransaction that
-- does, leaves no note behind. LEAST ignores the NULL of a first note.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Bump_AfterLocalRewrites_RecordsTheLowestPositionTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Bump_ANoteFromARolledBackTransaction_DoesNotCarryOverAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_note_origin_rewrite(p_from BIGINT) RETURNS VOID
LANGUAGE sql
AS $$
  SELECT set_config('whizbang.origin_rewritten_from',
    COALESCE(LEAST(NULLIF(current_setting('whizbang.origin_rewritten_from', true), '')::bigint, p_from)::text, ''),
    true);
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_note_origin_rewrite(BIGINT) IS
  'Internal (#2262): notes, for this transaction, the lowest position of the local lane a rewrite touched; the next _wh_bump_origin_generation records and consumes it.';

-- 092's definition whole, plus the note: refolding an epoch at or below the local lane's frontier rewrites what a
-- consumer may have verified there, from its first position. Closing a new epoch (above the frontier) folds what was
-- already there and notes nothing.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Refold_OfASealedLocalEpoch_NotesTheEpochsStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:ClosingANewEpoch_IsNoRewrite_AndNotesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fold_digest_epoch(
  p_lane      UUID,
  p_epoch     BIGINT,
  p_width     BIGINT,
  p_closed_at TIMESTAMPTZ
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  IF p_lane = __EMPTY_UUID__::uuid
     AND p_epoch <= (SELECT f.closed_through_epoch FROM __SCHEMA__.wh_digest_epoch_frontiers f WHERE f.origin_service_id = p_lane) THEN
    PERFORM __SCHEMA__._wh_note_origin_rewrite(p_epoch * p_width);
  END IF;

  DELETE FROM __SCHEMA__.wh_digest_epochs
  WHERE origin_service_id = p_lane AND epoch_id = p_epoch;

  INSERT INTO __SCHEMA__.wh_digest_epochs
    (origin_service_id, scope_tenant, event_type, epoch_id, digest_lo, digest_hi, event_count, closed_at)
  SELECT p_lane, b.b_tenant, b.b_type, p_epoch, b.b_lo, b.b_hi, b.b_cnt, p_closed_at
  FROM __SCHEMA__._wh_epoch_buckets(p_lane, p_epoch, p_width) b;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_digest_epoch IS
'Internal: (re)writes one epoch''s bucket rows from the canonical fold (_wh_epoch_buckets). 402 (#2262): refolding a sealed epoch of the local lane notes its first position as rewritten.';

-- 093's definition whole, plus the note: the range's minimum, before the early return, because a rewrite above the
-- frontier (nothing sealed to refold) can still lie below a consumer's seal.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Bump_AfterLocalRewrites_RecordsTheLowestPositionTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Bump_AfterARewriteOfAnotherOriginsLaneOnly_LeavesTheGenerationAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Truncate_OfAReceivedStream_LeavesTheGenerationAlone_AndOfALocalStream_RecordsItsFloorAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refold_epochs_covering(
  p_lane    UUID,
  p_min_seq BIGINT,
  p_max_seq BIGINT
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_width BIGINT;
BEGIN
  IF p_lane = __EMPTY_UUID__::uuid THEN
    PERFORM __SCHEMA__._wh_note_origin_rewrite(p_min_seq);
  END IF;

  SELECT f.epoch_width INTO v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = p_lane;
  IF v_width IS NULL THEN
    RETURN;
  END IF;
  PERFORM __SCHEMA__.refold_digest_epochs(p_lane, p_min_seq / v_width, p_max_seq / v_width);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_refold_epochs_covering IS
'Internal: refolds the sealed epochs whose sequence ranges a legitimate mutation touched. 402 (#2262): a range of the local lane notes its minimum as rewritten.';

-- ===========================================================================================
-- The bump: only after a noted local rewrite, recording how far it reached
-- ===========================================================================================

-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Bump_WithNoRewriteNoted_LeavesTheGenerationAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Bump_AfterLocalRewrites_RecordsTheLowestPositionTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Bump_ConsumesTheNote_SoASecondBumpInTheSameTransactionDoesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:TheLog_KeepsTheConfiguredNumberOfRewrites_AndTheOldestGoAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_bump_origin_generation() RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_from BIGINT := NULLIF(current_setting('whizbang.origin_rewritten_from', true), '')::bigint;
  v_generation BIGINT;
  v_keep BIGINT;
BEGIN
  IF v_from IS NULL THEN
    RETURN;   -- nothing in this origin's own lane was rewritten: nothing a consumer verified changed.
  END IF;
  PERFORM set_config('whizbang.origin_rewritten_from', '', true);

  UPDATE __SCHEMA__.wh_settings
  SET setting_value = ((setting_value)::bigint + 1)::text
  WHERE setting_key = 'integrity_origin_generation'
  RETURNING (setting_value)::bigint INTO v_generation;

  INSERT INTO __SCHEMA__.wh_integrity_origin_rewrites AS r (generation, rewritten_from, rewritten_at)
  VALUES (v_generation, GREATEST(v_from, 0), NOW())
  ON CONFLICT (generation) DO UPDATE
    SET rewritten_from = LEAST(r.rewritten_from, EXCLUDED.rewritten_from),
        rewritten_at = EXCLUDED.rewritten_at;

  SELECT GREATEST(COALESCE(
    (SELECT s.setting_value::bigint FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'integrity_origin_rewrite_log_rows'),
    10000), 1) INTO v_keep;
  DELETE FROM __SCHEMA__.wh_integrity_origin_rewrites r WHERE r.generation <= v_generation - v_keep;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_bump_origin_generation IS
'Internal: advances the origin generation after a legitimate mutation of folded history. 402 (#2262): only when the transaction noted a rewrite of the local lane, recording the lowest position it touched in wh_integrity_origin_rewrites.';

-- ===========================================================================================
-- The answer: how far history was rewritten since a consumer's generation
-- ===========================================================================================

-- STABLE, so every read sees the caller's one snapshot. Vouched when the log reaches back to the asked generation:
-- it starts at integrity_origin_rewrite_log_from and keeps the last integrity_origin_rewrite_log_rows generations.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:HistoryRewrite_SinceAGenerationTheLogReaches_IsTheLowestFloorAfterItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:HistoryRewrite_SinceAGenerationOlderThanTheLog_CannotBeVouchedForAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:HistoryRewrite_SinceAGenerationFromBeforeTheLogExisted_CannotBeVouchedForAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_history_rewrite(p_since BIGINT)
RETURNS TABLE (vouched BOOLEAN, rewritten_from BIGINT)
LANGUAGE sql STABLE
AS $$
  SELECT p_since >= GREATEST(
           COALESCE((SELECT s.setting_value::bigint FROM __SCHEMA__.wh_settings s
                     WHERE s.setting_key = 'integrity_origin_rewrite_log_from'), 0),
           COALESCE((SELECT s.setting_value::bigint FROM __SCHEMA__.wh_settings s
                     WHERE s.setting_key = 'integrity_origin_generation'), 0)
             - GREATEST(COALESCE((SELECT s.setting_value::bigint FROM __SCHEMA__.wh_settings s
                                  WHERE s.setting_key = 'integrity_origin_rewrite_log_rows'), 10000), 1)),
         (SELECT MIN(r.rewritten_from) FROM __SCHEMA__.wh_integrity_origin_rewrites r WHERE r.generation > p_since);
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_history_rewrite(BIGINT) IS
  'Issue #2262: the lowest position of this origin''s own lane rewritten after a generation (NULL: nothing was), and whether the rewrite log reaches back to that generation (vouched).';

-- ===========================================================================================
-- The guard: a seal falls only as far as the rewrite reached
-- ===========================================================================================

-- TRUE = compare away; FALSE = skip this round. The seal row is locked, so two manifests of one origin decide in turn.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_ARewriteAtOrAboveTheSeal_KeepsTheSeal_AndProceedsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_ARewriteBelowTheSeal_LowersItToTheRewritePoint_NotToZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_ARewriteNamingNoPosition_KeepsTheSealAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_FrequentBumpsAboveTheSeal_NeverResetItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_AFloorFromAGenerationNewerThanTheRecordedOne_IsUnknown_AndResetsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_AnOlderGeneration_ResetsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_WithoutAFloor_ResetsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginGenerationSqlTests.cs:SealGenerationGuard_ResetsTheSealOnce_ThenProceedsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_seal_guard(
  p_origin          UUID,
  p_generation      BIGINT,
  p_rewritten_since BIGINT,
  p_rewritten_from  BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_gen  BIGINT;
  v_seal BIGINT;
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_seals (origin_service_id, sealed_through, origin_generation, updated_at)
  VALUES (p_origin, 0, p_generation, NOW())
  ON CONFLICT (origin_service_id) DO NOTHING;

  SELECT s.origin_generation, s.sealed_through INTO v_gen, v_seal
  FROM __SCHEMA__.wh_integrity_seals s
  WHERE s.origin_service_id = p_origin
  FOR UPDATE;

  IF v_gen = p_generation THEN
    RETURN TRUE;
  END IF;

  -- The floor covers every rewrite after p_rewritten_since, so it covers those after the recorded generation too.
  IF p_rewritten_since IS NOT NULL AND p_rewritten_since <= v_gen AND v_gen < p_generation THEN
    IF p_rewritten_from IS NULL OR p_rewritten_from >= v_seal THEN
      UPDATE __SCHEMA__.wh_integrity_seals
      SET origin_generation = p_generation, updated_at = NOW()
      WHERE origin_service_id = p_origin;
      RETURN TRUE;   -- nothing below the seal changed.
    END IF;
    UPDATE __SCHEMA__.wh_integrity_seals
    SET sealed_through = GREATEST(p_rewritten_from, 0), origin_generation = p_generation, updated_at = NOW()
    WHERE origin_service_id = p_origin;
    RETURN FALSE;   -- the round in flight was asked from the old seal.
  END IF;

  UPDATE __SCHEMA__.wh_integrity_seals
  SET sealed_through = 0, origin_generation = p_generation, updated_at = NOW()
  WHERE origin_service_id = p_origin;
  RETURN FALSE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_seal_guard(UUID, BIGINT, BIGINT, BIGINT) IS
'Issue #2262, the consumer guard: an unchanged generation proceeds. A newer one with the origin''s rewrite floor keeps a seal the rewrite did not reach (and proceeds) and lowers one it did to the floor (and skips the round); without a usable floor the seal resets to 0 once and the round is skipped, as before.';

-- 093's guard keeps its name and arity (a second arity beside 093's definition would make every unqualified call
-- ambiguous whenever the ledger re-runs 093), and is now the floorless call of the guard above.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginRewriteFloorSqlTests.cs:Guard_TheFloorlessGuard_ResetsAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.integrity_seal_generation_guard(
  p_origin     UUID,
  p_generation BIGINT
) RETURNS BOOLEAN
LANGUAGE sql
AS $$
  SELECT __SCHEMA__.wh_integrity_seal_guard(p_origin, p_generation, NULL, NULL);
$$;

COMMENT ON FUNCTION __SCHEMA__.integrity_seal_generation_guard(UUID, BIGINT) IS
'Consumer guard without a rewrite floor: unchanged generation proceeds; a changed one resets the seal once and skips the round. 402 (#2262): the floorless call of wh_integrity_seal_guard.';
