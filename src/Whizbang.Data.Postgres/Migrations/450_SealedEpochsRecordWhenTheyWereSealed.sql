-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 450_SealedEpochsRecordWhenTheyWereSealed.sql
-- Date: 2026-11-15
-- Description: The sealed-history re-check resumes where it stopped and reaches back by time (#2353).
--
--              verify_digest_epochs (092), the re-check of sealed digest epochs, walked every lane from epoch 0 on
--              every call and stopped at its cap. Past the cap (10,000 epochs by default) the later epochs were never
--              re-checked, and every run paid again for the same first epochs. Nothing recorded when an epoch was
--              sealed, so the re-check could not be limited to recent history either.
--
--                the record         wh_digest_epoch_seals holds one row per sealed epoch per lane, empty epochs
--                                   included: when it was sealed and when it was last re-checked. A trigger on the
--                                   frontier writes it as the frontier advances, so close_digest_epochs (193) is not
--                                   copied. wh_digest_epochs rows cannot hold it: a refold deletes and rewrites them,
--                                   and an empty epoch has none.
--                the existing       every epoch already sealed is recorded once, sealed at its earliest bucket's
--                                   closed_at, or at the frontier's last advance when it holds no bucket.
--                the re-check       wh_recheck_digest_epochs re-checks the epochs sealed within a lookback that were
--                                   not re-checked within an interval, least recently re-checked first, at most a cap
--                                   per call. It refolds a drifted epoch, or reports it only. Each re-checked epoch
--                                   records the time, so the next call resumes past it.
--                the old entry      verify_digest_epochs keeps its arity and is the re-check with no lookback and no
--                                   interval: it rotates through every epoch instead of restarting at epoch 0.
--
--              Cost: one row per sealed epoch (bounded by epochs, never by events); the trigger inserts the epochs a
--              frontier update closed, which is one row per closure. A re-check reads its candidates through
--              idx_digest_epoch_seals_rotation and folds only the epochs it re-checks.
--
--              Number 450: 446 is the highest on develop and 447 to 449 are taken by the predicate bundle.
--
-- Reversal: lossy (the seal record, its trigger and the re-check go, and verify_digest_epochs returns to its 092
--           definition; the record of when each epoch was sealed and re-checked is lost)
-- Dependencies: 092 (wh_digest_epochs, wh_digest_epoch_frontiers, _wh_epoch_buckets, verify_digest_epochs),
--               408 (_wh_fold_digest_epoch)
-- Objects: wh_digest_epoch_seals, idx_digest_epoch_seals_rotation, _wh_record_epoch_seals,
--          trg_digest_epoch_frontier_seals, _wh_backfill_epoch_seals, wh_recheck_digest_epochs, verify_digest_epochs
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_digest_epoch_seals (
  origin_service_id UUID        NOT NULL,   -- zero-uuid = the local lane (wh_digest_epochs convention)
  epoch_id          BIGINT      NOT NULL,
  sealed_at         TIMESTAMPTZ NOT NULL,
  rechecked_at      TIMESTAMPTZ NULL,       -- NULL = never re-checked
  PRIMARY KEY (origin_service_id, epoch_id)
);

COMMENT ON TABLE __SCHEMA__.wh_digest_epoch_seals IS
'Issue #2353: one row per sealed digest epoch per lane, empty epochs included: when it was sealed and when the sealed-history re-check last recomputed it. Bounded by epochs. Written by the frontier trigger.';

-- The rotation order: never re-checked first, then least recently re-checked, oldest seal first within each.
CREATE INDEX IF NOT EXISTS idx_digest_epoch_seals_rotation
  ON __SCHEMA__.wh_digest_epoch_seals (rechecked_at NULLS FIRST, sealed_at);

-- Records the epochs a frontier change sealed, and forgets those above a frontier that went back. The sealed time is
-- the frontier's own updated_at, which close_digest_epochs sets to its p_now on every advance.
-- <docs>resilience/stream-integrity#seal-re-check</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Closing_RecordsWhenEachEpochWasSealed_EmptyEpochsIncludedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:AFrontierThatGoesBack_ForgetsTheEpochsAboveItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_record_epoch_seals() RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_old BIGINT := CASE WHEN TG_OP = 'UPDATE' THEN OLD.closed_through_epoch ELSE -1 END;
BEGIN
  IF NEW.closed_through_epoch > v_old THEN
    INSERT INTO __SCHEMA__.wh_digest_epoch_seals (origin_service_id, epoch_id, sealed_at)
    SELECT NEW.origin_service_id, e, NEW.updated_at
    FROM generate_series(v_old + 1, NEW.closed_through_epoch) e
    ON CONFLICT (origin_service_id, epoch_id) DO NOTHING;
  ELSIF NEW.closed_through_epoch < v_old THEN
    DELETE FROM __SCHEMA__.wh_digest_epoch_seals s
    WHERE s.origin_service_id = NEW.origin_service_id AND s.epoch_id > NEW.closed_through_epoch;
  END IF;
  RETURN NEW;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_record_epoch_seals() IS
'Internal (#2353): the frontier trigger; records each epoch a frontier advance sealed in wh_digest_epoch_seals.';

DROP TRIGGER IF EXISTS trg_digest_epoch_frontier_seals ON __SCHEMA__.wh_digest_epoch_frontiers;
CREATE TRIGGER trg_digest_epoch_frontier_seals
  AFTER INSERT OR UPDATE OF closed_through_epoch ON __SCHEMA__.wh_digest_epoch_frontiers
  FOR EACH ROW EXECUTE FUNCTION __SCHEMA__._wh_record_epoch_seals();

-- Records every epoch already sealed that has no row: sealed at its earliest bucket's closed_at, or at the frontier's
-- last advance when it holds no bucket. Run once by this migration; idempotent.
-- <docs>resilience/stream-integrity#seal-re-check</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:TheBackfill_RecordsEpochsSealedBeforeTheRecordExisted_AtTheirFoldTimeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_backfill_epoch_seals() RETURNS INTEGER
LANGUAGE sql
AS $$
  WITH recorded AS (
    INSERT INTO __SCHEMA__.wh_digest_epoch_seals (origin_service_id, epoch_id, sealed_at)
    SELECT f.origin_service_id, e,
           COALESCE((SELECT MIN(de.closed_at) FROM __SCHEMA__.wh_digest_epochs de
                     WHERE de.origin_service_id = f.origin_service_id AND de.epoch_id = e), f.updated_at)
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
    CROSS JOIN LATERAL generate_series(0, f.closed_through_epoch) e
    WHERE f.closed_through_epoch >= 0
    ON CONFLICT (origin_service_id, epoch_id) DO NOTHING
    RETURNING 1
  )
  SELECT COUNT(*)::int FROM recorded;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_backfill_epoch_seals() IS
'Internal (#2353): records the epochs sealed before wh_digest_epoch_seals existed. Idempotent.';

SELECT __SCHEMA__._wh_backfill_epoch_seals();

SELECT __SCHEMA__.drop_all_overloads('wh_recheck_digest_epochs');

-- The sealed-history re-check. Candidates are the epochs sealed at or after p_now - p_lookback_seconds (every epoch
-- when NULL) that were never re-checked or were last re-checked before p_now - p_every_seconds, least recently
-- re-checked first. An epoch holding an UNSETTLED arrival (a fresh redelivery into a sealed range) is skipped whole,
-- exactly as closure skips it, and stays first in line for the next call. A drifted epoch is refolded when p_refold,
-- and is only counted otherwise. Returns the epochs recomputed and how many of them drifted.
-- <docs>resilience/stream-integrity#seal-re-check</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Recheck_ResumesPastWhatItChecked_NeverRestartingAtTheFirstEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Recheck_LeavesEpochsSealedBeforeTheLookbackAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Recheck_ChecksAnEpochAtMostOncePerInterval_ThenAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Recheck_ADriftedEpoch_IsRefoldedOrOnlyReported_AsAskedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Recheck_AnEpochWithAnUnsettledArrival_IsSkippedAndStaysFirstAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Coordinator_RecheckDigestEpochsAsync_DrivesTheSqlFunctionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_recheck_digest_epochs(
  p_now              TIMESTAMPTZ,
  p_settle_seconds   INTEGER,
  p_max_epochs       INTEGER,
  p_lookback_seconds BIGINT,
  p_every_seconds    BIGINT,
  p_refold           BOOLEAN
) RETURNS TABLE (epochs_checked INTEGER, epochs_drifted INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__::uuid;
  v_lane    UUID;
  v_epoch   BIGINT;
  v_width   BIGINT;
  v_blocked BOOLEAN;
  v_stored  TEXT[];
  v_fresh   TEXT[];
  v_checked INTEGER := 0;
  v_drifted INTEGER := 0;
BEGIN
  FOR v_lane, v_epoch, v_width IN
    SELECT s.origin_service_id, s.epoch_id, f.epoch_width
    FROM __SCHEMA__.wh_digest_epoch_seals s
    JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = s.origin_service_id
    WHERE s.epoch_id <= f.closed_through_epoch
      AND (p_lookback_seconds IS NULL OR s.sealed_at >= p_now - make_interval(secs => p_lookback_seconds))
      AND (s.rechecked_at IS NULL OR s.rechecked_at <= p_now - make_interval(secs => GREATEST(p_every_seconds, 0)))
    ORDER BY s.rechecked_at NULLS FIRST, s.sealed_at, s.origin_service_id, s.epoch_id
  LOOP
    EXIT WHEN v_checked >= p_max_epochs;

    IF v_lane = c_zero THEN
      SELECT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es
        WHERE es.origin_service_id IS NULL
          AND es.commit_sequence >= v_epoch * v_width AND es.commit_sequence < (v_epoch + 1) * v_width
          AND es.created_at >= p_now - make_interval(secs => p_settle_seconds)
      ) INTO v_blocked;
    ELSE
      SELECT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es
        WHERE es.origin_service_id = v_lane
          AND es.origin_commit_sequence >= v_epoch * v_width AND es.origin_commit_sequence < (v_epoch + 1) * v_width
          AND es.created_at >= p_now - make_interval(secs => p_settle_seconds)
      ) INTO v_blocked;
    END IF;
    CONTINUE WHEN v_blocked;

    SELECT array_agg((de.scope_tenant, de.event_type, de.digest_lo, de.digest_hi, de.event_count)::text
                     ORDER BY de.scope_tenant, de.event_type)
    INTO v_stored
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane AND de.epoch_id = v_epoch;

    SELECT array_agg((b.b_tenant, b.b_type, b.b_lo, b.b_hi, b.b_cnt)::text ORDER BY b.b_tenant, b.b_type)
    INTO v_fresh
    FROM __SCHEMA__._wh_epoch_buckets(v_lane, v_epoch, v_width) b;

    IF v_stored IS DISTINCT FROM v_fresh THEN
      IF p_refold THEN
        PERFORM __SCHEMA__._wh_fold_digest_epoch(v_lane, v_epoch, v_width, p_now);
      END IF;
      v_drifted := v_drifted + 1;
    END IF;

    UPDATE __SCHEMA__.wh_digest_epoch_seals s
    SET rechecked_at = p_now
    WHERE s.origin_service_id = v_lane AND s.epoch_id = v_epoch;
    v_checked := v_checked + 1;
  END LOOP;

  RETURN QUERY SELECT v_checked, v_drifted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_recheck_digest_epochs(TIMESTAMPTZ, INTEGER, INTEGER, BIGINT, BIGINT, BOOLEAN) IS
'Issue #2353: the sealed-history re-check. Recomputes sealed epochs within a lookback, each at most once per interval, least recently re-checked first, up to a cap; refolds or reports drift. Non-zero drift means an unaccounted write path.';

-- 092's entry point, same arity: the re-check over every sealed epoch with no interval, refolding drift. It rotates
-- through the epochs instead of restarting at epoch 0.
-- <docs>resilience/stream-integrity#seal-re-check</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:VerifyDigestEpochs_RotatesThroughTheEpochs_InsteadOfRestartingAtZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.verify_digest_epochs(
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER,
  p_max_epochs     INTEGER
) RETURNS TABLE (epochs_checked INTEGER, epochs_drifted INTEGER)
LANGUAGE sql
AS $$
  SELECT r.epochs_checked, r.epochs_drifted
  FROM __SCHEMA__.wh_recheck_digest_epochs(p_now, p_settle_seconds, p_max_epochs, NULL, 0, TRUE) r;
$$;

COMMENT ON FUNCTION __SCHEMA__.verify_digest_epochs IS
'Seal backstop (092), since 450 (#2353) the sealed-history re-check over every epoch with no interval: recomputes closed epochs least recently re-checked first, refolds on drift. Non-zero drift means an unaccounted write path.';
