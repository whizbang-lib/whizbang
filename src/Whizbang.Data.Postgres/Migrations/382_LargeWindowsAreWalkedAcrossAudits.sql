-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 382_LargeWindowsAreWalkedAcrossAudits.sql
-- Date: 2026-11-09
-- Description: The stream-level walk of an audit window wider than the consumer's page budget resumes, at the next
--              audit, where the last walk stopped (#2211).
--
--              A type-level comparison that disagrees drills down to a stream-level walk of the window: the origin
--              answers its streams in stream-id order, a page at a time (MaxDigestsPerManifest), and the consumer
--              follows at most MaxManifestPagesPerAudit pages. The walk used to drop its cursor at the budget and the
--              next audit's drill-down started from the first stream again, so in a window with more streams than the
--              budget covers (a type whose every event is its own stream) the same leading streams were compared and
--              repaired at every audit, and the streams after them never were.
--
--              1. wh_integrity_drill_cursors (new): per (origin, window start), the last stream a walk stopped by the
--                 budget compared. The consumer keeps it when the budget stops a walk and takes it (reads and deletes
--                 it) when it next drills down that window, so a cursor is used once: a walk that reaches the window's
--                 last stream, or is dropped on the way, leaves none, and the next drill-down starts from the first
--                 stream. Every stream of the window is reached in turn.
--
--              The table holds at most one row per window being walked; keeping a cursor deletes the origin's cursors
--              of windows below its seal (wh_integrity_seals), which are never audited again. The coordinator reads and
--              writes it by primary key; no further index.
--
--              Number 382: 376 to 381 are taken on develop or by changes in flight.
--
-- Dependencies: 092 (wh_integrity_seals, read when a cursor is kept)
-- Objects: wh_integrity_drill_cursors
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_drill_cursors (
  origin_service_id      UUID        NOT NULL,
  window_since           BIGINT      NOT NULL,   -- the window's first commit sequence, which names it across audits
  resume_after_stream_id UUID        NOT NULL,   -- the last stream the stopped walk compared
  saved_at               TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (origin_service_id, window_since)
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_drill_cursors IS
  '382 (#2211): where the stream-level walk of an audit window stopped at the consumer''s page budget, so the next '
  'drill-down of the window resumes after it instead of comparing the same leading streams again. Taken once.';
