-- Migration: 155_DigestEpochLaneIndex.sql
-- Date: 2026-09-16
-- Description: The digest-epoch lane probes are served by an index.
--
--              close_digest_epochs and verify_digest_epochs (092) probe each foreign lane with
--              origin_service_id = X AND origin_commit_sequence >= .. AND < .. AND created_at >= ..
--              No index covered those columns, so every probe was a parallel sequential scan of
--              the whole event store, about twenty per maintenance tick. Under a bulk load the
--              closure occupied two database backends scanning a multi-million-row table for the
--              length of the load, on top of the load itself.
--
--              Partial on rows that have a lane: the own-lane probes filter on commit_sequence and
--              are served by idx_event_store_commit_sequence, and those rows would only widen this
--              index. created_at is the trailing column so the settle-window predicate is decided
--              inside the index rather than on the heap.
--
-- Dependencies: 093 (origin_service_id, origin_commit_sequence on wh_event_store)
-- Objects: idx_event_store_origin_lane

CREATE INDEX IF NOT EXISTS idx_event_store_origin_lane
  ON __SCHEMA__.wh_event_store (origin_service_id, origin_commit_sequence, created_at)
  WHERE origin_service_id IS NOT NULL;
