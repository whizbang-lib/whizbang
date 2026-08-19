-- Migration: 116_PerDatabaseStampFence.sql
-- Date: 2026-08-19
-- Description: Scope the commit-order stamper's ordering fence to THIS database.
--
--              The 047 barrier used pg_snapshot_xmin(pg_current_snapshot()), which is
--              CLUSTER-wide: transaction IDs are global to the server, so any open write
--              transaction in ANY database (an unrelated service, another deployment
--              sharing the cluster, an idle-in-transaction session, a handler holding a
--              transaction across an external call) held the horizon back and froze
--              commit-sequence stamping here. Because get_stream_events (058) hides
--              events until they are stamped, that surfaced as multi-second
--              perspective-visibility stalls injected by workloads that cannot possibly
--              have written this database's wh_event_store.
--
--              The correctness requirement is xmin-monotonic commit_sequence assignment
--              PER DATABASE: never stamp a committed row while an older (lower-xmin)
--              transaction that could still commit rows INTO THIS TABLE is in flight.
--              Only backends connected to this database can write this database's
--              tables, so the fence is the oldest assigned xid among:
--                (a) backends connected to current_database() (pg_stat_activity), and
--                (b) prepared transactions targeting current_database()
--                    (pg_prepared_xacts — two-phase transactions leave pg_stat_activity
--                    but can still commit later).
--              With neither present, every visible unstamped row is stable —
--              pg_snapshot_xmax (first as-yet-unassigned xid) is a safe upper bound
--              because a snapshot cannot see rows from unassigned transactions.
--
--              Races considered: an xid assigned AFTER the horizon read is strictly
--              greater than every xid visible to this statement, so it cannot invert
--              the ordering of rows stamped in this call. The stamper's own transaction
--              is read-only until the UPDATE and is excluded by pid explicitly.
-- Dependencies: 046 (commit_sequence column, wh_commit_seq, idx_event_store_unstamped),
--               047 (original function this supersedes).

SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

CREATE OR REPLACE FUNCTION __SCHEMA__.stamp_pending_commit_sequences(
  p_batch_size INTEGER DEFAULT 1000
) RETURNS INTEGER AS $$
DECLARE
  v_stamped_count INTEGER;
  v_horizon xid8;
BEGIN
  -- Per-database ordering fence (see migration header). LEAST of the two same-database
  -- in-flight sources; either sub-select COALESCEs to the snapshot xmax ceiling when its
  -- source is empty.
  --
  -- Type note (unchanged from 047): xmin/backend_xid/transaction are 32-bit `xid`;
  -- comparisons run in 64-bit `xid8` via the text cast. Safe within a wraparound epoch;
  -- autovacuum freezing prevents wrap issues in practice.
  SELECT LEAST(
    COALESCE((SELECT min(sa.backend_xid::text::xid8)
              FROM pg_stat_activity sa
              WHERE sa.datname = current_database()
                AND sa.pid <> pg_backend_pid()
                AND sa.backend_xid IS NOT NULL),
             pg_snapshot_xmax(pg_current_snapshot())),
    COALESCE((SELECT min(px.transaction::text::xid8)
              FROM pg_prepared_xacts px
              WHERE px.database = current_database()),
             pg_snapshot_xmax(pg_current_snapshot()))
  ) INTO v_horizon;

  -- FOR UPDATE SKIP LOCKED + ORDER BY xmin: unchanged from 047 — concurrent callers
  -- partition the work, and lower xmin gets the lower commit_sequence within a batch,
  -- preserving the monotonic ordering downstream cursors rely on.
  WITH eligible AS (
    SELECT event_id, xmin::text::xid8 AS xmin8
    FROM __SCHEMA__.wh_event_store
    WHERE commit_sequence IS NULL
      AND xmin::text::xid8 < v_horizon
    ORDER BY xmin::text::xid8
    LIMIT p_batch_size
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_event_store es
  SET commit_sequence = nextval('__SCHEMA__.wh_commit_seq')
  FROM eligible e
  WHERE es.event_id = e.event_id;

  GET DIAGNOSTICS v_stamped_count = ROW_COUNT;
  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences IS
'116 supersedes 047 — stamps commit_sequence on event_store rows whose inserting tx is provably committed AND past the PER-DATABASE ordering fence: the oldest assigned xid among backends connected to current_database() plus prepared transactions targeting it, falling back to pg_snapshot_xmax when neither exists. Only same-database backends can write this table, so open transactions elsewhere on a shared cluster no longer stall stamping (the 047 pg_snapshot_xmin barrier was cluster-wide). Allocation, ordering (xmin order), and FOR UPDATE SKIP LOCKED concurrency semantics are unchanged from 047. Returns count of rows stamped.';
