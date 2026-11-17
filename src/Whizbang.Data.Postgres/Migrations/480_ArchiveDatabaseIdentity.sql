-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 480_ArchiveDatabaseIdentity.sql
-- Date: 2026-11-17
-- Description: The database's archive identity, archive_database_id in wh_settings (#2367).
--
--              The Azure Blob archive store claims its container for one database with a create-only owner marker
--              object that holds this id, and its orphan sweep runs only when the marker names this database. A
--              database migrated on its own gets its own random id here; a restore or a copy of the database carries
--              the id it was taken from, so a database restored to its own container still matches its marker, and a
--              copy repointed to a new container claims that container. The id is written once and never changed.
--
--              Number 480: in the range assigned to this change.
--
-- Reversal: lossy (the id goes; applying 480 again gives the database a new id, and every container it claimed then
--           refuses its orphan sweep until the marker is replaced by an operator)
-- Dependencies: 028 (wh_settings)
-- Objects: wh_settings row archive_database_id
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
VALUES ('archive_database_id', gen_random_uuid()::text, 'string',
        '480 (#2367): this database''s archive identity. The Azure Blob archive store''s owner marker holds it, and the orphan sweep runs only for the database the marker names. Written once; never changed.')
ON CONFLICT (setting_key) DO NOTHING;
