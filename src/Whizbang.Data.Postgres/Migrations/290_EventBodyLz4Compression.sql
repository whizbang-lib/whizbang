-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 290_EventBodyLz4Compression.sql
-- Date: 2026-11-01
-- Description: Event bodies compress with lz4 (#1728, WS-2 slice S11). toast_tuple_target on wh_event_body stays at
--              the server's value.
--
--              Measured first, on copies of wh_event_body loaded with the same generated rows (PostgreSQL 17, bodies of
--              about 128 bytes to 8 KB of JSON text with about 770 bytes of stored metadata; the numbers are on the docs
--              page, data/postgres, "Event body storage"):
--
--                - PostgreSQL considers compressing a row only once the whole row is over its 2 KB toast threshold, a
--                  compile-time constant. A row under it is stored as it came, whatever toast_tuple_target says: in
--                  every copy, at every target from 128 to the default, 0% of the rows under 2 KB were compressed.
--                - Lowering toast_tuple_target therefore changes only the rows already over 2 KB, and on them it moves
--                  values out of line into the TOAST table rather than compressing more: 1.7% to 2.7% less in total on
--                  a mix with 69% of rows under 2 KB, for 12% to 23% more buffers read per body read by id.
--                - lz4 on both columns, at the default target: 1.1% less in total, 26% faster to write and 11% faster
--                  to read every body than the server's default pglz.
--
--              So this file sets lz4 and leaves the target alone. Compressing the small rows is not a table setting;
--              it is the dictionary work (WS-7 K3). Only values written after this file are compressed with lz4: a
--              value keeps the method it was written with until the row is rewritten (WS-4).
--
--              Each column is altered only when it is not lz4 already, so a re-run takes no lock on the table. A server
--              built without lz4 keeps pglz and says so in a warning, rather than failing the migration pass, which is
--              the guard the perspective tables' column compression already uses.
--
--              Number 290: reserved for this change.
--
-- Dependencies: 072 (wh_event_body)
-- Objects: wh_event_body
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_event_body') AND attname = 'event_data'
               AND NOT attisdropped AND attcompression IS DISTINCT FROM 'l') THEN
    ALTER TABLE __SCHEMA__.wh_event_body ALTER COLUMN event_data SET COMPRESSION lz4;
  END IF;
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_event_body') AND attname = 'metadata'
               AND NOT attisdropped AND attcompression IS DISTINCT FROM 'l') THEN
    ALTER TABLE __SCHEMA__.wh_event_body ALTER COLUMN metadata SET COMPRESSION lz4;
  END IF;
EXCEPTION WHEN feature_not_supported THEN
  RAISE WARNING 'Whizbang: lz4 compression is not available on this server; wh_event_body keeps its compression';
END $$;
