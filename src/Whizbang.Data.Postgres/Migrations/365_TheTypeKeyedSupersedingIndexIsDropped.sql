-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 365_TheTypeKeyedSupersedingIndexIsDropped.sql
-- Date: 2026-11-08
-- Description: Drops idx_event_store_superseding (295): idx_event_store_superseding_group (352) replaced it, and
--              nothing reads it any more (#2166).
--
--              295 keyed the superseding probe on (stream_id, event_type, supersede_key, version). Since 352 (#2124)
--              every probe matches on COALESCE(supersede_group, event_type) instead: the emit queue
--              (_wh_queue_superseded_events), the reaper's newest check (wh_reap_superseded_events, last word 358) and
--              the history reconcile. The group index is keyed on that expression and serves all of them, ungrouped
--              rows included, because an ungrouped row's expression is its event type. The type-keyed index can
--              answer those probes only by stream, so the planner never chooses it, while every keyed write into
--              wh_event_store still maintains it.
--
--              A plain DROP INDEX inside the migration's transaction, as every earlier index retirement does (164,
--              171, 320): dropping is a catalog change and a file unlink, not a scan, so its lock is held only for
--              the drop itself. The concurrent path (ConcurrentIndexBuild) exists for builds, which read the whole
--              table.
--
--              The comments in 295, 296, 336 and 352 that name the old index stay as written: those migrations have
--              shipped, and no function is redefined here to carry a corrected copy.
--
--              Number 365: 359 to 364 are taken by changes in flight.
--
-- Dependencies: 295 (created idx_event_store_superseding), 352 (idx_event_store_superseding_group, the replacement)
-- Objects: idx_event_store_superseding

DROP INDEX IF EXISTS __SCHEMA__.idx_event_store_superseding;
