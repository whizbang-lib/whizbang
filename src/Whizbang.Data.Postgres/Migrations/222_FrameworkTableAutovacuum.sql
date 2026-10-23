-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 222_FrameworkTableAutovacuum.sql
-- Date: 2026-10-21
-- Description: The large framework tables vacuum after a fixed number of changes rather than a fraction of the table
--              (#1582, WS-4 slice C2).
--
--              088 tuned the delete-churned queues with a 2% scale factor. A scale factor still grows with the table:
--              on a table of millions of rows even 2% is tens of thousands of dead rows, and the server default (20%,
--              5% on some managed servers) is hundreds of thousands. These tables grow with the data, so their
--              vacuum waits on a fixed count instead: scale factor 0 and a threshold of 10000.
--
--                wh_perspective_cursors, wh_lifecycle_completions, wh_stream_digests  updated or deleted continuously
--                wh_message_deduplication  every delivery inserts a row and retention deletes it; supersedes 088's 2%
--                wh_event_store            append-heavy: the insert threshold keeps the visibility map current, so
--                                          index-only scans stay index-only. 088 left this table alone because its
--                                          rows are not deleted; that reason does not cover inserts.
--                wh_event_body (TOAST)     reaped ephemeral bodies leave their dead chunks in the TOAST table
--
--              The analyze scale factors are left as they are. Storage parameters only; re-applying sets the same
--              values.
--
--              Number 222: 221 is the ledger retention change (#1572).
--
-- Dependencies: 035 (wh_lifecycle_completions), 072 (wh_event_body), 087 (wh_stream_digests), 088 (the queue tables'
--               settings this supersedes for wh_message_deduplication)
-- Objects: reloptions of wh_perspective_cursors, wh_lifecycle_completions, wh_stream_digests, wh_message_deduplication,
--          wh_event_store and wh_event_body's TOAST table
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_perspective_cursors SET (
  autovacuum_vacuum_scale_factor = 0,
  autovacuum_vacuum_threshold = 10000
);

ALTER TABLE __SCHEMA__.wh_lifecycle_completions SET (
  autovacuum_vacuum_scale_factor = 0,
  autovacuum_vacuum_threshold = 10000
);

ALTER TABLE __SCHEMA__.wh_stream_digests SET (
  autovacuum_vacuum_scale_factor = 0,
  autovacuum_vacuum_threshold = 10000
);

ALTER TABLE __SCHEMA__.wh_message_deduplication SET (
  autovacuum_vacuum_scale_factor = 0,
  autovacuum_vacuum_threshold = 10000
);

ALTER TABLE __SCHEMA__.wh_event_store SET (
  autovacuum_vacuum_insert_scale_factor = 0,
  autovacuum_vacuum_insert_threshold = 10000
);

ALTER TABLE __SCHEMA__.wh_event_body SET (
  toast.autovacuum_vacuum_scale_factor = 0,
  toast.autovacuum_vacuum_threshold = 10000
);
