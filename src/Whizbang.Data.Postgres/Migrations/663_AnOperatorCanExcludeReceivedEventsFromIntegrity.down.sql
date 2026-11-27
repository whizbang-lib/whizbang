-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 663_AnOperatorCanExcludeReceivedEventsFromIntegrity.sql (#2588): every mark is reversed first (the
-- flag cleared and the events folded back, exactly as an operator's unexclude does, logged as such), then the tables and
-- functions go. After this no event carries EventFlags.IntegrityExcluded, so nothing the earlier rule counts differently
-- is left behind.

DO $$
DECLARE
  v_ids UUID[];
BEGIN
  IF to_regclass('__SCHEMA__.wh_integrity_exclusions') IS NOT NULL THEN
    LOOP
      SELECT array_agg(x.event_id) INTO v_ids
      FROM (SELECT e.event_id FROM __SCHEMA__.wh_integrity_exclusions e ORDER BY e.event_id LIMIT 1000) x;
      EXIT WHEN v_ids IS NULL;
      PERFORM __SCHEMA__.wh_integrity_unexclude(v_ids, 'migration 663 reversed', 'migration', NULL, now());
    END LOOP;
  END IF;
END $$;

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_roll_off');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_summary');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_apply_batch');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_dry_run');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_adjustment');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_ask');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_settle');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_select');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_unexclude');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclude');
SELECT __SCHEMA__.drop_all_overloads('_wh_integrity_exclusion_recount');
SELECT __SCHEMA__.drop_all_overloads('_wh_integrity_exclusion_candidates');
DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_exclusion_requests;
DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_exclusion_log;
DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_exclusion_origins;
DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_exclusions;

-- ── wh_origin_position_ask and wh_origin_positions_of: back to their definitions in 561 ──
SELECT __SCHEMA__.drop_all_overloads('wh_origin_position_ask');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_WalksOneOriginInBoundedBatches_AndRestartsTheLapOnlyAfterThePauseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_ServesTheOriginServedLeastRecently_AndLeavesOutUnknownAnswersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_OnALargeStore_ReadsOnlyItsBatchOfTheHeldIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_position_ask(
  p_batch_size INTEGER,
  p_lap_pause INTERVAL,
  p_now TIMESTAMPTZ
) RETURNS TABLE(origin_service_id UUID, event_ids UUID[], lap_ended BOOLEAN) AS $$
#variable_conflict use_column
DECLARE
  v_origin UUID;
  v_after_at TIMESTAMPTZ;
  v_after_id UUID;
  v_ids UUID[];
  v_read INTEGER;
  v_last_at TIMESTAMPTZ;
  v_last_id UUID;
BEGIN
  -- Every origin holding a row without its position, one index probe per origin (a skip scan of the held index).
  INSERT INTO __SCHEMA__.wh_origin_position_walk (origin_service_id)
  WITH RECURSIVE o(id) AS (
    (SELECT es.origin_service_id FROM __SCHEMA__.wh_event_store es
     WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL
     ORDER BY es.origin_service_id LIMIT 1)
    UNION ALL
    SELECT (SELECT es.origin_service_id FROM __SCHEMA__.wh_event_store es
            WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL AND es.origin_service_id > o.id
            ORDER BY es.origin_service_id LIMIT 1)
    FROM o WHERE o.id IS NOT NULL
  )
  SELECT o.id FROM o WHERE o.id IS NOT NULL
  ON CONFLICT (origin_service_id) DO NOTHING;

  SELECT w.origin_service_id, w.after_created_at, w.after_event_id INTO v_origin, v_after_at, v_after_id
  FROM __SCHEMA__.wh_origin_position_walk w
  WHERE w.next_lap_at IS NULL OR w.next_lap_at <= p_now
  ORDER BY w.updated_at ASC NULLS FIRST, w.origin_service_id
  LIMIT 1
  FOR UPDATE SKIP LOCKED;
  IF v_origin IS NULL THEN
    RETURN;
  END IF;

  SELECT COALESCE(array_agg(b.event_id ORDER BY b.created_at, b.event_id)
                    FILTER (WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_origin_position_unknown u WHERE u.event_id = b.event_id)),
                  ARRAY[]::UUID[]),
         count(*)::int,
         (array_agg(b.created_at ORDER BY b.created_at DESC, b.event_id DESC))[1],
         (array_agg(b.event_id ORDER BY b.created_at DESC, b.event_id DESC))[1]
    INTO v_ids, v_read, v_last_at, v_last_id
  FROM (
    SELECT es.event_id, es.created_at
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = v_origin
      AND es.origin_commit_sequence IS NULL
      AND (v_after_at IS NULL OR (es.created_at, es.event_id) > (v_after_at, v_after_id))
    ORDER BY es.created_at, es.event_id
    LIMIT GREATEST(p_batch_size, 1)
  ) b;

  IF v_read < GREATEST(p_batch_size, 1) THEN
    -- The lap reached the end: start over after the pause, so an event the origin has not stamped yet is asked again.
    UPDATE __SCHEMA__.wh_origin_position_walk w
    SET after_created_at = NULL, after_event_id = NULL, next_lap_at = p_now + p_lap_pause, updated_at = p_now
    WHERE w.origin_service_id = v_origin;
  ELSE
    UPDATE __SCHEMA__.wh_origin_position_walk w
    SET after_created_at = v_last_at, after_event_id = v_last_id, next_lap_at = NULL, updated_at = p_now
    WHERE w.origin_service_id = v_origin;
  END IF;

  RETURN QUERY SELECT v_origin, v_ids, v_read < GREATEST(p_batch_size, 1);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_position_ask(INTEGER, INTERVAL, TIMESTAMPTZ) IS
'561 (#2544): the next batch of received events held without a position to ask their origin for: at most p_batch_size held rows of the origin served least recently, after its cursor, without those it answered unknown. A lap that reaches the end restarts after p_lap_pause.';

SELECT __SCHEMA__.drop_all_overloads('wh_origin_positions_of');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:PositionsOf_AnswersStamped_Unstamped_Archived_AndUnknownAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_positions_of(p_event_ids UUID[])
RETURNS TABLE(event_id UUID, state TEXT, commit_sequence BIGINT) AS $$
  SELECT u.eid,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL AND es.commit_sequence IS NOT NULL THEN 'positioned'
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN 'unstamped'
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL AND a.commit_sequence IS NOT NULL THEN 'archived'
           ELSE 'unknown'
         END,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN es.commit_sequence
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL THEN a.commit_sequence
         END
  FROM (SELECT DISTINCT x.eid FROM unnest(p_event_ids) AS x(eid)) u
  LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
  LEFT JOIN __SCHEMA__.wh_event_archive a ON a.event_id = u.eid
  ORDER BY u.eid;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_positions_of(UUID[]) IS
'561 (#2544): per asked event id, this service''s commit sequence for an event it wrote: positioned (from the store), unstamped (held, not stamped yet: ask again), archived (from wh_event_archive), or unknown (never written here, received from another service, or no longer held with a sequence).';
