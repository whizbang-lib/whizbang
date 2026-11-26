-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 593_DriftedReceivedEpochsAreHealedOnce.sql
-- Date: 2026-11-24
-- Description: Sealed epochs of received lanes that drifted before 592 are re-checked once, in bounded batches, by the
--              maintenance closure pass, and refolded where they no longer match the store (#2572).
--
--              Before 592 an event stored into a sealed epoch of a received lane (a backfill or redelivery answer
--              arriving after the epoch was sealed) left the epoch's fold short of what the store held. 592 stops new
--              drift; this heals what a database already holds, with nothing for an operator to turn on:
--                the queue      _wh_queue_received_epoch_heal, run once by this migration (gated on the
--                               integrity_epoch_heal_queued setting, so a re-run queues nothing), puts every sealed epoch
--                               of every received lane in wh_digest_epoch_heal_queue: one row per epoch, empty epochs
--                               included (a late event may sit in an epoch that sealed empty). Bounded by epochs, never by
--                               events.
--                the drain      wh_heal_queued_digest_epochs takes the lowest epochs first (a seal advances only as far as
--                               its first drifted epoch, so healing low epochs first frees seals soonest), at most p_max
--                               per call, and refolds each through 592's _wh_refold_epoch_if_drifted: under the epoch's
--                               lock, rewritten only when its fold differs from the store. An epoch its lane no longer
--                               seals is dropped. Refolds are counted in integrity_epoch_heal_refolded.
--                the pass       close_digest_epochs (193's definition whole) drains integrity_epoch_heal_per_cycle
--                               epochs (default 8) after closing, on every maintenance cycle that seals. Its return value
--                               is still the epochs it closed.
--
--              Why not the seal re-check: it is off by default, it reaches only epochs sealed within its lookback (30 days
--              by default, and the drift sits in the oldest epochs), and it skips an epoch holding a recent arrival. Why
--              not a refold at startup: the cost is one fold per sealed received epoch, which grows with history and has
--              no place in a migration. Why received lanes only: every path that rewrites the local lane refolds what it
--              touched and moves the origin generation, so its consumers re-verify; a local refold here would rewrite
--              what consumers already verified with no fault to show for it. The seal re-check stays the backstop there.
--
--              Cost: per maintenance cycle, at most integrity_epoch_heal_per_cycle epoch folds (each reads one epoch of
--              one lane by index) and one read of the queue's first rows by primary key; nothing once it is drained.
--
--              Number 593: reserved for this change, after 592.
--
-- Reversal: reversible (the queue, its functions and settings go; close_digest_epochs returns to its 193 definition;
--           epochs already healed stay healed, which is correct)
-- Dependencies: 193 (close_digest_epochs), 592 (_wh_refold_epoch_if_drifted), 092 (wh_digest_epoch_frontiers)
-- Objects: wh_digest_epoch_heal_queue, _wh_queue_received_epoch_heal, wh_heal_queued_digest_epochs, close_digest_epochs
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_digest_epoch_heal_queue (
  epoch_id          BIGINT      NOT NULL,
  origin_service_id UUID        NOT NULL,
  queued_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (epoch_id, origin_service_id)   -- lowest epochs first
);

COMMENT ON TABLE __SCHEMA__.wh_digest_epoch_heal_queue IS
'593 (#2572): sealed epochs of received lanes waiting for their one-off re-check after the fix that keeps seals true (592). Drained, lowest epochs first, by close_digest_epochs; empty once healed.';

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
VALUES ('integrity_epoch_heal_per_cycle', '8', 'integer',
        '593 (#2572): sealed received epochs re-checked per maintenance closure pass until the one-off heal queue is drained.')
ON CONFLICT (setting_key) DO NOTHING;

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Queues every sealed epoch of every received lane, once per database. Returns the epochs queued.
SELECT __SCHEMA__.drop_all_overloads('_wh_queue_received_epoch_heal');
-- <docs>resilience/stream-integrity#healing-epochs-that-drifted</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:TheUpgradeHeal_QueuesEverySealedReceivedEpochOnce_AndNoLocalOneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_queue_received_epoch_heal() RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_queued INTEGER;
BEGIN
  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
  VALUES ('integrity_epoch_heal_queued', 'true', 'boolean',
          '593 (#2572): the one-off re-check of sealed received epochs has been queued.')
  ON CONFLICT (setting_key) DO NOTHING;
  IF NOT FOUND THEN
    RETURN 0;
  END IF;

  WITH queued AS (
    INSERT INTO __SCHEMA__.wh_digest_epoch_heal_queue (epoch_id, origin_service_id)
    SELECT e.epoch_id, f.origin_service_id
    FROM __SCHEMA__.wh_digest_epoch_frontiers f
    CROSS JOIN LATERAL generate_series(0::bigint, f.closed_through_epoch) AS e(epoch_id)
    WHERE f.origin_service_id <> __EMPTY_UUID__::uuid
    ON CONFLICT DO NOTHING
    RETURNING 1
  )
  SELECT count(*)::int INTO v_queued FROM queued;
  RETURN v_queued;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_queue_received_epoch_heal() IS
'Internal (#2572): queues every sealed epoch of every received lane for one re-check, once per database (the integrity_epoch_heal_queued setting). Returns the epochs queued.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Re-checks at most p_max queued epochs, lowest first, refolding those that drifted. Returns what it checked and
-- refolded.
SELECT __SCHEMA__.drop_all_overloads('wh_heal_queued_digest_epochs');
-- <docs>resilience/stream-integrity#healing-epochs-that-drifted</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:TheHeal_RefoldsADriftedEpoch_LeavesAnUndriftedOne_AndDrainsTheQueueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:TheHeal_TakesTheLowestEpochsFirst_WithinItsBudgetAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:TheHeal_DropsAQueuedEpochItsLaneNoLongerSealsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_heal_queued_digest_epochs(p_now TIMESTAMPTZ, p_max INTEGER)
RETURNS TABLE(epochs_checked INTEGER, epochs_refolded INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  r RECORD;
  v_checked INTEGER := 0;
  v_refolded INTEGER := 0;
BEGIN
  FOR r IN
    WITH taken AS (
      SELECT q.epoch_id, q.origin_service_id
      FROM __SCHEMA__.wh_digest_epoch_heal_queue q
      ORDER BY q.epoch_id, q.origin_service_id
      LIMIT GREATEST(p_max, 0)
      FOR UPDATE SKIP LOCKED
    ),
    gone AS (
      DELETE FROM __SCHEMA__.wh_digest_epoch_heal_queue q
      USING taken t
      WHERE q.epoch_id = t.epoch_id AND q.origin_service_id = t.origin_service_id
      RETURNING q.epoch_id, q.origin_service_id
    )
    SELECT g.origin_service_id AS lane, g.epoch_id AS epoch, f.epoch_width AS width
    FROM gone g
    JOIN __SCHEMA__.wh_digest_epoch_frontiers f ON f.origin_service_id = g.origin_service_id
    WHERE g.epoch_id <= f.closed_through_epoch
    ORDER BY g.origin_service_id, g.epoch_id
  LOOP
    v_checked := v_checked + 1;
    IF __SCHEMA__._wh_refold_epoch_if_drifted(r.lane, r.epoch, r.width, p_now) THEN
      v_refolded := v_refolded + 1;
    END IF;
  END LOOP;

  IF v_refolded > 0 THEN
    INSERT INTO __SCHEMA__.wh_settings AS s (setting_key, setting_value, value_type, description)
    VALUES ('integrity_epoch_heal_refolded', v_refolded::text, 'integer',
            '593 (#2572): sealed received epochs the one-off re-check found drifted and refolded.')
    ON CONFLICT (setting_key) DO UPDATE SET setting_value = ((s.setting_value)::bigint + v_refolded)::text;
  END IF;

  RETURN QUERY SELECT v_checked, v_refolded;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_heal_queued_digest_epochs(TIMESTAMPTZ, INTEGER) IS
'593 (#2572): re-checks at most p_max queued sealed received epochs, lowest first, refolding each whose fold differs from the store (under its lock); drops an epoch its lane no longer seals. Returns the epochs checked and refolded.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- 193's definition whole, plus the heal: after closing, the next integrity_epoch_heal_per_cycle queued epochs.
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_NothingLeftToClose_CostFollowsTheLanesNotTheStoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_FindsTheLocalLaneAndEveryReceivedLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_ReceivedLaneWithoutDigestBuckets_IsStillALaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_ARegisteredLane_IsVisitedFromItsFrontierAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochLaneDiscoverySqlTests.cs:CloseDigestEpochs_LocalEventsWithoutABucket_BecomeALaneOnceFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochSqlTests.cs:CloseDigestEpochs_LedgerOnlyOrigin_GetsAFrontierRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochSqlTests.cs:CloseDigestEpochs_StarvedLane_GoesFirstUnderTheBudgetAsync</tests>
-- <docs>resilience/stream-integrity#healing-epochs-that-drifted</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealedEpochLateWriteSqlTests.cs:Closing_HealsTheQueue_WithTheConfiguredBudget_BesideTheEpochsItClosesAsync</tests>
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
  v_heal_per_cycle INTEGER;
BEGIN
  SELECT setting_value::bigint INTO v_default_width
  FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_epoch_width';
  v_default_width := COALESCE(v_default_width, 100000);

  -- #515: stall escape window (settings table, live-tunable). A lane whose frontier has not
  -- advanced for longer than this closes its blocked epoch anyway: the verify sweep refolds
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
      -- #515: a stalled lane forces its FIRST blocked epoch through (once per call: the
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

  -- 593 (#2572): the one-off re-check of sealed received epochs, a bounded batch per cycle until its queue is drained.
  -- Its own budget, so a closure backlog never starves it and it never takes the closures' share.
  SELECT setting_value::int INTO v_heal_per_cycle
  FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_epoch_heal_per_cycle';
  PERFORM 1 FROM __SCHEMA__.wh_heal_queued_digest_epochs(p_now, COALESCE(v_heal_per_cycle, 8));

  RETURN v_total;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.close_digest_epochs(TIMESTAMPTZ, INTEGER, INTEGER) IS
'Advances each lane''s contiguous epoch frontier, folding closable epochs from the event store. An epoch closes only when the settled max lies beyond it AND no unsettled event sits in its range (redelivery can land a fresh event with an old origin sequence). Runs on the maintenance cadence; the emit chain is untouched. 193: lanes are found from the frontiers, the digest buckets, the integrity ledger and the lane index, one index entry per lane, never by reading the event store row by row. 593 (#2572): then re-checks a bounded batch of queued sealed received epochs (integrity_epoch_heal_per_cycle), refolding those that drifted.';

-- One-off: queue the sealed received epochs this database already holds (a no-op on a re-run, and on a new database).
SELECT __SCHEMA__._wh_queue_received_epoch_heal();
