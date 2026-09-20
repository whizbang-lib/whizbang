-- Migration: 150_BucketAwareClaim
-- Date: 2026-09-09
-- Description: The claim schedules by bucket (priority step 3). claim_orphaned_inbox picks in three lanes: every
--   stream with a pending interactive row anywhere in it (the fold over all of a stream's pending rows, served by a
--   partial index so its cost is bounded by the pending interactive rows), then standard rows plus background rows
--   promoted past the background wait target, then background rows, each of the last two with 145's breadth-first
--   selection and early stop over its own band index so the cost still follows the batch. Background keeps a floor
--   of every batch; inside a lane commands stay ahead of events; per-stream order is untouched. claim_work re-offers
--   the streams an instance holds most urgent bucket first (inbox and perspective), and
--   claim_orphaned_perspective_events selects the most urgent streams first. Signatures are unchanged.
--   158: claim_work is priced by the batch it returns, never by what the instance holds: the orphan guards
--   probe two index heads instead of scanning the pending rows; the outbox re-offer walks the arrival index
--   to its batch; the inbox and perspective re-offers walk lane indexes one index-only probe per stream,
--   lane by lane in batch order from a stream id drawn per poll, and stop at the batch, returning a held
--   stream's oldest row in each lane it appears in rather than every row it holds there; and the inbox
--   event-store chain runs only for rows it has not stamped (wh_inbox.chain_emitted_at). The indexes and
--   the column are in 158.
-- Dependencies: 149_MessagePriority, 148_ActiveStreamLeases, 145_BoundedAcquisitionRewrite
-- Objects: idx_inbox_pending_interactive, idx_inbox_pending_arrival_standard, idx_inbox_pending_arrival_background, claim_orphaned_inbox, claim_work (result set: + priority, received_at), claim_orphaned_perspective_events
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from Migrations/constants.txt at apply time (README rule 12).

-- 162 moves instance_id, lease_expiry, partition_number, processed_at, scheduled_for to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'processed_at' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_pending_interactive
    ON __SCHEMA__.wh_inbox (stream_id, received_at, message_id)
    INCLUDE (instance_id, lease_expiry, scheduled_for, partition_number, is_event)
    WHERE processed_at IS NULL AND priority <= 99;
  END IF;
END $$;
-- Guarded for the same reason as the CREATE above: after 162 drops the columns this
-- index keys on, a replay never creates it, and COMMENT ON a missing index is 42P01.
DO $$
BEGIN
  IF to_regclass('__SCHEMA__.idx_inbox_pending_interactive') IS NOT NULL THEN
    COMMENT ON INDEX __SCHEMA__.idx_inbox_pending_interactive IS
    'Covering partial index over pending INTERACTIVE rows (priority 1 to 99) in per-stream arrival order (150). The '
    'urgent lane in claim_orphaned_inbox folds streams over this set alone, so its cost is bounded by the pending '
    'interactive rows, not the backlog.';
  END IF;
END $$;

-- 162 moves instance_id, lease_expiry, partition_number, processed_at, scheduled_for to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'processed_at' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_pending_arrival_standard
    ON __SCHEMA__.wh_inbox (received_at, message_id)
    INCLUDE (stream_id, instance_id, lease_expiry, scheduled_for, partition_number)
    WHERE processed_at IS NULL AND is_event = TRUE AND priority BETWEEN 100 AND 199;
  END IF;
END $$;
-- Guarded for the same reason as the CREATE above: after 162 drops the columns this
-- index keys on, a replay never creates it, and COMMENT ON a missing index is 42P01.
DO $$
BEGIN
  IF to_regclass('__SCHEMA__.idx_inbox_pending_arrival_standard') IS NOT NULL THEN
    COMMENT ON INDEX __SCHEMA__.idx_inbox_pending_arrival_standard IS
    'Arrival-order partial index over pending STANDARD events (150), so the standard lane''s breadth-first walk with an '
    'early stop never steps over background rows.';
  END IF;
END $$;

-- The band boundaries below are literals in three independent places: Whizbang.Core.Priority.WorkPriority, the
-- CONSTANT declarations in claim_orphaned_inbox further down, and these index predicates. Index DDL cannot
-- reference a plpgsql constant and neither can reference the C# one, so there is no textual way to share them;
-- PriorityLaneIndexUsabilityTests reads all three back and pins them to WorkPriority instead.
--
-- The predicate below MUST be written exactly as the lane queries it, 'priority > 199', not the equivalent
-- 'priority >= 200'. A partial index is only considered when Postgres can prove the query's predicate implies the
-- index's, and that proof is textual: it does not know an integer above 199 is an integer of at least 200. Declared
-- as '>= 200' while claim_orphaned_inbox's background lane filters with 'priority > c_standard_band_end', this index
-- was never once consulted, and the lane read the whole pending set through idx_inbox_received_at, filtering roughly
-- half of it away and then sorting, on every claim. Nothing about the rows returned changes, so only a plan says so:
-- PriorityLaneIndexUsabilityTests asserts each lane can reach its own index.
--
-- The DROP is load-bearing. CREATE INDEX IF NOT EXISTS matches on name alone, so without it a database already
-- carrying the '>= 200' form would keep it forever; the re-run this file's changed hash triggers would do nothing.
DROP INDEX IF EXISTS __SCHEMA__.idx_inbox_pending_arrival_background;
-- 162 moves instance_id, lease_expiry, partition_number, processed_at, scheduled_for to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'processed_at' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_pending_arrival_background
    ON __SCHEMA__.wh_inbox (received_at, message_id)
    INCLUDE (stream_id, instance_id, lease_expiry, scheduled_for, partition_number)
    WHERE processed_at IS NULL AND is_event = TRUE AND priority > 199;
  END IF;
END $$;
-- Guarded for the same reason as the CREATE above: after 162 drops the columns this
-- index keys on, a replay never creates it, and COMMENT ON a missing index is 42P01.
DO $$
BEGIN
  IF to_regclass('__SCHEMA__.idx_inbox_pending_arrival_background') IS NOT NULL THEN
    COMMENT ON INDEX __SCHEMA__.idx_inbox_pending_arrival_background IS
    'Arrival-order partial index over pending BACKGROUND events (150): the background lane''s walk, and the promotion '
    'of streams that waited past the background wait target (a leading range on received_at). Its predicate is written '
    'the way the lane queries it (priority > 199) because a partial index matches by textual implication, not arithmetic.';
  END IF;
END $$;

-- Exactly one overload per framework function: this name is defined at more than one
-- arity across the migration set, and CREATE OR REPLACE at a different arity ADDS an
-- overload beside the old one rather than replacing it. The duplicate then makes every
-- unqualified reference ambiguous (42725) -- including this file's own COMMENT ON
-- FUNCTION -- which fails the whole startup pass and strands every later migration.
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_inbox');

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_inbox: last word 148_ActiveStreamLeases.sql, with the lane block replaced by the bucket lanes.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
DECLARE
  -- 150: the bands (Whizbang.Core.Priority.WorkPriority) and the scheduling constants. The wait target and the
  -- floor are the framework's defaults for this release; a host's batch hook adjusts a stream's number in memory.
  c_interactive_band_end CONSTANT INTEGER := 99;
  c_standard_band_end CONSTANT INTEGER := 199;
  c_background_wait_target_seconds CONSTANT INTEGER := 300;
  c_background_floor_share CONSTANT NUMERIC := 0.1;
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION, not just re-emission. Without a limit here this statement leases every
  -- eligible row in one shot and charges an attempt to each, so an instance restarting onto a large
  -- backlog claims the whole thing instantly. The rows it cannot dispatch inside the lease expire
  -- un-dispatched, get re-claimed here, spend another attempt, and eventually dead-letter as
  -- MaxAttemptsExceeded having never reached a receptor - the error branch below is this statement
  -- stamping its own casualties.
  --
  -- The caller's claim limit previously reached claim_orphaned_perspective_events but not this
  -- function, so both the adaptive claim window and the outstanding budget were throttling a valve
  -- downstream of the flood: p_max_streams bounds only the RE-EMISSION of work already held
  -- (claim_work's eligible_inbox filters on instance_id = p_instance_id). That is why narrowing the
  -- window changed the rate of lease saturation without ever converging.
  --
  -- LIMIT NULL is unlimited in Postgres, so the default preserves the old behavior for any caller
  -- that has not been taught to pass a bound.
  WITH params AS (
    -- BIGINT: the unbounded default is INT max and max_rows + 1 below must not overflow.
    SELECT COALESCE(p_max_rows, 2147483647)::BIGINT AS max_rows,
           -- 150: a background stream that has waited past the background wait target competes as standard.
           p_now - (c_background_wait_target_seconds * INTERVAL '1 second') AS background_promote_before,
           -- 150: the share of a batch the background bucket always receives while it has pending streams.
           GREATEST(1, CEIL(LEAST(COALESCE(p_max_rows, 2147483647), 1000000) * c_background_floor_share))::BIGINT AS background_floor,
           -- 159: rows of one band and one ownership class the acquisition will look at. A multiple
           -- of the batch, and a constant against the backlog, which is the whole point: the window
           -- is what makes a poll cost the same whether the queue holds a thousand rows or a
           -- million. It cannot cost the acquisition its throughput, only its breadth: the lanes
           -- claim p_max_rows rows either way, and a narrower window means a lane meets fewer
           -- distinct streams and takes more rows from each (the few-mode branch below), which is
           -- the same batch drawn less widely. The window is over the OLDEST claimable rows and
           -- those are what the poll claims, so it advances every poll rather than re-reading one
           -- set forever.
           GREATEST(LEAST(COALESCE(p_max_rows, 1000), 1000000), 1)::BIGINT * 8 AS claim_window
  ),
  -- 145: ownership is a per-STREAM property. The two sets below are built ONCE per call from the small
  -- ledger tables and tested by hash membership. 138 evaluated the same predicate as correlated
  -- EXISTS probes on every pending inbox row, which is one of the two reasons the acquisition scan
  -- grew with the backlog instead of the batch (#714). "Live" keeps 025's meaning: a heartbeat within
  -- p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity.
  others_live AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
    WHERE ast.assigned_instance_id <> p_instance_id
      AND ast.lease_expiry > p_now
      AND (
        si.last_heartbeat_at >= p_stale_cutoff
        OR EXISTS (
          SELECT 1 FROM pg_stat_activity sa
          WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || si.instance_id::text
        )
      )
  ),
  mine_owned AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
  ),
  -- 159: the rows this call may take, read once, in an order an index carries. Every lane below
  -- selects from here instead of from wh_inbox, and each had re-derived the same predicate for
  -- itself:
  --
  --   WHERE processed_at IS NULL AND (instance_id IS NULL OR lease_expiry < p_now)
  --
  -- A disjunction has no index order, so each of the five walked the whole pending inbox and threw
  -- away what a live peer holds -- one plan removed 4,716 rows of 4,800 by filter to yield seven
  -- streams -- and fetched a heap page per row doing it, because the stream-ordered index carries
  -- neither is_event nor priority and a queue table under load is never all-visible, so nothing on
  -- it is ever really index-only. Five passes over the backlog per poll, several polls a second,
  -- every instance. Doubling the backlog doubled the poll.
  --
  -- Split in two, each half is a partial predicate a planner can prove, so 159's indexes carry the
  -- order: unowned rows by arrival, leased rows by lease expiry so the longest-expired come first.
  -- One walk per band per class, each stopped at the window, each covered by its index. The bucket
  -- is the leading key so a band draws from its own range: a large background backlog can never
  -- crowd an interactive row out of the window, and the background floor below still finds the
  -- background band populated. Rows of one stream keep their arrival order, which is what per-stream
  -- FIFO rests on, and the lanes' own band, kind, ownership and partition filters are unchanged --
  -- this narrows what they read, never what they choose.
  --
  -- The window is a bound the LIMITs below could not provide on their own. A lane stops at its
  -- LIMIT only when it can reach it, and during an import most pending rows belong to a peer or to
  -- another rank, so a lane that wants eleven streams and can see seven never stops early and runs
  -- to the end of the table. The bound has to be on rows examined, not on rows found.
  --
  -- The scheduled_for predicate rides along: a row that is not due yet is not claimable now, and
  -- every lane applied it.
  claimable AS MATERIALIZED (
    SELECT c.message_id, c.stream_id, c.received_at, c.is_event, c.priority, c.partition_number
    FROM (VALUES (0), (1), (2)) AS b(bucket)
    CROSS JOIN LATERAL (
      (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
       FROM __SCHEMA__.wh_inbox i
       WHERE i.processed_at IS NULL
         AND i.instance_id IS NULL
         AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 ELSE 2 END) = b.bucket
         AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
       ORDER BY i.received_at, i.message_id
       LIMIT (SELECT claim_window FROM params))
      UNION ALL
      (SELECT i.message_id, i.stream_id, i.received_at, i.is_event, i.priority, i.partition_number
       FROM __SCHEMA__.wh_inbox i
       WHERE i.processed_at IS NULL
         AND i.instance_id IS NOT NULL
         AND i.lease_expiry < p_now
         AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 ELSE 2 END) = b.bucket
         AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
       ORDER BY i.lease_expiry, i.received_at, i.message_id
       LIMIT (SELECT claim_window FROM params))
    ) c
  ),
  -- 150 BUCKET LANES (priority step 3). Every row carries an effective priority (149); the claim schedules by the
  -- bucket the number falls in: interactive (1 to 99), standard (100 to 199), background (200 and up). Lane 0 is
  -- every stream with a pending interactive row anywhere in it: the fold is over all of a stream's pending rows,
  -- so an interactive row queued behind bulk rows on its own stream pulls the whole stream forward while the
  -- stream's rows are still taken in order (its predecessors are prerequisites). That set is small by nature and
  -- served by idx_inbox_pending_interactive, so ranking it is bounded by the pending interactive rows, not the
  -- backlog. Lanes 1 and 2 keep 145's breadth-first selection with an early stop, run once per band over the
  -- band's own arrival-order index, so each lane's cost still follows the batch. Background streams keep a floor
  -- of every batch (never starved by a steady standard flow), and a background stream that has waited past the
  -- background wait target competes as standard. Inside a lane a command keeps its place ahead of events (145's
  -- command lane, #721). Priority reorders streams, never rows within a stream.
  urgent_streams AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.priority <= c_interactive_band_end
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows FROM params)
  ),
  pick_urgent AS (
    SELECT i.message_id AS cand_message_id,
           0 AS lane,
           CASE WHEN i.is_event THEN 1 ELSE 0 END AS kind,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    JOIN urgent_streams us ON us.stream_id = i.stream_id
    WHERE TRUE
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
  ),
  urgent_taken AS (
    SELECT LEAST((SELECT count(*) FROM pick_urgent), (SELECT max_rows FROM params))::BIGINT AS n
  ),
  -- Commands outside the urgent streams: a request someone is waiting on keeps its lane ahead of events, inside
  -- the band its number puts it in. Served by idx_inbox_pending_commands; the set is small by nature.
  pick_commands AS (
    SELECT i.message_id AS cand_message_id,
           CASE WHEN (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params)) THEN 2 ELSE 1 END AS lane,
           0 AS kind,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE TRUE
      AND i.is_event = FALSE
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
  ),
  commands_taken AS (
    SELECT LEAST((SELECT count(*) FROM pick_commands),
                 GREATEST((SELECT max_rows FROM params) - (SELECT n FROM urgent_taken), 0))::BIGINT AS n
  ),
  budget AS (
    SELECT GREATEST((SELECT max_rows FROM params) - (SELECT n FROM urgent_taken) - (SELECT n FROM commands_taken), 0) AS remaining_total
  ),
  -- The background reservation: as many rows as the floor, bounded by what is actually pending (a bounded probe)
  -- and by what the batch has left after the urgent rows and the commands.
  background_reserved AS (
    SELECT LEAST((SELECT background_floor FROM params),
                 (SELECT count(*) FROM (
                    SELECT 1 FROM claimable i
                    WHERE TRUE
                      AND i.is_event = TRUE
                      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
                      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
                      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
                    LIMIT (SELECT background_floor FROM params)) probe),
                 (SELECT remaining_total FROM budget))::BIGINT AS n
  ),
  -- LANE 1: standard rows, plus background rows promoted past the wait target; breadth-first, early stop.
  eligible_event_streams_std AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.is_event = TRUE
      AND (i.priority BETWEEN c_interactive_band_end + 1 AND c_standard_band_end OR (i.priority > c_standard_band_end AND i.received_at < (SELECT background_promote_before FROM params)))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows + 1 FROM params)
  ),
  mode_std AS (
    SELECT (SELECT count(*) FROM eligible_event_streams_std) AS n_streams,
           GREATEST((SELECT remaining_total FROM budget) - (SELECT n FROM background_reserved), 0) AS remaining
  ),
  event_heads_std AS (
    SELECT i.message_id AS cand_message_id,
           1 AS lane,
           1 AS kind,
           1::BIGINT AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE (SELECT n_streams > remaining FROM mode_std)
      AND i.is_event = TRUE
      AND (i.priority BETWEEN c_interactive_band_end + 1 AND c_standard_band_end OR (i.priority > c_standard_band_end AND i.received_at < (SELECT background_promote_before FROM params)))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox j
        WHERE j.stream_id = i.stream_id
          AND j.processed_at IS NULL
          AND j.is_event = TRUE
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
          AND (j.instance_id IS NULL OR j.lease_expiry < p_now)
          AND (j.scheduled_for IS NULL OR j.scheduled_for <= p_now)
          AND (j.partition_number IS NULL
               OR (j.partition_number % p_active_instance_count) = p_instance_rank
               OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = j.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      )
    ORDER BY i.received_at, i.message_id
    LIMIT (SELECT remaining FROM mode_std)
  ),
  kk_std AS (
    SELECT GREATEST(1, CEIL(remaining::NUMERIC / GREATEST(n_streams, 1)))::INTEGER AS k FROM mode_std
  ),
  event_few_std AS (
    SELECT h.message_id AS cand_message_id,
           1 AS lane,
           1 AS kind,
           h.rn AS stream_seq,
           h.received_at AS cand_received_at
    FROM eligible_event_streams_std s
    CROSS JOIN kk_std
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at,
             ROW_NUMBER() OVER (ORDER BY i.received_at, i.message_id) AS rn
      FROM claimable i
      WHERE i.stream_id = s.stream_id
        AND i.is_event = TRUE
        AND (i.partition_number IS NULL
             OR (i.partition_number % p_active_instance_count) = p_instance_rank
             OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      ORDER BY i.received_at, i.message_id
      LIMIT kk_std.k
    ) h
    WHERE (SELECT n_streams <= remaining FROM mode_std)
  ),
  standard_taken AS (
    SELECT ((SELECT count(*) FROM event_heads_std) + (SELECT count(*) FROM event_few_std))::BIGINT AS n
  ),
  -- LANE 2: background rows inside the wait target; whatever the standard lane left, never less than the floor.
  eligible_event_streams_bg AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM claimable i
    WHERE TRUE
      AND i.is_event = TRUE
      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows + 1 FROM params)
  ),
  mode_bg AS (
    SELECT (SELECT count(*) FROM eligible_event_streams_bg) AS n_streams,
           GREATEST((SELECT remaining_total FROM budget) - (SELECT n FROM standard_taken), 0) AS remaining
  ),
  event_heads_bg AS (
    SELECT i.message_id AS cand_message_id,
           2 AS lane,
           1 AS kind,
           1::BIGINT AS stream_seq,
           i.received_at AS cand_received_at
    FROM claimable i
    WHERE (SELECT n_streams > remaining FROM mode_bg)
      AND i.is_event = TRUE
      AND (i.priority > c_standard_band_end AND i.received_at >= (SELECT background_promote_before FROM params))
      AND i.stream_id NOT IN (SELECT stream_id FROM urgent_streams)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox j
        WHERE j.stream_id = i.stream_id
          AND j.processed_at IS NULL
          AND j.is_event = TRUE
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
          AND (j.instance_id IS NULL OR j.lease_expiry < p_now)
          AND (j.scheduled_for IS NULL OR j.scheduled_for <= p_now)
          AND (j.partition_number IS NULL
               OR (j.partition_number % p_active_instance_count) = p_instance_rank
               OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = j.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      )
    ORDER BY i.received_at, i.message_id
    LIMIT (SELECT remaining FROM mode_bg)
  ),
  kk_bg AS (
    SELECT GREATEST(1, CEIL(remaining::NUMERIC / GREATEST(n_streams, 1)))::INTEGER AS k FROM mode_bg
  ),
  event_few_bg AS (
    SELECT h.message_id AS cand_message_id,
           2 AS lane,
           1 AS kind,
           h.rn AS stream_seq,
           h.received_at AS cand_received_at
    FROM eligible_event_streams_bg s
    CROSS JOIN kk_bg
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at,
             ROW_NUMBER() OVER (ORDER BY i.received_at, i.message_id) AS rn
      FROM claimable i
      WHERE i.stream_id = s.stream_id
        AND i.is_event = TRUE
        AND (i.partition_number IS NULL
             OR (i.partition_number % p_active_instance_count) = p_instance_rank
             OR (p_allow_steal AND NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      ORDER BY i.received_at, i.message_id
      LIMIT kk_bg.k
    ) h
    WHERE (SELECT n_streams <= remaining FROM mode_bg)
  ),
  pick AS (
    -- A stream with rows in two bands is picked by both lanes (each takes the stream's heads in order); keep
    -- the more urgent lane's row so no message is a candidate twice.
    SELECT DISTINCT ON (u.cand_message_id) u.cand_message_id, u.lane, u.kind, u.stream_seq, u.cand_received_at
    FROM (
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM pick_urgent
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM pick_commands
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_heads_std
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_few_std
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_heads_bg
      UNION ALL
      SELECT cand_message_id, lane, kind, stream_seq, cand_received_at FROM event_few_bg
    ) u
    ORDER BY u.cand_message_id, u.lane
  ),
  -- 159: the batch is cut before the row lookup, not after it. The lanes together offer several
  -- times the batch, every one of them was fetched by primary key to be ordered and then discarded,
  -- and the whole ordering key (lane, kind, stream order, arrival, id) is already carried here --
  -- nothing the lookup returns takes part in it. A few batches of headroom is kept so SKIP LOCKED
  -- below still has rows to fall through to when a peer holds the head of the order; past that a
  -- contended poll returns a short batch and the next poll takes the rest, which is what SKIP LOCKED
  -- means everywhere else in this function.
  pick_ordered AS (
    SELECT u.cand_message_id, u.lane, u.kind, u.stream_seq, u.cand_received_at
    FROM pick u
    ORDER BY u.lane, u.kind, u.stream_seq, u.cand_received_at, u.cand_message_id
    LIMIT (SELECT max_rows * 4 FROM params)
  ),
  candidates AS (
    -- Lock under lane order. SKIP LOCKED skips rows a concurrent claimer holds, and the volatile predicates
    -- re-check under the lock - a row leased between pick and here is filtered exactly as before.
    SELECT i.message_id AS cand_message_id,
           pick.lane AS cand_lane,
           pick.kind AS cand_kind,
           pick.stream_seq AS cand_stream_seq,
           pick.cand_received_at
    FROM __SCHEMA__.wh_inbox i
    JOIN pick_ordered pick ON pick.cand_message_id = i.message_id
    WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND i.processed_at IS NULL
    ORDER BY pick.lane, pick.kind, pick.stream_seq, pick.cand_received_at, pick.cand_message_id
    LIMIT (SELECT max_rows FROM params)
    FOR UPDATE OF i SKIP LOCKED
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_inbox i
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: claim_orphaned is the SOLE source of attempt counting.
        -- Bumps unconditionally on every claim (fresh or re-claim) so attempts = 1 means
        -- "first attempt has started" (one-based). process_inbox_failures records error and
        -- releases the lease but does NOT bump - the next claim's bump captures attempt N+1.
        -- Single-source removes the double-counting that two bumps per failed cycle would cause.
        -- Without this, hung handlers (no exception thrown, lease eventually expires) looked
        -- identical to fresh messages in a consumer's production environment - an extended stuck-message backlog with
        -- attempts=0 across thousands of rows (production audit).
        attempts = i.attempts + 1,
        -- Attribute the attempt this claim is REPLACING. process_inbox_failures records
        -- error/failure_reason only when dispatch reported a failure; when the process is killed
        -- mid-dispatch (SIGKILL from a failed liveness probe, container replaced, handler hung past
        -- its lease) nothing reports anything - the lease just expires and the bump above spends
        -- another attempt in silence. The budget then runs out and the row dead-letters as
        -- "MaxAttemptsExceeded: attempts=N > max=M", which describes the counter and not the cause.
        --
        -- Observed in production as ~54k inbox rows averaging 11 attempts, every one with
        -- error IS NULL and failure_reason = 99 (Unknown) - no way to tell a crash-looping host
        -- from a genuinely failing handler. Stamp the abandonment so the row carries its own
        -- history: an expired lease still held by an instance (instance_id IS NOT NULL) means the
        -- previous attempt ended without ever reporting. Guarded on error IS NULL so a real
        -- recorded failure is never papered over - that error is the better diagnostic.
        failure_reason = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 6  -- MessageFailureReason.LeaseExpired
          ELSE i.failure_reason
        END,
        error = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 'Attempt ' || i.attempts || ' ended without a reported outcome: lease held by instance '
               || i.instance_id::text || ' expired at ' || i.lease_expiry::text
               || ' (process terminated mid-dispatch, or the handler outran its lease). '
               || 'No dispatch failure was recorded for that attempt.'
          ELSE i.error
        END
    -- Ownership and partition routing were resolved in `candidates` above, under a row lock. Only
    -- the two cheap invariants are re-asserted here, as defense against a lease that lapsed between
    -- selection and write:
    --
    --   OWNER PATH - a stream's live owner always claims its messages, ignoring partition modulo,
    --   which preserves per-stream FIFO and prevents the rank-churn wedge seen in production: when
    --   active_instance_count changes, modulo routing for a partition can shift to a different rank
    --   than the stream's existing owner. Without that branch the modulo-matched instance is blocked
    --   by the ownership NOT EXISTS and the owner is blocked by the modulo filter - neither claims.
    --
    --   UNOWNED / ABANDONED PATH - no live owner, so partition-based load balancing decides the rank.
    --   NULL partition_number (no stream binding) stays claimable by any rank. "Live" means a
    --   heartbeat within p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity; an
    --   instance killed by SIGKILL holds no meaningful ownership, and without the recency clause its
    --   dead lease would block cross-instance claims for the full lease duration (300 s default).
    --   See 011 (cleanup_stale_instances) for the eventual DELETE - this claim-time check is what
    --   makes recovery happen at the stale threshold rather than at lease expiry.
    FROM candidates c
    WHERE i.message_id = c.cand_message_id
      AND i.processed_at IS NULL
      AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
    RETURNING i.message_id AS c_message_id, i.stream_id AS c_stream_id, i.partition_number AS c_partition_number,
              c.cand_lane AS c_lane, c.cand_kind AS c_kind, c.cand_stream_seq AS c_stream_seq, c.cand_received_at AS c_received_at
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into REFRESH (row-only
  -- UPDATE for already-owned-with-live-lease streams) + PIN (INSERT...ON CONFLICT for
  -- the rare ownership-transition case, with ORDER BY stream_id for consistent lock
  -- acquisition). Symmetric with the fix in claim_orphaned_outbox (mig 024); see that
  -- migration for the full rationale. Eliminates the 40P01 deadlock observed in
  -- production (Whizbang PR #227).
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND c.c_stream_id IS NOT NULL
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, COALESCE(c.c_partition_number, 0) AS partition_number
      FROM claimed c
      WHERE c.c_stream_id IS NOT NULL
        AND NOT EXISTS (
          SELECT 1 FROM refreshed r WHERE r.refreshed_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO UPDATE
      SET last_activity_at = EXCLUDED.last_activity_at,
          assigned_instance_id = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.assigned_instance_id
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.assigned_instance_id
            ELSE ast.assigned_instance_id
          END,
          -- 148 (#731): the stream lease follows the assignment. Whoever the CASE above leaves as owner
          -- holds the lease: a stream this instance takes (unowned, orphaned by a deregistered instance,
          -- or already its own) is leased to p_lease_expiry; a stream another registered instance keeps
          -- is not touched.
          lease_expiry = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.lease_expiry
            WHEN ast.assigned_instance_id = EXCLUDED.assigned_instance_id THEN EXCLUDED.lease_expiry
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.lease_expiry
            ELSE ast.lease_expiry
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  -- 150: the result carries the pick order (bucket lane, commands before events, then breadth-first). UPDATE ... RETURNING
  -- alone promises no order, and a caller that consumes the rows in order relies on the lane.
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c
  ORDER BY c.c_lane, c.c_kind, c.c_stream_seq, c.c_received_at, c.c_message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_inbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, BOOLEAN) IS
  'Acquires unowned/abandoned pending inbox rows for an instance in bucket order (150): streams with a pending interactive row first, then standard (and promoted background) streams, then background streams with a floor of the batch; commands before events inside a lane; breadth-first with an early stop per lane (145); stream leases (148).';

-- ---------------------------------------------------------------------------------------------
-- claim_work: last word 145_BoundedAcquisitionRewrite.sql; held inbox and perspective streams re-offered bucket first,
-- and the result carries each inbox row's priority and arrival for the batch hooks (a result-set change: dropped first).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('claim_work');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300,
  p_fresh_share DOUBLE PRECISION DEFAULT 0.5,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE,
  p_max_perspective_streams INTEGER DEFAULT NULL
) RETURNS TABLE(
  source VARCHAR(20),           -- __CATEGORY_OUTBOX__ | __CATEGORY_INBOX__ | 'receptor' | __CATEGORY_PERSPECTIVE__
  work_id UUID,
  work_stream_id UUID,
  partition_number INTEGER,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  message_data TEXT,
  metadata JSONB,
  status INTEGER,
  attempts INTEGER,
  is_newly_stored BOOLEAN,
  is_orphaned BOOLEAN,
  perspective_name VARCHAR(200),
  priority INTEGER,             -- 150: the inbox row's effective priority (NULL for other sources)
  received_at TIMESTAMPTZ       -- 150: the inbox row's arrival (NULL for other sources)
) AS $$
DECLARE
  c_source_outbox CONSTANT VARCHAR(20) := __CATEGORY_OUTBOX__;
  c_source_inbox CONSTANT VARCHAR(20) := __CATEGORY_INBOX__;
  v_has_any_work BOOLEAN;
BEGIN
  -- 157: this function runs under plan_cache_mode = force_custom_plan (see its closing clause), so
  -- the plans below and in the acquisition functions it calls are made for the tables as they are
  -- at each poll, never kept from a poll that found them empty.
  -- Empty-call short-circuit: cheap indexed EXISTS lookups on partial indexes.
  -- Each LIMIT 1 against an existing partial index is sub-millisecond when buffer-cached.
  -- Note: wh_receptor_processing uses completed_at (not processed_at) for the "is done" semantic.
  -- 115: coalesce-pending rows are not claim-pump work — they wait for the coalesce worker.
  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL AND coalesce_group IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_receptor_processing WHERE completed_at IS NULL LIMIT 1);

  IF NOT v_has_any_work THEN
    RETURN;  -- empty result set; orphan-claim sub-functions never invoked
  END IF;

  -- Non-empty path: claim outbox work and return it.
  -- Inbox / receptor / perspective claim + return land in subsequent TDD cycles.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    v_count INTEGER;
    -- v0.661: track per-category RETURN QUERY rowcount so the drain-mode hint
    -- can be derived from ROW_COUNT instead of four fresh COUNT(*) queries.
    v_outbox_rows INTEGER := 0;
    v_inbox_rows INTEGER := 0;
    v_receptor_rows INTEGER := 0;
    v_perspective_rows INTEGER := 0;
    -- 158: the lane walks that re-offer held inbox and perspective streams.
    v_bucket INTEGER;
    v_is_event BOOLEAN;
    v_start UUID;
    v_remaining INTEGER;
    v_rows INTEGER;
  BEGIN
    -- Self-heal this instance's own registration before ranking against it. When a pod's heartbeat
    -- lapses past the stale cutoff (a GC pause, thread-pool starvation, a database failover), the
    -- stale-instance cleanup reaps its wh_service_instances row. Before this repair, every claim
    -- from that point on failed on the missing row, and because the failure aborted the claim, no
    -- work on the claim path was left to put the row back -- the instance stayed locked out until
    -- it was restarted. Repairing here closes that loop, using the identity the caller already
    -- passes in, so the restored row carries the real service name / host / process id rather than
    -- a placeholder.
    --
    -- Guarded by an indexed primary-key pre-check: claim_work is polled continuously, so an
    -- unconditional UPSERT would write a new row version per poll per instance and bloat the
    -- table. On the healthy path this performs no write at all. Only last_heartbeat_at is
    -- refreshed on conflict -- metadata stays owned by record_heartbeat.
    IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_service_instances
      WHERE instance_id = p_instance_id
        AND last_heartbeat_at >= v_stale_cutoff
    ) THEN
      INSERT INTO __SCHEMA__.wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES
        (p_instance_id, p_service_name, p_host_name, p_process_id, v_now, v_now)
      ON CONFLICT (instance_id) DO UPDATE SET
        last_heartbeat_at = EXCLUDED.last_heartbeat_at;
    END IF;

    SELECT instance_rank, active_instance_count INTO v_rank, v_count
    FROM __SCHEMA__.calculate_instance_rank(p_instance_id, v_stale_cutoff);

    -- v0.683 — per-inner-function guards. The existing v_has_any_work short-circuit
    -- (top of the function) only fires when ALL four queues are empty. Under steady
    -- import load, that's rare — but the typical pattern is "one queue has work,
    -- the others don't." Without per-function guards, claim_work paid the full
    -- claim_orphaned_*/emit_chain scan cost on every call regardless. Each guard
    -- uses an existing partial index (idx_{outbox,inbox}_unprocessed_claiming WHERE
    -- processed_at IS NULL, etc.) so the EXISTS probe is sub-millisecond. Behavior
    -- is preserved: if a guard returns false, the corresponding inner function had
    -- no rows to claim anyway, so skipping its scan is a pure win.

    -- Claim orphaned / unowned outbox work — only if any outbox row is unprocessed
    -- AND either unowned or has an expired lease (the orphan predicate matched by
    -- claim_orphaned_outbox's WHERE clause).
    -- 158: two index probes, not one scan. A single predicate over "unowned or expired" has to examine
    -- every pending row to prove there is none, which on a busy instance is its whole holdings on
    -- every poll. Unowned rows are found under instance_id IS NULL through the outstanding-by-instance
    -- index (123); expired leases are the head of the lease-expiry index (158), so an instance whose
    -- leases are all live proves it at the first entry.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE processed_at IS NULL
        AND coalesce_group IS NULL  -- 115: pending singles are never orphan-claimable
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND coalesce_group IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- Bounded for the same reason as the inbox call below: without a limit this acquires the whole
      -- eligible backlog in one statement, and the caller's claim limit never reaches the flood.
      PERFORM __SCHEMA__.claim_orphaned_outbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        p_max_streams
      );
    END IF;

    -- Claim orphaned / unowned inbox work — same predicate shape.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- p_max_streams bounds ACQUISITION here, not just the re-emission below. Omitting it let this
      -- call lease the entire eligible backlog in one statement — charging an attempt to every row —
      -- while the caller's claim window and outstanding budget bounded only what came back out of
      -- eligible_inbox. Both throttles sat downstream of the flood, so neither could ever have held.
      -- 145: acquisition has its own ROW bound. p_max_streams is a stream count; handing it to the
      -- acquisition as a row cap turned fat streams into one row per cycle (#714). The caller passes
      -- the rows it can afford; a caller that does not is bounded by the stream count as before.
      PERFORM __SCHEMA__.claim_orphaned_inbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        COALESCE(p_max_rows, p_max_streams), p_allow_steal
      );
    END IF;

    -- Claim orphaned perspective events — same predicate shape on
    -- wh_perspective_events.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- 145 (#719): perspective ACQUISITION has its own bound. The caller passes 0 while its drain channel is
      -- above its cap, so a perspective backlog is drained before more is leased; re-emission of held work
      -- below stays on p_max_streams so the drain keeps moving.
      -- 160: the perspective acquisition gets a ROW bound as well as its stream bound. It used to
      -- take a batch of streams and lease every pending event of each, so one poll leased whatever
      -- a consumer's streams happened to hold and cost accordingly. The caller's own budget is used
      -- when it passes one; otherwise a multiple of the batch, because handing a stream count
      -- straight to an acquisition as a row cap is 145's #714 mistake in reverse.
      PERFORM __SCHEMA__.claim_orphaned_perspective_events(
        p_instance_id, v_lease_expiry, v_now, COALESCE(p_max_perspective_streams, p_max_streams), v_rank, v_count,
        COALESCE(p_max_rows, GREATEST(COALESCE(p_max_perspective_streams, p_max_streams), 1) * 8)
      );
    END IF;

    -- Claim orphaned receptor work — wh_receptor_processing uses completed_at
    -- (not processed_at) for the "is done" semantic.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_receptor_processing
      WHERE completed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_receptor_work(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now
      );
    END IF;

    -- Back-fill wh_event_store + wh_perspective_events for inbox events claimed above.
    -- Replaces legacy process_work_batch Phase 4.5B + 4.6 self-healing — ensures that by the
    -- time an inbox event row reaches InboxDispatchWorker, its event_store row exists and
    -- perspective_events have been created so PerspectiveWorker can pick them up.
    --
    -- v0.683 guard: only call when this instance owns at least one unprocessed
    -- inbox event row with a stream_id. emit_chain's own internal NOT EXISTS
    -- check against wh_event_store filters out already-emitted rows; we don't
    -- repeat that check here because a production measurement
    -- showed the wrapping NOT EXISTS predicate at 42 ms mean (~4-5% of total
    -- DB time) under heavy inbox load — overwhelming the savings from skipping
    -- emit_chain. The simpler EXISTS uses idx_inbox_instance_lease and is
    -- sub-millisecond. The handler-delay backlog scenario where every event_id
    -- is already in wh_event_store is rare and is more appropriately addressed
    -- on the handler side (composite events) than in the work-pump.
    -- 158: only rows the chain has never read (idx_inbox_chain_pending). A holder in steady state
    -- has none, and the poll skips the chain without touching a held row.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox i
      WHERE i.instance_id = p_instance_id
        AND i.processed_at IS NULL
        AND i.is_event = true
        AND i.stream_id IS NOT NULL
        AND i.chain_emitted_at IS NULL
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__._emit_event_store_chain_for_inbox(p_instance_id, v_lease_expiry, v_now, p_partition_count);
    END IF;

    -- Return outbox work owned by this instance, oldest first, a batch of it.
    -- 158: a walk of idx_outbox_held_arrival that stops at the batch. The previous shape ranked every
    -- held row per stream with a window function it never read and sorted them all before taking the
    -- batch, so the poll priced itself by the holdings: a busy instance holds thousands of leased rows
    -- and polls several times a second. Same rows in the same order, ties broken by message id.
    RETURN QUERY
    -- Per-stream-drain projection (Phase H step 5b): claim_work returns stream_ids only for
    -- outbox. The OutboxDrainWorker consumes WorkBatch.OutboxStreamIds and pulls full payloads
    -- on demand via fetch_outbox_batch. Body columns are NULL — keeps the bytes-on-the-wire
    -- proportional to the active stream set, not the leased-row count × payload size.
    SELECT
      c_source_outbox               AS source,
      o.message_id                  AS work_id,
      o.stream_id                   AS work_stream_id,
      o.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      o.status,
      o.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      NULL::INTEGER                 AS priority,
      NULL::TIMESTAMPTZ             AS received_at
    FROM __SCHEMA__.wh_outbox o
    WHERE o.instance_id = p_instance_id
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL  -- 115: pending singles; the index predicate
      AND o.lease_expiry > v_now
      AND o.published_at IS NULL  -- skip debug-mode forensic rows (production never sets this — row is deleted)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
    ORDER BY o.created_at, o.message_id
    LIMIT p_max_streams;

    -- v0.661: track this category's RETURN QUERY rowcount so the drain-mode
    -- hint at function end can be derived from ROW_COUNT instead of a fresh
    -- COUNT(*) scan. See drain-mode hint block below.
    GET DIAGNOSTICS v_outbox_rows = ROW_COUNT;

    -- Return inbox work owned by this instance: one row per lane a held stream appears in, a batch
    -- of them.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_inbox_held_lanes one index-only probe per stream, lane by lane in the
    -- order the batch is ordered: most urgent bucket first (150), commands before events inside a
    -- bucket (145). Inside a lane the fresh and retried classes are walked side by side (126), each
    -- able to take the whole batch, because the share must not hold a slot empty when the other
    -- class is: what apportions them is the interleave below, not the walks. A lane's walk starts at
    -- a stream id drawn per poll and wraps once, so a lane larger than the batch does not enumerate
    -- the same streams on every poll; a lane no larger than the batch is enumerated whole. One step
    -- lands on one stream, so a lane costs exactly the streams it enumerates.
    --
    -- One row stands for a held stream in a lane: the oldest row the instance holds of it there,
    -- carrying that row's number, arrival and attempts. A stream whose rows span lanes is returned
    -- once per lane, exactly as before, and the batch hooks fold those rows to the stream's most
    -- urgent number and oldest arrival (ClaimedInboxStreamFolder), unchanged. What is no longer
    -- returned is a stream's further rows inside one lane: the drain consumes stream ids and pulls a
    -- stream's rows on demand (fetch_inbox_batch), so they carried nothing a caller read, and ranking
    -- them is what cost the poll. A stream met in both classes of a lane stands as its retried row,
    -- which is its head -- an attempt is charged at the head and everything older is processed, so a
    -- stream with a retried row has a retried head, and stream FIFO means the fresh rows behind it
    -- cannot dispatch anyway (126). The previous shape ranked every held row with three window
    -- functions on every poll and fetched every held row's heap page to do it, and a busy instance
    -- holds thousands of rows and polls several times a second.
    --
    -- GREATEST drops a NULL, so a NULL batch leaves nothing to fill and the re-offer returns nothing.
    -- Nothing passes NULL (the parameter defaults to 1000 and the coordinators pass an integer), and
    -- returning nothing is the safe reading of "no batch size": the previous LIMIT NULL meant no
    -- bound at all, which is the failure this migration exists to stop.
    v_remaining := GREATEST(p_max_streams, 0);
    <<inbox_lanes>>
    FOR v_bucket IN 0..2 LOOP
      FOREACH v_is_event IN ARRAY ARRAY[false, true] LOOP
        EXIT inbox_lanes WHEN v_remaining <= 0;
        v_start := gen_random_uuid();
        RETURN QUERY
        WITH RECURSIVE lanes AS (
          -- Two walks side by side, one per class, seeded at the drawn point. A seed is a bound and
          -- not a candidate.
          SELECT c.fresh_lane, v_start AS stream_id, false AS wrapped, true AS is_seed, 0 AS steps,
                 NULL::INTEGER AS priority, NULL::UUID AS message_id, NULL::TIMESTAMPTZ AS received_at,
                 NULL::INTEGER AS attempts, NULL::INTEGER AS partition_number, NULL::INTEGER AS status
          FROM (VALUES (true), (false)) AS c(fresh_lane)
          UNION ALL
          SELECT h.fresh_lane, n.stream_id, n.wrapped, false, h.steps + 1,
                 n.priority, n.message_id, n.received_at, n.attempts, n.partition_number, n.status
          FROM lanes h
          CROSS JOIN LATERAL (
            -- The lane's next stream after the current one, up to the drawn point once wrapped. The
            -- index is keyed (holder, bucket, kind, class, stream, arrival, id) and covers every
            -- column below, so this is one index-only probe: it lands on the stream's oldest row in
            -- the lane and the next stream is one probe away. The upper bound is written as a CASE
            -- rather than an OR so it stays an index condition; as a filter the walk would read past
            -- the drawn point instead of stopping at it.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, h.wrapped AS wrapped
             FROM __SCHEMA__.wh_inbox i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND i.stream_id > h.stream_id
               AND i.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            UNION ALL
            -- ...or, past the lane's last stream, its first one: the walk wraps, once.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, true
             FROM __SCHEMA__.wh_inbox i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND NOT h.wrapped
               AND i.stream_id <= v_start
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            LIMIT 1
          ) n
          -- One step, one stream: each class may take the whole batch, and the interleave apportions.
          WHERE h.steps < v_remaining
        ),
        inbox_candidates AS (
          -- A stream met in both classes stands as its retried row: that row is its head (126).
          SELECT DISTINCT ON (l.stream_id)
                 l.stream_id, l.priority, l.message_id, l.received_at, l.attempts,
                 l.partition_number, l.status
          FROM lanes l
          WHERE NOT l.is_seed
          ORDER BY l.stream_id, l.fresh_lane
        ),
        ranked_inbox AS (
          SELECT c.stream_id, c.priority, c.message_id, c.received_at, c.attempts,
                 c.partition_number, c.status,
                 (c.attempts = 0) AS is_fresh,
                 ROW_NUMBER() OVER (PARTITION BY (c.attempts = 0) ORDER BY c.received_at, c.message_id) AS class_rank
          FROM inbox_candidates c
        ),
        ordered_inbox AS (
          SELECT ri.stream_id, ri.priority, ri.message_id, ri.received_at, ri.attempts,
                 ri.partition_number, ri.status
          FROM ranked_inbox ri
          ORDER BY
            -- 126: fresh-head streams receive p_fresh_share of the batch, retried-head streams the
            -- remainder, each class in arrival order; an empty class hands its share to the other,
            -- because the key only competes candidates that exist.
            CASE WHEN ri.is_fresh
                 THEN (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(LEAST(p_fresh_share, 1.0), 0.000001)
                 ELSE (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(1.0 - LEAST(p_fresh_share, 1.0), 0.000001)
            END,
            ri.received_at,
            ri.message_id
          LIMIT v_remaining
        )
        -- Per-stream-drain projection (Phase H step 5d): inbox follows outbox into stream-ids-only.
        -- InboxDrainWorker reads stream_ids off IInboxDrainChannel and pulls payloads on demand
        -- via fetch_inbox_batch. Body columns are NULL -- keeps claim_work's bytes-on-the-wire
        -- proportional to active stream count.
        SELECT
          c_source_inbox                AS source,
          oi.message_id                 AS work_id,
          oi.stream_id                  AS work_stream_id,
          oi.partition_number,
          NULL::VARCHAR(200)            AS destination,
          NULL::VARCHAR(500)            AS message_type,
          NULL::VARCHAR(500)            AS envelope_type,
          NULL::TEXT                    AS message_data,
          NULL::JSONB                   AS metadata,
          oi.status,
          oi.attempts,
          false                         AS is_newly_stored,
          false                         AS is_orphaned,
          NULL::VARCHAR(200)            AS perspective_name,
          oi.priority                   AS priority,
          oi.received_at                AS received_at
        FROM ordered_inbox oi;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_remaining := v_remaining - v_rows;
      END LOOP;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_inbox_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- Return receptor work owned by this instance.
    -- Receptor work uses `id` as the work_id (not message_id) and `completed_at` as the "done" marker.
    -- Most fields are NULL — receptors carry their state in dedicated columns the worker reads
    -- directly via the work_id; the row here just signals "this receptor needs attention".
    RETURN QUERY
    SELECT
      'receptor'::VARCHAR(20)       AS source,
      rp.id                         AS work_id,
      rp.stream_id                  AS work_stream_id,
      rp.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      rp.status::INTEGER,
      rp.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      NULL::INTEGER                 AS priority,
      NULL::TIMESTAMPTZ             AS received_at
    FROM __SCHEMA__.wh_receptor_processing rp
    WHERE rp.instance_id = p_instance_id
      AND rp.lease_expiry > v_now
      AND rp.completed_at IS NULL
    LIMIT p_max_streams;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_receptor_rows = ROW_COUNT;

    -- Return perspective work as one row per distinct stream owned by this instance, most urgent
    -- bucket first, oldest event first within a bucket, a batch of streams.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_perspective_held_lanes one index-only probe per stream, bucket by bucket,
    -- most urgent first; the first entry of a stream in its bucket is its oldest held event there,
    -- which is what orders the streams within the bucket. The walk starts at a stream id drawn per
    -- poll and wraps once, so a bucket larger than the batch does not enumerate the same streams on
    -- every poll, and one no larger than the batch is enumerated whole. One step lands on one stream,
    -- so a bucket costs exactly the streams it enumerates. The previous shape aggregated every held
    -- event per poll to put streams with a hundred or fewer pending events ahead of larger ones; that
    -- tier guarded a batch of rows against one large stream, and the drain has been per stream with an
    -- unbounded channel since Phase H, so a large stream no longer displaces small ones.
    v_remaining := GREATEST(p_max_streams, 0);
    FOR v_bucket IN 0..2 LOOP
      EXIT WHEN v_remaining <= 0;
      v_start := gen_random_uuid();
      RETURN QUERY
      WITH RECURSIVE lane AS (
        -- Seeded at the drawn point; the seed is not a candidate.
        SELECT v_start AS stream_id, NULL::UUID AS event_id, false AS wrapped, true AS is_seed, 0 AS steps
        UNION ALL
        SELECT n.stream_id, n.event_id, n.wrapped, false, h.steps + 1
        FROM lane h
        CROSS JOIN LATERAL (
          -- The bucket's next stream after the current one, up to the drawn point once wrapped...
          (SELECT pe.stream_id, pe.event_id, h.wrapped AS wrapped
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND pe.stream_id > h.stream_id
             AND pe.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          UNION ALL
          -- ...or, past the bucket's last stream, its first one: the walk wraps.
          (SELECT pe.stream_id, pe.event_id, true
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 ELSE 2 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND NOT h.wrapped
             AND pe.stream_id <= v_start
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          LIMIT 1
        ) n
        WHERE h.steps < v_remaining
      )
      SELECT
        'perspective_stream'::VARCHAR(20) AS source,
        NULL::UUID                        AS work_id,
        l.stream_id                       AS work_stream_id,
        NULL::INTEGER                     AS partition_number,
        NULL::VARCHAR(200)                AS destination,
        NULL::VARCHAR(500)                AS message_type,
        NULL::VARCHAR(500)                AS envelope_type,
        NULL::TEXT                        AS message_data,
        NULL::JSONB                       AS metadata,
        0::INTEGER                        AS status,
        0::INTEGER                        AS attempts,
        false                             AS is_newly_stored,
        false                             AS is_orphaned,
        NULL::VARCHAR(200)                AS perspective_name,
        NULL::INTEGER                     AS priority,
        NULL::TIMESTAMPTZ                 AS received_at
      FROM lane l
      WHERE NOT l.is_seed
      ORDER BY l.event_id, l.stream_id
      LIMIT v_remaining;

      GET DIAGNOSTICS v_rows = ROW_COUNT;
      v_remaining := v_remaining - v_rows;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_perspective_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- 130 doorbell debounce: finding work stamps this instance's watermark — the signal
    -- producers use to suppress redundant notifies while this drainer is awake. Rides
    -- inside the claim (zero extra round trips) and skips the empty case so the
    -- empty-call short-circuit's ~1 ms idle floor is untouched.
    -- 140: the stamp never waits (issue #699). The store's debounce touches the same rows inside
    -- its own transaction; under a burst the two met in opposite orders with the inbox row locks
    -- and deadlocked. A watermark row another session holds is skipped this tick: the stamp is a
    -- freshness hint and the next tick re-stamps it. Rows that do not exist yet are created; rows
    -- that exist and are free are stamped in place.
    WITH kinds AS (
      SELECT k.kind
      FROM (VALUES (c_source_outbox), (c_source_inbox), (__CATEGORY_PERSPECTIVE__)) AS k(kind)
    WHERE (k.kind = c_source_outbox AND v_outbox_rows > 0)
       OR (k.kind = c_source_inbox AND (v_inbox_rows > 0 OR v_receptor_rows > 0))
       -- 133: the perspective watermark must reflect DRAINABLE progress, not merely a
       -- claimed Stored-but-fence-held row. claim_work returns a perspective_stream row for
       -- any leased unprocessed perspective_event, but a row whose underlying event is still
       -- unstamped (commit_sequence IS NULL, held by the per-database ordering fence) cannot
       -- be surfaced by the fetch gate — the drainer spins with no progress. Arming the
       -- watermark for it lets the doorbell debounce (130/131) suppress the fence-clearing
       -- stamp's make-up ring, stranding visibility on the adaptive poll cap (issue #677).
       -- The EXISTS runs at most once: the k.kind guard short-circuits it away for the
       -- outbox/inbox VALUES rows.
       -- 158: the question is asked of a batch of the rows leased longest, not of every held row.
       -- Over all of them it was priced by the holdings whenever the fence held (#677 is exactly
       -- that state), joining every leased row to the event store per poll. A held row whose event is
       -- stamped is found among the oldest leases if it is found at all; when none of them is, the
       -- watermark stays unarmed and the make-up ring goes through, which is the safe direction.
       OR (k.kind = __CATEGORY_PERSPECTIVE__ AND v_perspective_rows > 0 AND EXISTS (
             SELECT 1
             FROM (
               SELECT pe.event_id
               FROM __SCHEMA__.wh_perspective_events pe
               WHERE pe.instance_id = p_instance_id
                 AND pe.lease_expiry > v_now
                 AND pe.processed_at IS NULL
               ORDER BY pe.lease_expiry
               LIMIT GREATEST(p_max_streams, 1)
             ) held
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = held.event_id
             WHERE es.commit_sequence IS NOT NULL))
    ),
    lockable AS (
      SELECT ns.instance_id, ns.payload_kind
      FROM __SCHEMA__.wh_notify_state ns
      JOIN kinds ON kinds.kind = ns.payload_kind
      WHERE ns.instance_id = p_instance_id
      FOR UPDATE OF ns SKIP LOCKED
    ),
    stamped AS (
      UPDATE __SCHEMA__.wh_notify_state ns
      SET last_work_at = NOW()
      FROM lockable
      WHERE ns.instance_id = lockable.instance_id AND ns.payload_kind = lockable.payload_kind
      RETURNING ns.payload_kind
    )
    INSERT INTO __SCHEMA__.wh_notify_state (instance_id, payload_kind, last_work_at)
    SELECT p_instance_id, kinds.kind, NOW()
    FROM kinds
    WHERE NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_notify_state ns2
      WHERE ns2.instance_id = p_instance_id AND ns2.payload_kind = kinds.kind)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    -- Drain-mode hint: if any of the four return categories filled its LIMIT
    -- (rows == p_max_streams), there's likely more eligible work for this
    -- instance — RAISE NOTICE so the C# claim worker skips its wait and
    -- re-polls immediately. Survives pgbouncer (protocol message, not a
    -- session-state thing).
    --
    -- v0.661 forensic (gate.hold_duration_ms histogram during a consumer's
    -- draft-job import): the prior implementation ran four separate
    -- COUNT(*) queries here (one per category, plus a COUNT(DISTINCT
    -- stream_id) on wh_perspective_events). Under import load with millions
    -- of leased rows per instance, those counts dominated claim_work hold
    -- time — ClaimWorkAsync at p99 5031 ms / avg 128 ms. We don't need
    -- exact counts; we only need to know whether ANY category filled its
    -- LIMIT. ROW_COUNT after each RETURN QUERY gives us that for free.
    IF v_outbox_rows = p_max_streams
       OR v_inbox_rows = p_max_streams
       OR v_receptor_rows = p_max_streams
       OR v_perspective_rows = p_max_streams THEN
      RAISE NOTICE 'whizbang.has_more=true';
    END IF;
  END;

  RETURN;
END;
-- 157: custom plans for the poll and everything it calls. The queue tables are empty between loads,
-- and a session that polled while they were empty kept generic plans made for empty tables; once
-- the tables filled those plans scanned them whole, nested, on every poll, until the next analyze
-- invalidated them. Measured: a poll that takes well under a second with fresh plans did not finish
-- inside the command timeout with the empty-table plans. Planning per call costs milliseconds.
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.claim_work(UUID, TEXT, TEXT, INTEGER, INTEGER, INTEGER, INTEGER, DOUBLE PRECISION, INTEGER, BOOLEAN, INTEGER) IS
  'Leases work for an instance and re-offers the streams it holds (145: bounded acquisition, command lane, row bound, stealing). 150: the re-offered inbox and perspective streams are ordered most urgent bucket first. 158: every re-offer is bounded by the batch it returns rather than by the holdings -- a held stream is re-offered as its oldest row in each lane it appears in, not as every row it holds there -- and the inbox chain reads only rows it has not stamped.';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_perspective_events: last word 148_ActiveStreamLeases.sql; the most urgent streams are selected first.
-- ---------------------------------------------------------------------------------------------
-- 160: the signature gains p_max_rows, so the old five-argument overload has to go first or the
-- initializer's duplicate-overload sweep force-replays this file on every start.
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_perspective_events');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_perspective_events(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_max_streams INTEGER DEFAULT 500,
  p_instance_rank INTEGER DEFAULT 0,
  p_active_instance_count INTEGER DEFAULT 1,
  -- 160: rows this call may lease, as p_max_streams bounds the streams it may take. Without it the
  -- claim selected a batch of STREAMS and then leased every pending event of each, so a poll cost
  -- what a consumer's streams happened to hold: 3,730 blocks a call over streams twelve events
  -- deep and 54,914 over streams two hundred deep, for the same batch. NULL keeps the old
  -- unbounded behavior for a caller that has not been taught to pass one; claim_work passes its own
  -- row budget. 145 (#714) is the warning against the other mistake, handing a STREAM count to an
  -- acquisition as a row cap and turning a fat stream into one row per cycle -- hence a multiple of
  -- the batch rather than the batch itself.
  p_max_rows INTEGER DEFAULT NULL
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200)
) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  -- 157: the window this poll chooses streams from, bounded by the batch. The most urgent
  -- claimable-looking events in (priority, event_id) order, walked from idx_perspective_event_urgency
  -- with an early stop, so the cost of choosing streams follows the batch and never the backlog.
  -- Aggregating every claimable event per poll, on every instance, was most of a saturated database's
  -- CPU under a bulk load. A stream whose head lies beyond the window is by definition less urgent
  -- than every stream selected from it, and a later poll sees it. Ownership and per-stream ordering
  -- are decided on the window below, and a selected stream is still captured in full.
  WITH head AS (
    SELECT
      pe.event_work_id,
      pe.stream_id,
      pe.perspective_name,
      pe.event_id,
      pe.partition_number,
      pe.priority
    FROM __SCHEMA__.wh_perspective_events pe
    WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND pe.processed_at IS NULL
    ORDER BY pe.priority, pe.event_id
    LIMIT GREATEST(p_max_streams, 1) * 8
  ),
  claimable_events AS (
    -- The events of the window this instance may claim (orphaned or unleased, owned or unowned).
    SELECT
      pe.event_work_id,
      pe.stream_id,
      pe.perspective_name,
      pe.event_id,
      pe.partition_number,
      pe.priority
    FROM head pe
    WHERE TRUE
      -- Phase H step 6 slice 2: stream ownership now combines wh_active_streams pinning
      -- (OWNER PATH — always wins) with partition-modulo load balancing for unowned streams
      -- (UNOWNED PATH — symmetric with claim_orphaned_outbox / _inbox).
      AND (
        -- OWNER PATH — wh_active_streams says this instance owns the stream. Always claim.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = pe.stream_id
            AND ast.assigned_instance_id = p_instance_id
        )
        OR
        -- UNOWNED / ABANDONED PATH — partition-modulo selection for streams with no live owner.
        (
          (pe.partition_number % p_active_instance_count) = p_instance_rank
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = pe.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND (
                -- Existing check: a row in wh_service_instances counts as alive.
                -- This is removed by cleanup_stale_instances at the stale threshold.
                EXISTS (
                  SELECT 1 FROM __SCHEMA__.wh_service_instances si
                  WHERE si.instance_id = ast.assigned_instance_id
                )
                -- Slice 2b of zero-idle-polling — additive defensive predicate:
                -- a pod with a live Whizbang LISTEN connection in pg_stat_activity
                -- counts as alive even if its wh_service_instances row has been
                -- cleaned up (transient state during pod restart, race between
                -- cleanup_stale_instances and the pod opening its LISTEN
                -- connection on next boot). Strictly additive — only adds
                -- protection against premature orphan-claim, never loosens.
                OR EXISTS (
                  SELECT 1 FROM pg_stat_activity sa
                  WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || ast.assigned_instance_id::text
                )
              )
          )
        )
      )
      -- Ensure ordering - no earlier uncompleted events in same perspective
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
        WHERE earlier.stream_id = pe.stream_id
          AND earlier.perspective_name = pe.perspective_name
          AND earlier.event_id < pe.event_id
          AND (
            (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
            OR (earlier.scheduled_for > p_now)
          )
      )
  ),
  -- Select up to p_max_streams distinct streams from claimable events
  -- 150: the most urgent streams first (the fold is the stream's most urgent claimable event), then the oldest.
  selected_streams AS (
    SELECT ce.stream_id
    FROM claimable_events ce
    GROUP BY ce.stream_id
    ORDER BY MIN(ce.priority), MIN(ce.event_id::TEXT)   -- no min(uuid); a v7 id orders chronologically as text
    LIMIT p_max_streams
  ),
  -- 140: lock the selected streams' rows with SKIP LOCKED before leasing them (issue #699), the
  -- shape claim_orphaned_inbox and claim_orphaned_outbox already use. A row another session holds
  -- is a row being completed or leased; waiting for it stalled the whole claim tick behind one
  -- commit batch. The lock is scoped to the selected streams, never the full eligible backlog.
  -- 157: full-stream capture reads the selected streams' rows through idx_perspective_event_order,
  -- not the window, so a stream still drains in one lease as before; the same ordering predicate
  -- keeps an event behind a leased or scheduled earlier one out of the claim.
  -- 160: the row bound has to be reached BEFORE the per-row work, not after it. Bounding only the
  -- final result still sorted every claimable row of every selected stream and ran the ordering
  -- probe below on each one, so leasing fell to a handful of rows a call while the poll still cost
  -- what the streams held: 8,352 blocks a call at depth against 1,523 shallow, for 6.7 rows leased.
  -- Each stream is walked separately instead, oldest first through idx_perspective_event_order,
  -- and stops at the bound on its own. Work is then bounded by the batch times the bound rather
  -- than by the depth of whatever streams the batch happened to select, and a deep stream can still
  -- contribute the whole bound rather than being rationed to one row a cycle (145, #714).
  candidate_events AS (
    SELECT c.event_work_id, c.event_id
    FROM selected_streams ss
    CROSS JOIN LATERAL (
      SELECT pe.event_work_id, pe.event_id
      FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.stream_id = ss.stream_id
        AND (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
        AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
        AND pe.processed_at IS NULL
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
          WHERE earlier.stream_id = pe.stream_id
            AND earlier.perspective_name = pe.perspective_name
            AND earlier.event_id < pe.event_id
            AND (
              (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
              OR (earlier.scheduled_for > p_now)
            )
        )
      ORDER BY pe.event_id
      LIMIT COALESCE(p_max_rows, 2147483647)
    ) c
  ),
  locked AS (
    -- Oldest first across the selected streams, and stop at the row bound. event_id is a v7 id, so
    -- this is arrival order, and per-stream order falls out of it: an event is only reached after
    -- every earlier event of its own stream, so a stream is captured from its head and never with a
    -- hole. A stream deeper than the bound is captured over consecutive polls, which is what the
    -- inbox has done since 145; the drain is per stream with an unbounded channel and the re-offer
    -- hands the stream back each poll, so the remainder is picked up rather than stranded.
    -- Full-stream capture in one lease was never a correctness property, only the previous cost of
    -- not bounding this.
    SELECT pe.event_work_id
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN candidate_events ce ON ce.event_work_id = pe.event_work_id
    WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND pe.processed_at IS NULL
    ORDER BY ce.event_id
    LIMIT COALESCE(p_max_rows, 2147483647)
    FOR UPDATE OF pe SKIP LOCKED
  ),
  -- Claim the locked events for the selected streams, oldest first, up to the row bound (160).
  claimed AS (
    UPDATE __SCHEMA__.wh_perspective_events pe
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = pe.attempts + 1
    FROM locked l
    WHERE pe.event_work_id = l.event_work_id
    RETURNING pe.event_work_id AS c_event_work_id, pe.stream_id AS c_stream_id, pe.perspective_name AS c_perspective_name, pe.partition_number AS c_partition_number
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into REFRESH (row-only
  -- UPDATE for already-owned-with-live-lease streams) + PIN (INSERT...ON CONFLICT for
  -- the rare ownership-transition case, with ORDER BY stream_id for consistent lock
  -- acquisition). Symmetric with the fix in claim_orphaned_outbox (mig 024); see that
  -- migration for the full rationale. Eliminates the 40P01 deadlock observed in
  -- production (Whizbang PR #227).
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, c.c_partition_number AS partition_number
      FROM claimed c
      WHERE NOT EXISTS (
        SELECT 1 FROM refreshed r WHERE r.refreshed_stream_id = c.c_stream_id
      )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO UPDATE
      SET last_activity_at = EXCLUDED.last_activity_at,
          assigned_instance_id = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.assigned_instance_id
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.assigned_instance_id
            ELSE ast.assigned_instance_id
          END,
          -- 148 (#731): the stream lease follows the assignment. Whoever the CASE above leaves as owner
          -- holds the lease: a stream this instance takes (unowned, orphaned by a deregistered instance,
          -- or already its own) is leased to p_lease_expiry; a stream another registered instance keeps
          -- is not touched.
          lease_expiry = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.lease_expiry
            WHEN ast.assigned_instance_id = EXCLUDED.assigned_instance_id THEN EXCLUDED.lease_expiry
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.lease_expiry
            ELSE ast.lease_expiry
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_event_work_id AS event_work_id, c.c_stream_id AS stream_id, c.c_perspective_name AS perspective_name FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_perspective_events(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER, INTEGER) IS
  'Acquires unowned/abandoned pending perspective events for an instance, by stream (027 ownership, 140 per-stream gate, 148 stream leases). 150: the streams with the most urgent claimable event are selected first, oldest first within a priority. 160: bounded by ROWS as well as by streams -- it used to take a batch of streams and lease every pending event of each, so one call cost whatever a consumer''s streams happened to hold; a stream deeper than the bound is now captured from its head over consecutive polls.';
