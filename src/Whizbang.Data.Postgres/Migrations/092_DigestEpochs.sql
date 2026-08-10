-- 092_DigestEpochs.sql
--
-- Epoch substrate for bounded integrity reconciliation (docs: resilience/stream-integrity;
-- proposal: bounded-integrity-reconciliation).
--
-- Epochs partition each origin lane's commit-sequence space into fixed-width windows and store one
-- immutable XOR fold per (origin, tenant, type, epoch) bucket. Because the digest algebra is
-- self-inverse and order-independent, any sequence range composes by XOR of epoch folds — which is
-- what lets reconciliation stop re-reading history: a manifest answer becomes a read of immutable
-- rows instead of a bit_xor aggregation over every stream row, and verified history is never
-- re-examined on the hot path.
--
-- DESIGN POINT — epochs are derived at CLOSURE time by a range recompute, not maintained on the
-- write path. The emit chain is untouched by this migration. Two reasons:
--   1. The local lane's commit_sequence is stamped ASYNCHRONOUSLY (~1ms after emit), so epoch
--      assignment at emit time is impossible for it.
--   2. The emit chain is the hottest path in the system; closure runs on the maintenance cadence
--      where an O(events-in-epoch) recompute is cheap and bounded.
--
-- CLOSURE SAFETY — an epoch is closable only when the lane's settled maximum sequence lies beyond
-- it AND no unsettled event sits inside its range. The second guard exists because a RECEIVED lane
-- can get a fresh-arrived event with an OLD origin sequence (redelivery); a settled-max frontier
-- alone would close over it and the fold would be stale from birth. The frontier is contiguous, so
-- closure stalls at a blocked epoch until the arrival settles.
--
-- LATE REPAIRS — a backfill can deliver events into an already-closed epoch's range. The repair
-- path calls refold_digest_epochs for the affected range; the scheduled self-sweep is the backstop
-- for anything that skips that call.
--
-- WIDTH PINNING — epoch identity is floor(seq / width): changing the width remaps every boundary
-- and makes existing folds meaningless. The width is read from wh_settings at a lane's FIRST
-- close and pinned on the lane's frontier row; later setting changes do not move existing lanes.
--
-- Fold predicates mirror ComputeStreamDigestsAsync / the 087 emit-chain fold EXACTLY:
-- ephemeral-born events (flags & 8) and at-most-once occurrences never enter the fold — which is
-- also why the ephemeral reaper and the tier-2 pointer prune are NOT seal-invalidation sites.
--
-- Dependencies: 087 (wh_stream_digests conventions), 028 (wh_settings)

INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
VALUES ('integrity_epoch_width', '100000', 'integer',
        'Sequence width of one digest epoch. Read at a lane''s first close and pinned on its frontier row; changing it later does not move existing lanes.')
ON CONFLICT (setting_key) DO NOTHING;

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_digest_epochs (
  origin_service_id UUID        NOT NULL,   -- zero-uuid = the local lane (wh_stream_digests convention)
  scope_tenant      TEXT        NOT NULL DEFAULT '',
  event_type        TEXT        NOT NULL,
  epoch_id          BIGINT      NOT NULL,
  digest_lo         BIGINT      NOT NULL,
  digest_hi         BIGINT      NOT NULL,
  event_count       INTEGER     NOT NULL,
  closed_at         TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (origin_service_id, scope_tenant, event_type, epoch_id)
);

COMMENT ON TABLE __SCHEMA__.wh_digest_epochs IS
'Immutable per-epoch XOR folds per (origin lane, tenant, type). Epoch N covers lane sequences [N*width, (N+1)*width). Rows change only via refold_digest_epochs (repair) or a generation rebase. Absence of a row = empty bucket in that epoch (XOR identity).';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_digest_epoch_frontiers (
  origin_service_id    UUID        PRIMARY KEY,
  closed_through_epoch BIGINT      NOT NULL,   -- -1 = nothing closed yet
  epoch_width          BIGINT      NOT NULL,
  updated_at           TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_digest_epoch_frontiers IS
'Per-lane contiguous closure frontier + the pinned epoch width. Every epoch at or below the frontier is closed; bucket rows exist only where the epoch was non-empty.';

-- Folds ONE epoch for ONE lane: delete-then-insert, so it serves both first close and refold.
-- The unsettled-in-range guard lives in the CALLERS (close checks it, refold deliberately does
-- not — a repair refold must fold what is there now).
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fold_digest_epoch(
  p_lane      UUID,
  p_epoch     BIGINT,
  p_width     BIGINT,
  p_closed_at TIMESTAMPTZ
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
  v_from  BIGINT := p_epoch * p_width;
  v_to    BIGINT := (p_epoch + 1) * p_width;   -- exclusive
BEGIN
  DELETE FROM __SCHEMA__.wh_digest_epochs
  WHERE origin_service_id = p_lane AND epoch_id = p_epoch;

  IF p_lane = c_zero THEN
    INSERT INTO __SCHEMA__.wh_digest_epochs
      (origin_service_id, scope_tenant, event_type, epoch_id, digest_lo, digest_hi, event_count, closed_at)
    SELECT c_zero, COALESCE(es.scope ->> 't', ''), es.event_type, p_epoch,
           bit_xor(hashtextextended(es.event_id::text, 0)),
           bit_xor(hashtextextended(es.event_id::text, 1)),
           COUNT(*)::int, p_closed_at
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.origin_service_id IS NULL
      AND es.commit_sequence >= v_from AND es.commit_sequence < v_to
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 2, 3;
  ELSE
    INSERT INTO __SCHEMA__.wh_digest_epochs
      (origin_service_id, scope_tenant, event_type, epoch_id, digest_lo, digest_hi, event_count, closed_at)
    SELECT p_lane, COALESCE(es.scope ->> 't', ''), es.event_type, p_epoch,
           bit_xor(hashtextextended(es.event_id::text, 0)),
           bit_xor(hashtextextended(es.event_id::text, 1)),
           COUNT(*)::int, p_closed_at
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.origin_service_id = p_lane
      AND es.origin_commit_sequence >= v_from AND es.origin_commit_sequence < v_to
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 2, 3;
  END IF;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_digest_epoch IS
'Internal: (re)computes one epoch''s bucket folds for one lane from the event store. Predicates mirror the 087 emit-chain fold exactly — ephemeral (flags&8) and at-most-once excluded.';

-- Advances each lane's contiguous frontier, folding every closable epoch. Returns epochs closed
-- across all lanes (empty epochs advance the frontier and count, but write no rows).
CREATE OR REPLACE FUNCTION __SCHEMA__.close_digest_epochs(
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER,
  p_max_epochs     INTEGER
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
  v_default_width BIGINT;
  v_total   INTEGER := 0;
  v_lane    UUID;
  v_width   BIGINT;
  v_frontier BIGINT;
  v_settled_max BIGINT;
  v_target  BIGINT;
  v_epoch   BIGINT;
  v_blocked BOOLEAN;
BEGIN
  SELECT setting_value::bigint INTO v_default_width
  FROM wh_settings WHERE setting_key = 'integrity_epoch_width';
  v_default_width := COALESCE(v_default_width, 100000);

  FOR v_lane IN
    SELECT DISTINCT COALESCE(es.origin_service_id, c_zero)
    FROM __SCHEMA__.wh_event_store es
  LOOP
    -- Pin the width on first contact with this lane.
    INSERT INTO __SCHEMA__.wh_digest_epoch_frontiers
      (origin_service_id, closed_through_epoch, epoch_width, updated_at)
    VALUES (v_lane, -1, v_default_width, p_now)
    ON CONFLICT (origin_service_id) DO NOTHING;

    SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
    WHERE f.origin_service_id = v_lane;

    -- The lane's settled maximum. The epoch containing it stays open: the lane keeps appending
    -- into it, so only strictly lower epochs are closure candidates.
    IF v_lane = c_zero THEN
      SELECT MAX(es.commit_sequence) INTO v_settled_max
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id IS NULL
        AND es.commit_sequence IS NOT NULL
        AND es.created_at < p_now - make_interval(secs => p_settle_seconds);
    ELSE
      SELECT MAX(es.origin_commit_sequence) INTO v_settled_max
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id = v_lane
        AND es.origin_commit_sequence IS NOT NULL
        AND es.created_at < p_now - make_interval(secs => p_settle_seconds);
    END IF;

    CONTINUE WHEN v_settled_max IS NULL;
    v_target := (v_settled_max / v_width) - 1;

    v_epoch := v_frontier + 1;
    WHILE v_epoch <= v_target AND v_total < p_max_epochs LOOP
      -- The redelivery guard: any UNSETTLED event inside the range means the fold would be
      -- incomplete. The frontier is contiguous, so this lane stops here for now.
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
      EXIT WHEN v_blocked;

      PERFORM __SCHEMA__._wh_fold_digest_epoch(v_lane, v_epoch, v_width, p_now);

      UPDATE __SCHEMA__.wh_digest_epoch_frontiers f
      SET closed_through_epoch = v_epoch, updated_at = p_now
      WHERE f.origin_service_id = v_lane;

      v_total := v_total + 1;
      v_epoch := v_epoch + 1;
    END LOOP;
  END LOOP;

  RETURN v_total;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.close_digest_epochs IS
'Advances each lane''s contiguous epoch frontier, folding closable epochs from the event store. An epoch closes only when the settled max lies beyond it AND no unsettled event sits in its range (redelivery can land a fresh event with an old origin sequence). Runs on the maintenance cadence; the emit chain is untouched.';

-- Recomputes already-closed epochs after a repair back-fills their range. Clamped to the frontier:
-- an epoch that never closed has no stale fold to fix and will be folded by close when eligible.
CREATE OR REPLACE FUNCTION __SCHEMA__.refold_digest_epochs(
  p_lane UUID,
  p_from BIGINT,
  p_to   BIGINT
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_width    BIGINT;
  v_frontier BIGINT;
  v_epoch    BIGINT;
  v_count    INTEGER := 0;
BEGIN
  SELECT f.epoch_width, f.closed_through_epoch INTO v_width, v_frontier
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = p_lane;

  IF v_width IS NULL THEN
    RETURN 0;   -- lane never closed anything; nothing stale exists
  END IF;

  v_epoch := GREATEST(p_from, 0);
  WHILE v_epoch <= LEAST(p_to, v_frontier) LOOP
    PERFORM __SCHEMA__._wh_fold_digest_epoch(p_lane, v_epoch, v_width, NOW());
    v_count := v_count + 1;
    v_epoch := v_epoch + 1;
  END LOOP;

  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.refold_digest_epochs IS
'Recomputes closed epochs in [p_from, p_to] (clamped to the lane''s frontier) after a repair delivered events into their range. The scheduled self-sweep backstops any missed call.';
