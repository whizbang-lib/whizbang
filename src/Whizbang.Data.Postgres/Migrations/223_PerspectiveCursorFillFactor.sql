-- Migration: 223_PerspectiveCursorFillFactor.sql
-- Date: 2026-10-21
-- Description: wh_perspective_cursors keeps 10% of each page free for the new versions of its rows (#1592, WS-4
--              slice C3).
--
--              Every batch a perspective applies rewrites its cursor row. An update whose new version fits on the
--              same page and changes no indexed column is a heap-only (HOT) update: no index entries are written and
--              the old version is pruned inside the page without vacuum. A page packed full (the default fillfactor
--              of 100) leaves no room, so the new version lands on another page and every index gains an entry.
--              fillfactor 90 leaves that room.
--
--              A new fillfactor applies only to pages written from then on, so a table that already holds rows is
--              queued for a rewrite (wh_request_table_rewrite, 089), which the maintenance worker performs under the
--              operator's rewrite policy. Set and queued only when the value is not already 90, so a re-run changes
--              nothing and queues nothing.
--
--              Number 223: 222 is the framework-table autovacuum settings (#1582).
--
-- Dependencies: 089 (wh_request_table_rewrite), 222 (this table's autovacuum settings)
-- Objects: reloptions of wh_perspective_cursors
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = to_regclass('__SCHEMA__.wh_perspective_cursors')
                   AND 'fillfactor=90' = ANY (coalesce(reloptions, ARRAY[]::text[]))) THEN
    ALTER TABLE __SCHEMA__.wh_perspective_cursors SET (fillfactor = 90);
    IF pg_relation_size(to_regclass('__SCHEMA__.wh_perspective_cursors')) > 0 THEN
      PERFORM __SCHEMA__.wh_request_table_rewrite('wh_perspective_cursors');
    END IF;
  END IF;
END $$;
