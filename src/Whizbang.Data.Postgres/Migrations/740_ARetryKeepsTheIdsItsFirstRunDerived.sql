-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 740_ARetryKeepsTheIdsItsFirstRunDerived.sql
-- Date: 2026-11-27
-- Description: An inbox row records the derivation scheme its first run ran under, and every retry runs under it (#2633).
--
--              A receptor a handling invokes locally now runs in a handling of its own, so the ids it derives, sends
--              and emits move once, and so do the outer body's send ordinals after the call. A row that started on a
--              build before that change and is retried on one after it must keep its first run's ids, or the retry's
--              copies are new messages instead of duplicates the stores absorb.
--
--                wh_inbox_state.derivation_scheme  SMALLINT, NULL until a row's first run records it: 0 is the scheme
--                                 of every build before #2633 (the inner receptor shares the caller's handling), 1 the
--                                 inner receptor's own handling. Nullable with no default, so adding it rewrites nothing.
--                fetch_inbox_batch_with_derivation_scheme  fetch_inbox_batch_in_stream_order (576) verbatim, with the
--                                 calling build's scheme as a new last parameter. It stamps a returned row on its first
--                                 run (attempts at most 1, nothing recorded) and returns each row's recorded scheme, 0
--                                 for a row that a build before the column ran first. A new name, because a function's
--                                 result never changes under its name (454); 576's function stays for a build before this
--                                 one, which never records a scheme, so its rows read as 0 on a later retry.
--
--              The stamp is one more update of a row's work state, on its first fetch only. A build before this one
--              that retries a row a later build first ran derives its own build's ids: a build cannot honor a scheme
--              it does not know.
-- Dependencies: 162 (wh_inbox_state), 576 (fetch_inbox_batch_in_stream_order)
-- Objects: wh_inbox_state.derivation_scheme, fetch_inbox_batch_with_derivation_scheme

ALTER TABLE __SCHEMA__.wh_inbox_state ADD COLUMN IF NOT EXISTS derivation_scheme SMALLINT;

COMMENT ON COLUMN __SCHEMA__.wh_inbox_state.derivation_scheme IS
'740 (#2633): the derivation scheme the row''s first run ran under, which every retry runs under: 0 a locally invoked receptor shares its caller''s handling (every build before #2633), 1 it runs in a handling of its own. NULL until the first run records it; a row a build before the column ran first reads as 0.';

-- <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchDerivationSchemeSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchStreamOrderSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_inbox_batch_with_derivation_scheme(
  p_stream_ids UUID[],
  p_instance_id UUID,
  p_max_per_stream INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL,
  p_derivation_scheme SMALLINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  handler_name VARCHAR(200),
  message_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  error TEXT,
  priority INTEGER,
  stream_position BIGINT,
  derivation_scheme SMALLINT
) AS $$
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- 740 (#2633): fetch_inbox_batch_in_stream_order (576) verbatim, returning the scheme each row first ran under and
  -- recording it on a row's first run (see stamped below).
  --
  -- 576 (#2551): a stream's rows are ranked in its stream order, never by id: its events by stored version (the receive
  -- order 574 stored them in), its commands by arrival, each kind dealt into the arrival slots it holds. The rank is
  -- returned as stream_position, and the drain dispatches by it.
  --
  -- 162: this function needs both tables, because it returns the message and selects on its claim
  -- state. The LEASE table drives the join: every predicate below is on it, so the narrow table is
  -- scanned and the wide row is fetched only for the rows that survive. The previous form scanned
  -- the wide row to apply the same predicate. i.* is enumerated rather than starred because the
  -- moved columns are no longer on wh_inbox and a star would silently change the result shape.
  RETURN QUERY
  -- Materialized, so each row's stored version is probed once however often the ranking below reads it.
  WITH candidates AS MATERIALIZED (
    SELECT
      i.message_id,
      ist.stream_id,
      i.handler_name,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      ist.status,
      ist.attempts,
      ist.partition_number,
      ist.is_event,
      ist.error,
      i.priority,
      i.arrival_position,
      ist.derivation_scheme AS recorded_scheme,
      -- An event's stored version: one primary-key probe per leased row. A command has none.
      (SELECT es.version FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id) AS stored_version
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    -- v0.658 slice 7: mirror of fetch_outbox_batch's Empty/NULL stream handling -- see the matching
    -- comment in the outbox query for the full rationale.
    WHERE (
        ist.stream_id = ANY(p_stream_ids)
        OR ((ist.stream_id IS NULL OR ist.stream_id = __EMPTY_UUID__::uuid)
            AND ist.message_id = ANY(p_stream_ids))
      )
      AND ist.instance_id = p_instance_id
      AND ist.lease_expiry > NOW()
      AND ist.processed_at IS NULL  -- inbox uses processed_at as both production-marker and debug-kept-marker
      -- 230 (#1607): an event is dispatched once it is stored. The emit chain stores a leased event in the claim
      -- that leased it, unless the event waits behind its stream's head; such a row stays leased and unstored,
      -- and completing it would delete it before it ever reached the event store.
      AND (ist.chain_emitted_at IS NOT NULL OR ist.is_event = false OR ist.stream_id IS NULL)
      AND (ist.scheduled_for IS NULL OR ist.scheduled_for <= NOW())
  ),
  -- Each kind's rank in its own order, and the arrival slots each kind holds.
  kinds AS (
    SELECT c.*,
           (c.stored_version IS NOT NULL) AS versioned,
           row_number() OVER (PARTITION BY c.stream_id ORDER BY c.arrival_position) AS slot,
           row_number() OVER (PARTITION BY c.stream_id, (c.stored_version IS NOT NULL)
                              ORDER BY c.stored_version, c.arrival_position) AS kind_rank
    FROM candidates c
  ),
  -- The row of rank n in its kind takes its kind's n-th slot. Both sides are numbered over the batch in (stream, kind,
  -- rank) order, so they meet on one key.
  by_kind AS (
    SELECT k.*, row_number() OVER (ORDER BY k.stream_id, k.versioned, k.kind_rank) AS pair FROM kinds k
  ),
  slots AS (
    SELECT k.slot, row_number() OVER (ORDER BY k.stream_id, k.versioned, k.slot) AS pair FROM kinds k
  ),
  ranked AS (
    SELECT r.*, s.slot AS rank_in_stream
    FROM by_kind r
    JOIN slots s ON s.pair = r.pair
  ),
  -- Running byte total in the SAME order the rows are returned, so the cut is a suffix of the
  -- slice and stream-FIFO is preserved. Measured on the payload columns because those are what
  -- cross the wire and land on the heap; the fixed-width columns are noise by comparison.
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id ORDER BY r.rank_in_stream
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
    WHERE r.rank_in_stream <= p_max_per_stream
  ),
  -- 740 (#2633): the rows this fetch returns, the only ones it may stamp: a row cut by the budget is not run now.
  chosen AS MATERIALIZED (
    SELECT b.*
    FROM budgeted b
    WHERE p_max_bytes IS NULL
       OR b.rank_in_stream = 1              -- never starve a stream on an oversized head message
       OR b.running_bytes <= p_max_bytes
  ),
  -- 740 (#2633): a row's first run records the scheme it runs under. The claim bumps attempts on every claim, so a row
  -- at attempts 1 (0 only before a claim) is on its first run, and one above it was run before: by a build before the
  -- column when it records nothing, which is the shared scheme (0). A recorded scheme is never overwritten.
  stamped AS (
    UPDATE __SCHEMA__.wh_inbox_state ist
    SET derivation_scheme = p_derivation_scheme
    FROM chosen c
    WHERE ist.message_id = c.message_id
      AND p_derivation_scheme IS NOT NULL
      AND c.recorded_scheme IS NULL
      AND c.attempts <= 1
    RETURNING ist.message_id AS stamped_message_id
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.handler_name::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.error,
    b.priority,
    b.rank_in_stream,
    (CASE WHEN s.stamped_message_id IS NOT NULL THEN p_derivation_scheme ELSE COALESCE(b.recorded_scheme, 0) END)::SMALLINT
  FROM chosen b
  LEFT JOIN stamped s ON s.stamped_message_id = b.message_id
  ORDER BY b.stream_id, b.rank_in_stream;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_inbox_batch_with_derivation_scheme(UUID[], UUID, INTEGER, BIGINT, SMALLINT) IS
  '740 (#2633): fetch_inbox_batch_in_stream_order (576) returning the derivation scheme each row first ran under, and recording p_derivation_scheme on a returned row''s first run (attempts at most 1, nothing recorded). A row a build before the column ran first returns 0. A new name, because a function''s result never changes under its name (migration 454).';
