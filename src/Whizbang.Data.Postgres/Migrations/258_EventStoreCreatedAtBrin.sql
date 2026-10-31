-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 258_EventStoreCreatedAtBrin.sql
-- Date: 2026-10-29
-- Description: The event store's created_at gets a BRIN index, the one candidate of four whose order and queries
--              qualify (#1745, WS-4 slice C4.11).
--
--              A BRIN index keeps one minimum and maximum per range of heap pages, so it narrows a range query only
--              as far as the table's physical order follows the column (pg_stats.correlation near 1). Each candidate
--              was seeded in its production write order and measured (BrinIndexCandidatesTests):
--
--                wh_event_store.created_at        near 1: rows are appended in time order, and the stamper's one
--                                                 non-HOT update per event writes the new version near the tail
--                wh_event_store.commit_sequence   near 1, but every reader walks it in order (a cursor's
--                                                 commit_sequence > n ORDER BY commit_sequence LIMIT k), which only
--                                                 the existing B-tree idx_event_store_commit_sequence can return
--                wh_log.logged_at,                near 1 while the table only grows, below 0.9 after one retention
--                wh_lifecycle_completions.        pass: rows leave from the oldest end, vacuum frees those pages and
--                  completed_at,                  the next rows fill them, so the order rotates. Their B-trees serve
--                wh_message_deduplication.        the retention delete at any order and stay; a partitioned wh_log or
--                  first_seen_at                  wh_lifecycle_completions drops whole partitions instead
--
--              The range query the index serves: the startup orphaned-lifecycle reconciliation
--              (OrphanedLifecycleEventsSql.Build) reads the last thirty minutes of events,
--              created_at >= window start, on every start. No index on wh_event_store leads with created_at for
--              every event (idx_event_store_ephemeral_reap is partial on ephemeral events), so the query read the
--              whole store. The BRIN index reads the ranges that hold the window: the store's tail.
--
--              Nothing is replaced. idx_event_store_ephemeral_reap stays: the reap needs its order and its INCLUDE
--              columns, which a BRIN index cannot give.
--
--              pages_per_range is the default 128, written out so the choice is visible: one range is 1 MB of
--              heap, so the index is a few pages per gigabyte of store and the window reads a few ranges past what
--              it needs. autosummarize stays off: the window is the tail, which is the unsummarized part and is
--              always read, and vacuum (which the stamper's updates keep running on this table) summarizes the
--              ranges behind it. From PostgreSQL 16 a BRIN index does not stop a heap-only update.
--
--              Built inside the migration's transaction, like 155 and 195. A BRIN build is one sequential read of
--              the table with no sort, the cheapest index build there is, and the first start of this release pays
--              it once.
--
-- Dependencies: wh_event_store (the infrastructure schema, applied before the migrations)
-- Objects: idx_event_store_created_at_brin

CREATE INDEX IF NOT EXISTS idx_event_store_created_at_brin
  ON __SCHEMA__.wh_event_store USING brin (created_at) WITH (pages_per_range = 128);

COMMENT ON INDEX __SCHEMA__.idx_event_store_created_at_brin IS
  'Serves the startup orphaned-lifecycle reconciliation''s window (created_at >= start, the last thirty minutes) '
  'over a store that keeps every event (258, #1745). BRIN because created_at follows the heap order '
  '(correlation near 1): one summary per 128 pages instead of one entry per event.';
