-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 593_DriftedReceivedEpochsAreHealedOnce.sql (#2572): the heal queue, its functions and settings go, and
-- close_digest_epochs returns to its 193 definition. Epochs already healed stay healed, which is correct.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_heal_queued_digest_epochs(TIMESTAMPTZ, INTEGER);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_queue_received_epoch_heal();
DROP TABLE IF EXISTS __SCHEMA__.wh_digest_epoch_heal_queue;
DELETE FROM __SCHEMA__.wh_settings
WHERE setting_key IN ('integrity_epoch_heal_per_cycle', 'integrity_epoch_heal_queued', 'integrity_epoch_heal_refolded');

-- 193's definition.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_NothingLeftToClose_CostFollowsTheLanesNotTheStoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_FindsTheLocalLaneAndEveryReceivedLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_ReceivedLaneWithoutDigestBuckets_IsStillALaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_ARegisteredLane_IsVisitedFromItsFrontierAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_LocalEventsWithoutABucket_BecomeALaneOnceFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochSqlTests.cs:CloseDigestEpochs_LedgerOnlyOrigin_GetsAFrontierRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochSqlTests.cs:CloseDigestEpochs_StarvedLane_GoesFirstUnderTheBudgetAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.close_digest_epochs(
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER,
  p_max_epochs     INTEGER
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__::uuid;
  v_default_width BIGINT;
  v_total   INTEGER := 0;
  v_lane    UUID;
  v_width   BIGINT;
  v_frontier BIGINT;
  v_settled_max BIGINT;
  v_target  BIGINT;
  v_epoch   BIGINT;
  v_blocked BOOLEAN;
  v_max_stall INTEGER;
  v_stalled BOOLEAN;
  v_frontier_updated TIMESTAMPTZ;
BEGIN
  SELECT setting_value::bigint INTO v_default_width
  FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_epoch_width';
  v_default_width := COALESCE(v_default_width, 100000);

  -- #515: stall escape window (settings table, live-tunable). A lane whose frontier has not
  -- advanced for longer than this closes its blocked epoch anyway — the verify sweep refolds
  -- on drift, so a late straggler is corrected by the existing backstop instead of pinning
  -- the lane forever. Non-positive disables the escape.
  SELECT setting_value::int INTO v_max_stall
  FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_epoch_max_stall_seconds';
  v_max_stall := COALESCE(v_max_stall, 3600);

  -- #515: an origin with ledger history but no local events is the LOSS signature, and it must
  -- exist as a lane for the verify machinery to flag rather than vanish from the audit. Ordering
  -- is least-recently-advanced first (never-seen lanes lead), so the global closure budget
  -- round-robins across lanes over cycles instead of letting scan order starve the tail.
  --
  -- 193: every source is walked one index entry per lane, never row by row (see the header). Each
  -- walk is the same shape: the smallest lane, then repeatedly the smallest lane above the last,
  -- until there is none.
  FOR v_lane IN
    WITH RECURSIVE
    store_lanes AS (
      (SELECT es.origin_service_id AS lane
       FROM __SCHEMA__.wh_event_store es
       WHERE es.origin_service_id IS NOT NULL
       ORDER BY es.origin_service_id
       LIMIT 1)
      UNION ALL
      SELECT (SELECT es.origin_service_id
              FROM __SCHEMA__.wh_event_store es
              WHERE es.origin_service_id > sl.lane
              ORDER BY es.origin_service_id
              LIMIT 1)
      FROM store_lanes sl
      WHERE sl.lane IS NOT NULL
    ),
    digest_lanes AS (
      (SELECT d.origin_service_id AS lane
       FROM __SCHEMA__.wh_stream_digests d
       ORDER BY d.origin_service_id
       LIMIT 1)
      UNION ALL
      SELECT (SELECT d.origin_service_id
              FROM __SCHEMA__.wh_stream_digests d
              WHERE d.origin_service_id > dl.lane
              ORDER BY d.origin_service_id
              LIMIT 1)
      FROM digest_lanes dl
      WHERE dl.lane IS NOT NULL
    ),
    ledger_lanes AS (
      (SELECT il.origin_service_id AS lane
       FROM __SCHEMA__.wh_integrity_ledger il
       ORDER BY il.origin_service_id
       LIMIT 1)
      UNION ALL
      SELECT (SELECT il.origin_service_id
              FROM __SCHEMA__.wh_integrity_ledger il
              WHERE il.origin_service_id > ll.lane
              ORDER BY il.origin_service_id
              LIMIT 1)
      FROM ledger_lanes ll
      WHERE ll.lane IS NOT NULL
    )
    SELECT lanes.lane
    FROM (
      SELECT sl.lane FROM store_lanes sl WHERE sl.lane IS NOT NULL
      UNION
      SELECT dl.lane FROM digest_lanes dl WHERE dl.lane IS NOT NULL
      UNION
      SELECT ll.lane FROM ledger_lanes ll WHERE ll.lane IS NOT NULL
      UNION
      SELECT fr.origin_service_id FROM __SCHEMA__.wh_digest_epoch_frontiers fr
    ) lanes
    LEFT JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = lanes.lane
    ORDER BY f.updated_at ASC NULLS FIRST
  LOOP
    -- Pin the width on first contact with this lane.
    INSERT INTO __SCHEMA__.wh_digest_epoch_frontiers
      (origin_service_id, closed_through_epoch, epoch_width, updated_at)
    VALUES (v_lane, -1, v_default_width, p_now)
    ON CONFLICT (origin_service_id) DO NOTHING;

    SELECT f.closed_through_epoch, f.epoch_width, f.updated_at INTO v_frontier, v_width, v_frontier_updated
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
    WHERE f.origin_service_id = v_lane;
    v_stalled := v_max_stall > 0
      AND v_frontier_updated < p_now - make_interval(secs => v_max_stall);

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
      -- #515: a stalled lane forces its FIRST blocked epoch through (once per call — the
      -- escape is a trickle, not an open gate); an unstalled blocked lane waits as before.
      EXIT WHEN v_blocked AND NOT v_stalled;
      IF v_blocked THEN
        v_stalled := FALSE;
      END IF;

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

COMMENT ON FUNCTION __SCHEMA__.close_digest_epochs(TIMESTAMPTZ, INTEGER, INTEGER) IS
'Advances each lane''s contiguous epoch frontier, folding closable epochs from the event store. An epoch closes only when the settled max lies beyond it AND no unsettled event sits in its range (redelivery can land a fresh event with an old origin sequence). Runs on the maintenance cadence; the emit chain is untouched. 193: lanes are found from the frontiers, the digest buckets, the integrity ledger and the lane index, one index entry per lane, never by reading the event store row by row.';
