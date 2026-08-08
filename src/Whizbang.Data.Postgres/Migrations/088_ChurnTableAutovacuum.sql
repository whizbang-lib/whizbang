-- Migration: 088_ChurnTableAutovacuum.sql
-- Description: Aggressive per-table autovacuum for the delete-churned messaging tables, so dead
--              tuples are reclaimed promptly and the heap settles into a bounded steady state
--              instead of growing to cover every burst.
-- Dependencies: the tables below (created in 001-087). Settings only; no structural change.

-- PostgreSQL's default autovacuum_vacuum_scale_factor of 0.2 vacuums a table only once a fifth of
-- it is dead. On a queue table that is inserted and deleted continuously that is far too slow: a
-- burst outruns autovacuum, the heap grows to cover the peak, and it never comes back down. The
-- dead space is reusable, but the pages are still real and a sequential scan still reads every one
-- of them -- a table can hold a handful of live rows and take seconds to count because the scan
-- walks the empty pages left behind by a burst that has long since drained.
--
-- Dropping the scale factor to 0.02 recycles that space roughly an order of magnitude sooner, so
-- the steady state stays bounded. The analyze factor moves with it: after a burst drains, stats
-- claiming the old row count would keep the planner choosing sequential scans over the now-tiny
-- table.
--
-- This is prevention, not cure. Plain (auto)vacuum never returns pages to the operating system, so
-- a table that has ALREADY bloated still needs a rewrite -- VACUUM FULL or pg_repack, both of which
-- take an exclusive lock and so belong in an operator runbook rather than an automatic maintenance
-- step. These settings are what stop a table getting there in the first place.
--
-- Deliberately NOT tuned: append-mostly tables. wh_dead_letters is forensic (rows accumulate and
-- are read, not deleted on the hot path) and wh_event_store keeps its pointers by design, so a
-- tighter vacuum threshold would buy nothing and only add scan cost.

-- Inbound and outbound queues: rows are deleted on successful completion, so churn tracks
-- throughput exactly. These are the two that bloat first and worst under a message storm.
ALTER TABLE __SCHEMA__.wh_inbox SET (
  autovacuum_vacuum_scale_factor = 0.02,
  autovacuum_analyze_scale_factor = 0.02
);

ALTER TABLE __SCHEMA__.wh_outbox SET (
  autovacuum_vacuum_scale_factor = 0.02,
  autovacuum_analyze_scale_factor = 0.02
);

-- Perspective work items: one row per (event, consuming perspective), deleted once processed.
-- Fans out with the number of perspectives, so it churns faster than the queues that feed it.
ALTER TABLE __SCHEMA__.wh_perspective_events SET (
  autovacuum_vacuum_scale_factor = 0.02,
  autovacuum_analyze_scale_factor = 0.02
);

-- Dedup window: every delivered message inserts a row, and the retention sweep deletes it once the
-- window lapses. Pure insert-then-delete churn with a fixed-size working set.
ALTER TABLE __SCHEMA__.wh_message_deduplication SET (
  autovacuum_vacuum_scale_factor = 0.02,
  autovacuum_analyze_scale_factor = 0.02
);

-- Stream affinity: rows come and go as streams become active and are cleaned up when their work
-- completes. Small but hot -- it is read on the claim path, so stale statistics here are costly.
ALTER TABLE __SCHEMA__.wh_active_streams SET (
  autovacuum_vacuum_scale_factor = 0.02,
  autovacuum_analyze_scale_factor = 0.02
);
