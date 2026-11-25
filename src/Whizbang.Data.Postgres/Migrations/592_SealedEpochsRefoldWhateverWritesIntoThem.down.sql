-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 592_SealedEpochsRefoldWhateverWritesIntoThem.sql (#2572): the event store's touch triggers, the
-- commit refold, the touch table and the helpers go, and _wh_fold_digest_epoch returns to its 408 definition. Folds
-- already refolded stay, which is correct: each equals the store.

DROP TRIGGER IF EXISTS wh_event_store_touches_sealed_epochs_insert_trg ON __SCHEMA__.wh_event_store;
DROP TRIGGER IF EXISTS wh_event_store_touches_sealed_epochs_update_trg ON __SCHEMA__.wh_event_store;
DROP TRIGGER IF EXISTS wh_sealed_epoch_touches_refold_trg ON __SCHEMA__.wh_sealed_epoch_touches;

DROP FUNCTION IF EXISTS __SCHEMA__._wh_touch_sealed_epochs_on_update();
DROP FUNCTION IF EXISTS __SCHEMA__._wh_touch_sealed_epochs_on_insert();
DROP FUNCTION IF EXISTS __SCHEMA__._wh_refold_touched_epochs_at_commit();
DROP FUNCTION IF EXISTS __SCHEMA__._wh_refold_touched_epochs();
DROP FUNCTION IF EXISTS __SCHEMA__._wh_refold_epoch_if_drifted(UUID, BIGINT, BIGINT, TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_touch_sealed_epochs(UUID[], BIGINT[]);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_digest_epoch_lock_key(UUID, BIGINT);

-- 408's definition.
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
'Internal: (re)writes one epoch''s bucket rows from the canonical fold (_wh_epoch_buckets). 408 (#2262): refolding a sealed epoch of the local lane notes its first position as rewritten.';

DROP TABLE IF EXISTS __SCHEMA__.wh_sealed_epoch_touches;
