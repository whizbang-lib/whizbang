-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 450_SealedEpochsRecordWhenTheyWereSealed.sql (#2353): the seal record, its trigger and the re-check
-- go, and verify_digest_epochs returns to its 092 definition, which walks every lane from epoch 0.

DROP TRIGGER IF EXISTS trg_digest_epoch_frontier_seals ON __SCHEMA__.wh_digest_epoch_frontiers;
SELECT __SCHEMA__.drop_all_overloads('_wh_record_epoch_seals');
SELECT __SCHEMA__.drop_all_overloads('_wh_backfill_epoch_seals');
SELECT __SCHEMA__.drop_all_overloads('wh_recheck_digest_epochs');
DROP TABLE IF EXISTS __SCHEMA__.wh_digest_epoch_seals;
SELECT __SCHEMA__.drop_all_overloads('verify_digest_epochs');

-- <docs>resilience/stream-integrity#seal-re-check</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:VerifyDigestEpochs_DetectsAndHealsADriftedSeal_ThenReportsCleanAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.verify_digest_epochs(
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER,
  p_max_epochs     INTEGER
) RETURNS TABLE (epochs_checked INTEGER, epochs_drifted INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__::uuid;
  v_lane     UUID;
  v_frontier BIGINT;
  v_width    BIGINT;
  v_epoch    BIGINT;
  v_blocked  BOOLEAN;
  v_stored   TEXT[];
  v_fresh    TEXT[];
  v_checked  INTEGER := 0;
  v_drifted  INTEGER := 0;
BEGIN
  FOR v_lane, v_frontier, v_width IN
    SELECT f.origin_service_id, f.closed_through_epoch, f.epoch_width
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
    WHERE f.closed_through_epoch >= 0
  LOOP
    v_epoch := 0;
    WHILE v_epoch <= v_frontier LOOP
      IF v_checked >= p_max_epochs THEN
        RETURN QUERY SELECT v_checked, v_drifted;
        RETURN;
      END IF;

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

      IF NOT v_blocked THEN
        SELECT array_agg((de.scope_tenant, de.event_type, de.digest_lo, de.digest_hi, de.event_count)::text
                         ORDER BY de.scope_tenant, de.event_type)
        INTO v_stored
        FROM __SCHEMA__.wh_digest_epochs de
        WHERE de.origin_service_id = v_lane AND de.epoch_id = v_epoch;

        SELECT array_agg((b.b_tenant, b.b_type, b.b_lo, b.b_hi, b.b_cnt)::text
                         ORDER BY b.b_tenant, b.b_type)
        INTO v_fresh
        FROM __SCHEMA__._wh_epoch_buckets(v_lane, v_epoch, v_width) b;

        IF v_stored IS DISTINCT FROM v_fresh THEN
          PERFORM __SCHEMA__._wh_fold_digest_epoch(v_lane, v_epoch, v_width, p_now);
          v_drifted := v_drifted + 1;
        END IF;
        v_checked := v_checked + 1;
      END IF;

      v_epoch := v_epoch + 1;
    END LOOP;
  END LOOP;

  RETURN QUERY SELECT v_checked, v_drifted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.verify_digest_epochs IS
'Sweep backstop: recomputes each closed epoch from the store, compares bucket-for-bucket, refolds on drift. Non-zero drift means an unaccounted write path: alarm-worthy, not routine. Epochs with unsettled arrivals are skipped whole.';
