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

-- The CANONICAL epoch fold: one epoch's bucket folds for one lane, computed from the event
-- store. Every consumer of "what should this epoch hold" — close, refold, verify — reads THIS
-- function, so the predicates (mirroring the 087 emit-chain fold: ephemeral flags&8 and
-- at-most-once excluded) live in exactly one place and cannot drift apart.
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_epoch_buckets(
  p_lane  UUID,
  p_epoch BIGINT,
  p_width BIGINT
) RETURNS TABLE (b_tenant TEXT, b_type TEXT, b_lo BIGINT, b_hi BIGINT, b_cnt INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
  v_from  BIGINT := p_epoch * p_width;
  v_to    BIGINT := (p_epoch + 1) * p_width;   -- exclusive
BEGIN
  IF p_lane = c_zero THEN
    RETURN QUERY
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type::text,
           bit_xor(hashtextextended(es.event_id::text, 0)),
           bit_xor(hashtextextended(es.event_id::text, 1)),
           COUNT(*)::int
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.origin_service_id IS NULL
      AND es.commit_sequence >= v_from AND es.commit_sequence < v_to
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 1, 2;
  ELSE
    RETURN QUERY
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type::text,
           bit_xor(hashtextextended(es.event_id::text, 0)),
           bit_xor(hashtextextended(es.event_id::text, 1)),
           COUNT(*)::int
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.origin_service_id = p_lane
      AND es.origin_commit_sequence >= v_from AND es.origin_commit_sequence < v_to
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 1, 2;
  END IF;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_epoch_buckets IS
'Internal: the canonical epoch fold — the single source of truth for what an epoch should hold. Predicates mirror the 087 emit-chain fold exactly.';

-- Folds ONE epoch for ONE lane: delete-then-insert, so it serves close, refold, and heal.
-- The unsettled-in-range guard lives in the CALLERS (close and verify check it; refold
-- deliberately does not — a repair refold must fold what is there now).
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fold_digest_epoch(
  p_lane      UUID,
  p_epoch     BIGINT,
  p_width     BIGINT,
  p_closed_at TIMESTAMPTZ
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_digest_epochs
  WHERE origin_service_id = p_lane AND epoch_id = p_epoch;

  INSERT INTO __SCHEMA__.wh_digest_epochs
    (origin_service_id, scope_tenant, event_type, epoch_id, digest_lo, digest_hi, event_count, closed_at)
  SELECT p_lane, b.b_tenant, b.b_type, p_epoch, b.b_lo, b.b_hi, b.b_cnt, p_closed_at
  FROM __SCHEMA__._wh_epoch_buckets(p_lane, p_epoch, p_width) b;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_digest_epoch IS
'Internal: (re)writes one epoch''s bucket rows from the canonical fold (_wh_epoch_buckets).';

-- #80-D: the sweep''s seal backstop. Manifest answers trust sealed epochs WITHOUT re-verifying
-- (that is the whole point of the epochs), so this is the ONE place a bad seal gets caught —
-- each closed epoch is recomputed from the store, compared bucket-for-bucket, and refolded on
-- drift. Epochs holding an UNSETTLED arrival (a fresh backfill into a closed range) are skipped
-- whole, exactly like closure: verifying now would fold an in-flight delivery into a seal; they
-- verify on a later sweep.
CREATE OR REPLACE FUNCTION __SCHEMA__.verify_digest_epochs(
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER,
  p_max_epochs     INTEGER
) RETURNS TABLE (epochs_checked INTEGER, epochs_drifted INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
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
'Sweep backstop: recomputes each closed epoch from the store, compares bucket-for-bucket, refolds on drift. Non-zero drift means an unaccounted write path — alarm-worthy, not routine. Epochs with unsettled arrivals are skipped whole.';

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

-- Serves type-level digest answers FROM the epochs: sealed history composes by XOR of epoch rows
-- and only the OPEN window (above the frontier) folds live — O(open window) instead of O(store).
-- Once sealed, an epoch row is AUTHORITATIVE here: answers do not re-verify it against the store
-- (that would re-buy the full-scan cost on every answer); the scheduled self-sweep owns detecting
-- a bad seal. With no frontier the whole lane is "open" and this degrades to the plain full fold.
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch(
  p_origin_lane    UUID,          -- NULL = the local lane (this service's own emissions)
  p_event_types    TEXT[],        -- NULL = all types
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
  v_lane      UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier  BIGINT := -1;
  v_width     BIGINT;
  v_open_from BIGINT := 0;
  v_types     TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_frontier := -1;      -- nothing sealed: the whole lane folds live below
    v_open_from := 0;
  ELSE
    v_open_from := (v_frontier + 1) * v_width;
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id <= v_frontier
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  open_fold AS (
    -- The live remainder. A row the stamper has not yet sequenced belongs here by definition —
    -- it can be in no epoch, so the NULL-sequence branch keeps it from falling through the crack.
    SELECT COALESCE(es.scope ->> 't', '') AS b_tenant, es.event_type AS b_type,
           bit_xor(hashtextextended(es.event_id::text, 0)) AS lo,
           bit_xor(hashtextextended(es.event_id::text, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
      AND (CASE WHEN p_origin_lane IS NULL
                THEN es.commit_sequence IS NULL OR es.commit_sequence >= v_open_from
                ELSE es.origin_commit_sequence IS NULL OR es.origin_commit_sequence >= v_open_from
           END)
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM open_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_type_digests_epoch IS
'Type-level digest answer served from sealed epochs + a live fold of only the open window. Sealed rows are authoritative (the self-sweep owns detecting a bad seal). Falls back to the plain full fold when the lane has no closed epochs.';

-- The lane's SETTLED maximum sequence — the watermark ceiling for negotiated-scope answers
-- (#80-B). An answer must never claim coverage of an unsettled sequence: redelivery or in-flight
-- stamping could still land events there, and an asker that sealed past it would alarm on them.
CREATE OR REPLACE FUNCTION __SCHEMA__.integrity_settled_max(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS BIGINT
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  v_max BIGINT;
BEGIN
  IF p_origin_lane IS NULL THEN
    SELECT MAX(es.commit_sequence) INTO v_max
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id IS NULL
      AND es.commit_sequence IS NOT NULL
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds);
  ELSE
    SELECT MAX(es.origin_commit_sequence) INTO v_max
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = p_origin_lane
      AND es.origin_commit_sequence IS NOT NULL
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds);
  END IF;
  RETURN v_max;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.integrity_settled_max IS
'Settled maximum sequence for a lane — the ceiling every negotiated-scope answer''s watermark is capped at.';

-- Negotiated-scope type-level fold (#80-B): digests for the half-open window [p_since, p_until).
-- Epochs FULLY inside the window contribute their sealed fold (authoritative, same rule as the
-- unwindowed path); PARTIALLY covered epochs fold live over just the covered fringe — a seal is
-- indivisible, it cannot answer for half its range. The caller clamps p_until at the settled max
-- BEFORE calling (integrity_settled_max), so this function is a pure window fold.
-- Rows without a sequence (stamper in flight) are not addressable by any window and are excluded;
-- they appear only in unwindowed answers.
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch_window(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_event_types    TEXT[],        -- NULL = all types
  p_since          BIGINT,        -- inclusive
  p_until          BIGINT,        -- exclusive, pre-clamped at the settled max
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := '00000000-0000-0000-0000-000000000000';
  v_lane     UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier BIGINT := -1;
  v_width    BIGINT;
  v_e_lo     BIGINT;
  v_e_hi     BIGINT;
  v_types    TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_e_lo := 0;
    v_e_hi := -1;   -- no sealed epochs: the whole window folds live
  ELSE
    -- Sealed epochs fully inside [p_since, p_until): first with start >= since, last whose
    -- exclusive end fits, both clamped to the frontier. An empty run (e_lo > e_hi) = all live.
    v_e_lo := (p_since + v_width - 1) / v_width;
    v_e_hi := LEAST(p_until / v_width - 1, v_frontier);
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id BETWEEN v_e_lo AND v_e_hi
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  lane AS (
    SELECT es.*,
           CASE WHEN p_origin_lane IS NULL THEN es.commit_sequence
                ELSE es.origin_commit_sequence END AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
  ),
  live_fold AS (
    -- The window minus the sealed run: the low fringe [since, e_lo*width) and the tail
    -- [(e_hi+1)*width, until) — or the whole window when no epoch fits (e_lo > e_hi).
    SELECT COALESCE(l.scope ->> 't', '') AS b_tenant, l.event_type AS b_type,
           bit_xor(hashtextextended(l.event_id::text, 0)) AS lo,
           bit_xor(hashtextextended(l.event_id::text, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM lane l
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = l.event_id
    WHERE l.lane_seq IS NOT NULL
      AND l.lane_seq >= p_since AND l.lane_seq < p_until
      AND (v_e_lo > v_e_hi
           OR l.lane_seq < v_e_lo * v_width
           OR l.lane_seq >= (v_e_hi + 1) * v_width)
      AND (v_types IS NULL OR l.event_type = ANY(v_types))
      AND COALESCE(l.flags, 0) & 8 = 0
      AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
      AND l.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM live_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_type_digests_epoch_window IS
'Windowed type-level fold [p_since, p_until): fully-contained sealed epochs answer from their seal, fringes fold live. Caller pre-clamps p_until at integrity_settled_max.';

-- The consumer's per-origin verified watermark (#80-C): every sequence below sealed_through
-- proved clean in a past complete-window audit. Steady-state audits start here — this row is
-- what stops verified history from being re-shipped and re-verified forever. Monotonic by the
-- GREATEST upsert; the trust-but-verify sweep is what catches a seal that should not have been.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_seals (
  origin_service_id UUID        PRIMARY KEY,
  sealed_through    BIGINT      NOT NULL,
  updated_at        TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_seals IS
'Per-origin verified watermark: the exclusive end of the highest window that audited clean and complete. The next windowed audit asks from here.';
