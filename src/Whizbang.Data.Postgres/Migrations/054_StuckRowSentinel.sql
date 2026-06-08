-- Migration: 054_StuckRowSentinel.sql
-- Date: 2026-06-07 (v0.657 slice 5)
-- Description: Structural canary for the "row claimed but never drained" bug
--              class — independent of root cause. Adds partial indexes on
--              wh_outbox(attempts) and wh_inbox(attempts) gated on
--              `processed_at IS NULL AND attempts > 5` so the index stays
--              ~0-sized in steady state, plus find_stuck_outbox_rows /
--              find_stuck_inbox_rows SQL functions invoked by the maintenance
--              worker once per cycle to surface stuck rows as Warning logs.
-- Dependencies: wh_outbox, wh_inbox tables (created via EF Core entity migrations).
--
-- Slot-3 forensic context: a JDX BFF row sat stuck for 24 h with attempts at
-- 992, processed_at NULL, no DLQ promotion, no error, no log. v0.656 Debug
-- breadcrumbs surfaced the bug (by absence). This sentinel surfaces ANY row
-- exhibiting the same symptom — even when the root cause is entirely new.

-- ============================================================================
-- Partial indexes — cost-control mechanism
-- ============================================================================
-- The predicate `attempts > 5` keeps the index ~0-sized in steady state. Most
-- rows publish on the first or second claim attempt; the partial index only
-- materialises entries for rows that have failed at least 5 times, which is
-- a tiny set in healthy traffic. Postgres uses these indexes for queries
-- with predicates like `attempts > 10` because a partial-index predicate is
-- a superset of the query predicate when 5 < 10.
--
-- Without these indexes, find_stuck_*_rows would full-scan wh_outbox /
-- wh_inbox on every 10-min maintenance tick — at JDX scale (millions of
-- historical rows including processed=NOT NULL rows pre-cleanup) the
-- sentinel itself becomes a problem.

CREATE INDEX IF NOT EXISTS idx_outbox_stuck_sentinel
  ON __SCHEMA__.wh_outbox (attempts)
  WHERE processed_at IS NULL AND attempts > 5;

CREATE INDEX IF NOT EXISTS idx_inbox_stuck_sentinel
  ON __SCHEMA__.wh_inbox (attempts)
  WHERE processed_at IS NULL AND attempts > 5;

-- ============================================================================
-- find_stuck_outbox_rows — surface wh_outbox rows the drainer never reaches
-- ============================================================================
SELECT __SCHEMA__.drop_all_overloads('find_stuck_outbox_rows');

CREATE OR REPLACE FUNCTION __SCHEMA__.find_stuck_outbox_rows(
  p_max_attempts INTEGER,
  p_limit INTEGER
) RETURNS TABLE (
  message_id UUID,
  message_type TEXT,
  stream_id UUID,
  attempts INTEGER,
  claimed_since TIMESTAMPTZ
) LANGUAGE SQL STABLE AS $$
  SELECT o.message_id,
         o.message_type::TEXT,
         o.stream_id,
         o.attempts,
         o.created_at
  FROM __SCHEMA__.wh_outbox o
  WHERE o.attempts > p_max_attempts
    AND o.processed_at IS NULL
  ORDER BY o.attempts DESC, o.created_at ASC
  LIMIT p_limit;
$$;

COMMENT ON FUNCTION __SCHEMA__.find_stuck_outbox_rows IS
'Structural canary (v0.657 slice 5): returns wh_outbox rows whose attempts exceeds p_max_attempts AND have not been processed. Invoked by MaintenanceWorker once per maintenance tick (default 10 min); each row produces a Warning log so operators see the symptom independent of root cause. Uses partial index idx_outbox_stuck_sentinel for O(log N) cost on a ~0-sized index in steady state.';

-- ============================================================================
-- find_stuck_inbox_rows — mirror for wh_inbox
-- ============================================================================
SELECT __SCHEMA__.drop_all_overloads('find_stuck_inbox_rows');

CREATE OR REPLACE FUNCTION __SCHEMA__.find_stuck_inbox_rows(
  p_max_attempts INTEGER,
  p_limit INTEGER
) RETURNS TABLE (
  message_id UUID,
  message_type TEXT,
  stream_id UUID,
  attempts INTEGER,
  claimed_since TIMESTAMPTZ
) LANGUAGE SQL STABLE AS $$
  SELECT i.message_id,
         i.message_type::TEXT,
         i.stream_id,
         i.attempts,
         i.received_at
  FROM __SCHEMA__.wh_inbox i
  WHERE i.attempts > p_max_attempts
    AND i.processed_at IS NULL
  ORDER BY i.attempts DESC, i.received_at ASC
  LIMIT p_limit;
$$;

COMMENT ON FUNCTION __SCHEMA__.find_stuck_inbox_rows IS
'Structural canary (v0.657 slice 5): mirror of find_stuck_outbox_rows for wh_inbox. Returns rows whose attempts exceeds p_max_attempts AND have not been processed. Same maintenance-tick invocation pattern + partial-index cost model.';
