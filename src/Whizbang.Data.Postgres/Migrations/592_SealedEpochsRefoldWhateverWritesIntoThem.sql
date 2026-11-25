-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 592_SealedEpochsRefoldWhateverWritesIntoThem.sql
-- Date: 2026-11-24
-- Description: A sealed digest epoch is refolded by the store itself whenever a write adds an event to it or moves one
--              in or out of it, whichever path made the write (#2572).
--
--              A sealed epoch's fold (wh_digest_epochs) is what answers and audits trust for that range: nothing
--              re-reads the events behind it on the hot path. Each path that rewrote sealed history had to remember to
--              refold what it touched, and the receive path did not: a backfill or redelivery answer stored by the
--              inbox emit chain (551) lands at its origin's position, which can lie in an epoch sealed long before it
--              arrived. Nothing refolded that epoch, so its fold counted fewer events than the store held, the windowed
--              audit against the origin mismatched on every pass, the stream drill-down found the live rows equal and
--              opened nothing, and the consumer's seal never advanced past the first such epoch.
--
--              The refold now rides the write, so a path added later cannot forget it:
--                the touch      the store's own triggers record each sealed epoch a write changes, as one row per
--                               (transaction, lane, epoch) in wh_sealed_epoch_touches. An insert is read once per
--                               statement from its transition table (set-based; an outbox emit, whose rows carry no
--                               position yet, finds nothing to record). An update of origin_service_id or
--                               origin_commit_sequence is read per row, on both sides: the lane and position it left
--                               and the one it joined. Only digest-eligible rows count: an ephemeral, at-most-once or
--                               exempt event is in no fold. A row that inherited a superseding slot also touches the
--                               epoch its slot sits in.
--                the refold     at commit, a deferred constraint trigger on the touch table refolds each recorded
--                               epoch once, in (lane, epoch) order, under a per-epoch advisory lock, so two transactions
--                               writing into one sealed epoch fold one after the other and the later one sees the
--                               earlier one's event. An epoch is rewritten only when its fresh fold differs from its
--                               rows, so a write that leaves the fold as it was (a restore that hands back what its cut
--                               carried, an event inserted and removed again) rewrites nothing. A refold of the local
--                               lane notes the rewrite (408), and the generation is bumped once, so this service's
--                               consumers lower their seals as far as the rewrite reached.
--                one fold       _wh_fold_digest_epoch (408's definition whole) now also clears the touch of the epoch it
--                               folds, so a path that already refolds what it changed (the redelivery reconcile and the
--                               position fill, adoption, origin correction and re-attribution, restore) leaves the
--                               commit nothing to fold again.
--
--              An epoch above its lane's frontier is not sealed: closure folds it when it closes, and a touch of it is
--              never recorded. A touched epoch the frontier fell below before commit is left to closure the same way.
--
--              Cost: an insert statement pays one transition-table scan joined to the frontiers (one row per lane); an
--              outbox emit's rows have no position and leave the scan at once. A received event positioned in an open
--              epoch, the ordinary case, records nothing. Only a write into a sealed epoch writes a touch row, takes a
--              lock and folds that epoch, once per transaction.
--
--              Number 592: reserved for this change; 593 heals the epochs that drifted before it.
--
-- Reversal: reversible (the triggers, the touch table and the helpers go; _wh_fold_digest_epoch returns to 408's
--           definition; folds already refolded stay, which is correct)
-- Dependencies: 092 (wh_digest_epochs, wh_digest_epoch_frontiers), 408 (_wh_fold_digest_epoch, _wh_note_origin_rewrite,
--               _wh_bump_origin_generation), 461 (_wh_epoch_buckets, the slot a superseding row counts in)
-- Objects: wh_sealed_epoch_touches, _wh_digest_epoch_lock_key, _wh_touch_sealed_epochs, _wh_fold_digest_epoch,
--          _wh_refold_epoch_if_drifted, _wh_refold_touched_epochs, _wh_refold_touched_epochs_at_commit,
--          _wh_touch_sealed_epochs_on_insert, _wh_touch_sealed_epochs_on_update,
--          wh_event_store_touches_sealed_epochs_insert_trg, wh_event_store_touches_sealed_epochs_update_trg,
--          wh_sealed_epoch_touches_refold_trg
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_sealed_epoch_touches (
  txid              BIGINT NOT NULL,   -- the writing transaction (txid_current()); a row never outlives it
  origin_service_id UUID   NOT NULL,   -- the lane; zero-uuid = the local lane
  epoch_id          BIGINT NOT NULL,
  PRIMARY KEY (txid, origin_service_id, epoch_id)
);

COMMENT ON TABLE __SCHEMA__.wh_sealed_epoch_touches IS
'592 (#2572): the sealed epochs a transaction''s writes changed, one row per (transaction, lane, epoch), recorded by the event store''s triggers and refolded and removed at that transaction''s commit. Empty outside a writing transaction.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The advisory lock of one epoch, so every refold of it is serialized. One 64-bit key per (lane, epoch).
SELECT __SCHEMA__.drop_all_overloads('_wh_digest_epoch_lock_key');
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ARefoldAtCommit_HoldsTheEpochsLockUntilItsTransactionEndsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_digest_epoch_lock_key(p_lane UUID, p_epoch BIGINT) RETURNS BIGINT
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $$
  SELECT hashtextextended('wh_digest_epoch:' || p_lane::text || ':' || p_epoch::text, 0);
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_digest_epoch_lock_key(UUID, BIGINT) IS
'Internal (#2572): the advisory lock key of one digest epoch of one lane, taken by every refold of a touched or healed epoch.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Records, for this transaction, the sealed epochs the given (lane, position) pairs fall in. A NULL or negative
-- position, a lane with no frontier and an epoch above the frontier record nothing.
SELECT __SCHEMA__.drop_all_overloads('_wh_touch_sealed_epochs');
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnExplicitRefoldAfterTheChange_LeavesNothingForTheCommitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnIneligibleEvent_IntoASealedEpoch_IsNotRecordedAsATouchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_touch_sealed_epochs(p_lanes UUID[], p_positions BIGINT[]) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_sealed_epoch_touches (txid, origin_service_id, epoch_id)
  SELECT DISTINCT txid_current(), m.lane, m.pos / f.epoch_width
  FROM unnest(p_lanes, p_positions) AS m(lane, pos)
  JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = m.lane
  WHERE m.pos IS NOT NULL AND m.pos >= 0
    AND m.pos / f.epoch_width <= f.closed_through_epoch
  ORDER BY 2, 3
  ON CONFLICT DO NOTHING;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_touch_sealed_epochs(UUID[], BIGINT[]) IS
'Internal (#2572): records, for this transaction, the sealed epochs the (lane, position) pairs fall in, for the commit to refold.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- 408's definition whole, plus the touch: an epoch this transaction folds is no longer waiting for its commit refold.
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnExplicitRefoldAfterTheChange_LeavesNothingForTheCommitAsync</tests>
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

  -- 592 (#2572): folded now, after every write of this transaction so far.
  DELETE FROM __SCHEMA__.wh_sealed_epoch_touches t
  WHERE t.txid = txid_current() AND t.origin_service_id = p_lane AND t.epoch_id = p_epoch;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_digest_epoch IS
'Internal: (re)writes one epoch''s bucket rows from the canonical fold (_wh_epoch_buckets). 408 (#2262): refolding a sealed epoch of the local lane notes its first position as rewritten. 592 (#2572): clears this transaction''s touch of the epoch, so its commit does not fold it again.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Refolds one epoch only if its rows differ from a fresh fold, under the epoch's lock. The lock is its own statement,
-- so the compare that follows reads a snapshot taken after any earlier holder committed. Returns whether it refolded.
SELECT __SCHEMA__.drop_all_overloads('_wh_refold_epoch_if_drifted');
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ARefoldAtCommit_HoldsTheEpochsLockUntilItsTransactionEndsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AChangeThatLeavesTheFoldAsItWas_RewritesNothing_AndBumpsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:TwoTransactions_StoringIntoOneSealedEpoch_LeaveItsFoldCountingBothAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refold_epoch_if_drifted(
  p_lane  UUID,
  p_epoch BIGINT,
  p_width BIGINT,
  p_now   TIMESTAMPTZ
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_stored TEXT[];
  v_fresh  TEXT[];
BEGIN
  PERFORM pg_advisory_xact_lock(__SCHEMA__._wh_digest_epoch_lock_key(p_lane, p_epoch));

  SELECT array_agg((de.scope_tenant, de.event_type, de.digest_lo, de.digest_hi, de.event_count)::text
                   ORDER BY de.scope_tenant, de.event_type)
  INTO v_stored
  FROM __SCHEMA__.wh_digest_epochs de
  WHERE de.origin_service_id = p_lane AND de.epoch_id = p_epoch;

  SELECT array_agg((b.b_tenant, b.b_type, b.b_lo, b.b_hi, b.b_cnt)::text ORDER BY b.b_tenant, b.b_type)
  INTO v_fresh
  FROM __SCHEMA__._wh_epoch_buckets(p_lane, p_epoch, p_width) b;

  IF v_stored IS NOT DISTINCT FROM v_fresh THEN
    RETURN FALSE;
  END IF;
  PERFORM __SCHEMA__._wh_fold_digest_epoch(p_lane, p_epoch, p_width, p_now);
  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_refold_epoch_if_drifted(UUID, BIGINT, BIGINT, TIMESTAMPTZ) IS
'Internal (#2572): under the epoch''s advisory lock, refolds one epoch only when its stored rows differ from the canonical fold; returns whether it did.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Refolds every epoch this transaction touched and that is still sealed, each once, in (lane, epoch) order, and bumps
-- the generation once when a local epoch was rewritten. Returns the epochs rewritten.
SELECT __SCHEMA__.drop_all_overloads('_wh_refold_touched_epochs');
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ADirectInsert_IntoASealedEpochOfAReceivedLane_RefoldsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnEventMovedIntoASealedLocalEpoch_RefoldsIt_AndAdvancesTheOriginGenerationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ATouchedEpochTheFrontierFellBelow_IsLeftForClosureAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnExplicitRefoldAfterTheChange_LeavesNothingForTheCommitAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refold_touched_epochs() RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_lanes  UUID[];
  v_epochs BIGINT[];
  v_width  BIGINT;
  v_through BIGINT;
  v_i      INTEGER;
  v_refolded INTEGER := 0;
  v_local  BOOLEAN := FALSE;
BEGIN
  WITH gone AS (
    DELETE FROM __SCHEMA__.wh_sealed_epoch_touches t
    WHERE t.txid = txid_current()
    RETURNING t.origin_service_id, t.epoch_id
  )
  SELECT array_agg(g.origin_service_id ORDER BY g.origin_service_id, g.epoch_id),
         array_agg(g.epoch_id ORDER BY g.origin_service_id, g.epoch_id)
  INTO v_lanes, v_epochs
  FROM gone g;

  IF v_lanes IS NULL THEN
    RETURN 0;
  END IF;

  FOR v_i IN 1 .. cardinality(v_lanes) LOOP
    SELECT f.epoch_width, f.closed_through_epoch INTO v_width, v_through
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
    WHERE f.origin_service_id = v_lanes[v_i];
    CONTINUE WHEN v_width IS NULL OR v_epochs[v_i] > v_through;

    IF __SCHEMA__._wh_refold_epoch_if_drifted(v_lanes[v_i], v_epochs[v_i], v_width, NOW()) THEN
      v_refolded := v_refolded + 1;
      v_local := v_local OR v_lanes[v_i] = __EMPTY_UUID__::uuid;
    END IF;
  END LOOP;

  IF v_local THEN
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;
  RETURN v_refolded;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_refold_touched_epochs() IS
'Internal (#2572): refolds, once each and in (lane, epoch) order, the still-sealed epochs this transaction''s writes touched whose folds no longer match the store, and bumps the origin generation once when a local epoch was rewritten. Runs at commit; returns the epochs rewritten.';

-- The commit's refold. Fires once per touch row; the first firing folds them all and the rest find none.
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ADirectInsert_IntoASealedEpochOfAReceivedLane_RefoldsItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refold_touched_epochs_at_commit() RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  PERFORM __SCHEMA__._wh_refold_touched_epochs();
  RETURN NULL;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_refold_touched_epochs_at_commit() IS
'Internal (#2572): the deferred trigger on wh_sealed_epoch_touches that refolds the transaction''s touched sealed epochs at commit.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The touches of an insert statement, from its transition table: each eligible row's own position, and the slot a
-- superseding row inherited. Rows without a position (an outbox emit's, a received event held unpositioned) touch
-- nothing.
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ABackfilledEvent_StoredByTheInboxChainInsideASealedEpoch_RefoldsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:ADirectInsert_IntoASealedEpochOfAReceivedLane_RefoldsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnIneligibleEvent_IntoASealedEpoch_IsNotRecordedAsATouchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_insert() RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  PERFORM __SCHEMA__._wh_touch_sealed_epochs(array_agg(x.lane), array_agg(x.pos))
  FROM (
    SELECT COALESCE(n.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           CASE WHEN n.origin_service_id IS NULL THEN n.commit_sequence ELSE n.origin_commit_sequence END AS pos
    FROM new_rows n
    WHERE __DIGEST_ELIGIBLE__(n)
    UNION ALL
    SELECT COALESCE(n.origin_service_id, __EMPTY_UUID__::uuid), n.supersede_slot_at
    FROM new_rows n
    WHERE n.supersede_slot_at IS NOT NULL AND __DIGEST_ELIGIBLE__(n)
  ) x
  WHERE x.pos IS NOT NULL
  HAVING count(*) > 0;
  RETURN NULL;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_insert() IS
'Internal (#2572): the statement trigger that records the sealed epochs an insert into wh_event_store changed.';

-- The touches of a row whose origin or origin position changed: the epoch it left and the epoch it joined.
-- <docs>resilience/stream-integrity#what-keeps-a-seal-true</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AReattribution_MovingAPositionedEventBetweenLanes_RefoldsTheSealedEpochsOfBothAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:APositionWrittenByAnUpdate_IntoASealedEpoch_RefoldsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:AnEventMovedIntoASealedLocalEpoch_RefoldsIt_AndAdvancesTheOriginGenerationAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_update() RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  PERFORM __SCHEMA__._wh_touch_sealed_epochs(
    ARRAY[COALESCE(OLD.origin_service_id, __EMPTY_UUID__::uuid), COALESCE(OLD.origin_service_id, __EMPTY_UUID__::uuid),
          COALESCE(NEW.origin_service_id, __EMPTY_UUID__::uuid), COALESCE(NEW.origin_service_id, __EMPTY_UUID__::uuid)],
    ARRAY[CASE WHEN __DIGEST_ELIGIBLE__(OLD) THEN
            CASE WHEN OLD.origin_service_id IS NULL THEN OLD.commit_sequence ELSE OLD.origin_commit_sequence END END,
          CASE WHEN __DIGEST_ELIGIBLE__(OLD) THEN OLD.supersede_slot_at END,
          CASE WHEN __DIGEST_ELIGIBLE__(NEW) THEN
            CASE WHEN NEW.origin_service_id IS NULL THEN NEW.commit_sequence ELSE NEW.origin_commit_sequence END END,
          CASE WHEN __DIGEST_ELIGIBLE__(NEW) THEN NEW.supersede_slot_at END]);
  RETURN NULL;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_update() IS
'Internal (#2572): the row trigger that records the sealed epochs a change of an event''s origin or origin position left and joined.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The triggers. An update fires only when the origin or its position actually changed.
DROP TRIGGER IF EXISTS wh_event_store_touches_sealed_epochs_insert_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_touches_sealed_epochs_insert_trg
  AFTER INSERT ON __SCHEMA__.wh_event_store
  REFERENCING NEW TABLE AS new_rows
  FOR EACH STATEMENT
  EXECUTE FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_insert();

DROP TRIGGER IF EXISTS wh_event_store_touches_sealed_epochs_update_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_touches_sealed_epochs_update_trg
  AFTER UPDATE OF origin_service_id, origin_commit_sequence ON __SCHEMA__.wh_event_store
  FOR EACH ROW
  WHEN (OLD.origin_service_id IS DISTINCT FROM NEW.origin_service_id
        OR OLD.origin_commit_sequence IS DISTINCT FROM NEW.origin_commit_sequence)
  EXECUTE FUNCTION __SCHEMA__._wh_touch_sealed_epochs_on_update();

DROP TRIGGER IF EXISTS wh_sealed_epoch_touches_refold_trg ON __SCHEMA__.wh_sealed_epoch_touches;
CREATE CONSTRAINT TRIGGER wh_sealed_epoch_touches_refold_trg
  AFTER INSERT ON __SCHEMA__.wh_sealed_epoch_touches
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW
  EXECUTE FUNCTION __SCHEMA__._wh_refold_touched_epochs_at_commit();
